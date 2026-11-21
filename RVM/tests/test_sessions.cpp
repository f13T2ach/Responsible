#include <cstdint>
#include <memory>
#include <span>
#include <string>
#include <utility>
#include <vector>

#include "channel/protobuf_codec.h"
#include "core/engine.h"
#include "runtime/backend.h"
#include "rvm/v1/control.pb.h"
#include "rvm/v1/rpc.pb.h"
#include "server/rpc_dispatcher.h"
#include "server/session_manager.h"
#include "test_registry.h"

using rvm::core::Engine;
using rvm::core::EngineConfig;
using rvm::environment::AvailabilityReport;
using rvm::environment::IAvailabilityProbe;
using rvm::environment::ProbeResult;
using rvm::environment::ProbeState;
using rvm::runtime::BackendKind;
using rvm::runtime::BackendProbe;
using rvm::runtime::EnvironmentFacts;
using rvm::runtime::EnvironmentPhase;
using rvm::runtime::EnvironmentSpec;
using rvm::runtime::IEnvironment;
using rvm::runtime::IEnvironmentBackend;
using rvm::server::DispatchResult;
using rvm::server::RpcDispatcher;
using rvm::server::SessionManager;
using rvm::v1::CreateSessionRequest;
using rvm::v1::RpcRequest;
using rvm::v1::RpcResponse;
using rvm::v1::Session;

namespace {

class FakeBackend;

class FakeEnvironment final : public IEnvironment {
 public:
  FakeEnvironment(FakeBackend& owner, EnvironmentSpec spec)
      : owner_(owner), spec_(std::move(spec)) {}

  rvm::core::Result<EnvironmentFacts> Start() override;
  rvm::core::Status Stop() override;
  rvm::core::Status Reset() override { return Stop(); }
  EnvironmentPhase phase() const override { return phase_; }

 private:
  FakeBackend& owner_;
  EnvironmentSpec spec_;
  EnvironmentPhase phase_ = EnvironmentPhase::kAbsent;
};

class FakeBackend final : public IEnvironmentBackend {
 public:
  bool fail_create = false;
  bool fail_start = false;
  int creates = 0;
  int starts = 0;
  int stops = 0;
  EnvironmentSpec last_spec;
  FakeEnvironment* last_environment = nullptr;

  BackendKind kind() const override { return BackendKind::kSandbox; }

  BackendProbe Probe() const override {
    BackendProbe probe;
    probe.ready = true;
    probe.detail = "fake";
    return probe;
  }

  rvm::core::Result<std::unique_ptr<IEnvironment>> Create(const EnvironmentSpec& spec) override {
    ++creates;
    last_spec = spec;
    if (fail_create) {
      return rvm::core::Error(rvm::core::ErrorCode::kHcsUnavailable, "fake create failure");
    }
    auto environment = std::make_unique<FakeEnvironment>(*this, spec);
    last_environment = environment.get();
    return std::unique_ptr<IEnvironment>(std::move(environment));
  }
};

rvm::core::Result<EnvironmentFacts> FakeEnvironment::Start() {
  ++owner_.starts;
  if (owner_.fail_start) {
    phase_ = EnvironmentPhase::kFailed;
    return rvm::core::Error(rvm::core::ErrorCode::kBackendFailed, "fake start failure");
  }
  phase_ = EnvironmentPhase::kReady;
  EnvironmentFacts facts;
  facts.windows_build = L"26200";
  facts.memory_mb = spec_.memory_mb;
  facts.vcpu_count = spec_.vcpu_count;
  facts.vbs_enabled = true;
  facts.hvci_enabled = true;
  facts.network_enabled = false;
  return facts;
}

rvm::core::Status FakeEnvironment::Stop() {
  ++owner_.stops;
  phase_ = EnvironmentPhase::kStopped;
  return rvm::core::Status::Ok();
}

AvailabilityReport AvailableReport() {
  AvailabilityReport report;
  report.appcontainer = ProbeResult{ProbeState::kOk, "ok"};
  report.winfsp = ProbeResult{ProbeState::kOk, "ok"};
  report.vbs = ProbeResult{ProbeState::kOk, "ok"};
  report.hvci = ProbeResult{ProbeState::kOk, "ok"};
  return report;
}

AvailabilityReport UnavailableReport() {
  AvailabilityReport report;
  report.appcontainer = ProbeResult{ProbeState::kFailed, "userenv.dll is missing"};
  return report;
}

class FakeProbe final : public IAvailabilityProbe {
 public:
  explicit FakeProbe(AvailabilityReport report) : report_(std::move(report)) {}
  AvailabilityReport Probe() const override { return report_; }

 private:
  AvailabilityReport report_;
};

std::unique_ptr<Engine> MakeEngine(AvailabilityReport report) {
  auto engine = std::make_unique<Engine>(EngineConfig{},
                                         std::make_unique<FakeProbe>(std::move(report)));
  RVM_CHECK(engine->Initialize().ok());
  return engine;
}

CreateSessionRequest WorkspaceRequest(const std::string& root = "C:\\work") {
  CreateSessionRequest request;
  request.set_client_id("responsible");
  request.set_workspace_root(root);
  request.mutable_policy()->set_mode(rvm::v1::SANDBOX_MODE_WORKSPACE_WRITE);
  request.mutable_policy()->set_approval(rvm::v1::APPROVAL_MODE_ASK);
  return request;
}

std::vector<std::uint8_t> EncodeRequest(const RpcRequest& request) {
  const auto encoded = rvm::channel::SerializeMessage(request, rvm::channel::kMaxPayloadBytes);
  RVM_CHECK(encoded.ok());
  return std::vector<std::uint8_t>(encoded.value().begin(), encoded.value().end());
}

RpcResponse DecodeResponse(const std::vector<std::uint8_t>& payload) {
  RpcResponse response;
  const auto parsed = rvm::channel::ParseMessage(
      std::span<const std::uint8_t>(payload.data(), payload.size()), response,
      rvm::channel::kMaxPayloadBytes);
  RVM_CHECK(parsed.ok());
  return response;
}

RpcRequest HelloRequest(std::uint64_t request_id, std::uint32_t pid) {
  RpcRequest request;
  request.set_protocol(rvm::v1::PROTOCOL_VERSION_1);
  request.set_request_id(request_id);
  request.mutable_hello()->set_requested_protocol(rvm::v1::PROTOCOL_VERSION_1);
  request.mutable_hello()->set_client_name("responsible-host");
  request.mutable_hello()->set_client_pid(pid);
  request.mutable_hello()->set_token("session-token");
  return request;
}

DispatchResult Dispatch(RpcDispatcher& dispatcher, const RpcRequest& request,
                        std::uint32_t peer_pid) {
  const std::vector<std::uint8_t> payload = EncodeRequest(request);
  const auto result = dispatcher.Handle(
      std::span<const std::uint8_t>(payload.data(), payload.size()), peer_pid, {},
      [] { return true; });
  RVM_CHECK(result.ok());
  return result.value();
}

}  // namespace

RVM_TEST(sessions_create_boots_the_backend_and_reports_facts) {
  FakeBackend backend;
  SessionManager sessions(backend, AvailableReport());

  CreateSessionRequest request = WorkspaceRequest();
  request.set_memory_mb(4096);
  request.set_vcpu_count(4);
  request.add_masked_patterns("*.secret");

  const auto created = sessions.Create(request);
  RVM_CHECK(created.ok());
  RVM_CHECK_EQ(created.value().session_id(), 1u);
  RVM_CHECK(created.value().state() == rvm::v1::SESSION_STATE_READY);
  RVM_CHECK(created.value().backend() == rvm::v1::BACKEND_KIND_SANDBOX);
  RVM_CHECK_EQ(created.value().workspace_root(), std::string("C:\\work"));
  RVM_CHECK_EQ(created.value().virtual_workspace_root(), std::string("C:\\RVM\\Workspace"));
  RVM_CHECK(created.value().created_unix_ms() > 0);
  RVM_CHECK_EQ(created.value().environment().windows_build(), std::string("26200"));
  RVM_CHECK(created.value().environment().vbs_enabled());
  RVM_CHECK_EQ(created.value().environment().memory_mb(), 4096u);

  RVM_CHECK_EQ(backend.creates, 1);
  RVM_CHECK_EQ(backend.starts, 1);
  RVM_CHECK_EQ(backend.last_spec.session_id, std::wstring(L"s1"));
  RVM_CHECK_EQ(backend.last_spec.memory_mb, 4096u);
  RVM_CHECK_EQ(backend.last_spec.masked_patterns.size(), 1u);
  RVM_CHECK(backend.last_spec.policy.mode == rvm::core::SandboxMode::kWorkspaceWrite);
  RVM_CHECK(!backend.last_spec.allow_base_layer_creation);

  const auto fetched = sessions.Get(1);
  RVM_CHECK(fetched.ok());
  RVM_CHECK(fetched.value().state() == rvm::v1::SESSION_STATE_READY);
  RVM_CHECK_EQ(sessions.List().size(), 1u);
}

RVM_TEST(sessions_create_is_rejected_when_rvm_is_unavailable) {
  FakeBackend backend;
  SessionManager sessions(backend, UnavailableReport());

  const auto created = sessions.Create(WorkspaceRequest());
  RVM_CHECK(!created.ok());
  RVM_CHECK(created.error().code == rvm::core::ErrorCode::kUnavailable);
  RVM_CHECK(created.error().detail.find("appcontainer") != std::string::npos);
  RVM_CHECK_EQ(backend.creates, 0);
}

RVM_TEST(sessions_create_validates_requests_before_touching_the_backend) {
  FakeBackend backend;
  SessionManager sessions(backend, AvailableReport());

  auto missing_workspace = WorkspaceRequest();
  missing_workspace.clear_workspace_root();
  const auto missing = sessions.Create(missing_workspace);
  RVM_CHECK(!missing.ok());
  RVM_CHECK(missing.error().code == rvm::core::ErrorCode::kInvalidArgument);

  auto no_policy = WorkspaceRequest();
  no_policy.clear_policy();
  const auto policy = sessions.Create(no_policy);
  RVM_CHECK(!policy.ok());
  RVM_CHECK(policy.error().code == rvm::core::ErrorCode::kProtocolError);

  auto tiny_memory = WorkspaceRequest();
  tiny_memory.set_memory_mb(64);
  const auto memory = sessions.Create(tiny_memory);
  RVM_CHECK(!memory.ok());
  RVM_CHECK(memory.error().code == rvm::core::ErrorCode::kInvalidArgument);

  auto too_many_vcpus = WorkspaceRequest();
  too_many_vcpus.set_vcpu_count(65);
  const auto vcpus = sessions.Create(too_many_vcpus);
  RVM_CHECK(!vcpus.ok());
  RVM_CHECK(vcpus.error().code == rvm::core::ErrorCode::kInvalidArgument);

  auto invalid_utf8 = WorkspaceRequest("\xC3\x28");
  const auto workspace = sessions.Create(invalid_utf8);
  RVM_CHECK(!workspace.ok());
  RVM_CHECK_EQ(backend.creates, 0);
}

RVM_TEST(sessions_create_rejects_backend_features_that_are_not_ready) {
  FakeBackend backend;
  SessionManager sessions(backend, AvailableReport());

  auto extra_root = WorkspaceRequest();
  extra_root.mutable_policy()->add_writable_roots("C:\\other");
  const auto roots = sessions.Create(extra_root);
  RVM_CHECK(!roots.ok());
  RVM_CHECK(roots.error().code == rvm::core::ErrorCode::kNotImplemented);

  auto danger = WorkspaceRequest();
  danger.mutable_policy()->set_mode(rvm::v1::SANDBOX_MODE_DANGER_FULL_ACCESS);
  const auto full = sessions.Create(danger);
  RVM_CHECK(!full.ok());
  RVM_CHECK(full.error().code == rvm::core::ErrorCode::kNotImplemented);
  RVM_CHECK_EQ(backend.creates, 0);
}

RVM_TEST(sessions_start_failure_releases_the_environment) {
  FakeBackend backend;
  backend.fail_start = true;
  SessionManager sessions(backend, AvailableReport());

  const auto created = sessions.Create(WorkspaceRequest());
  RVM_CHECK(!created.ok());
  RVM_CHECK(created.error().code == rvm::core::ErrorCode::kBackendFailed);
  RVM_CHECK_EQ(backend.starts, 1);
  RVM_CHECK_EQ(backend.stops, 1);
  RVM_CHECK(!sessions.Get(1).ok());
  RVM_CHECK(sessions.List().empty());
}

RVM_TEST(sessions_create_propagates_backend_create_errors) {
  FakeBackend backend;
  backend.fail_create = true;
  SessionManager sessions(backend, AvailableReport());

  const auto created = sessions.Create(WorkspaceRequest());
  RVM_CHECK(!created.ok());
  RVM_CHECK(created.error().code == rvm::core::ErrorCode::kHcsUnavailable);
  RVM_CHECK_EQ(backend.starts, 0);
}

RVM_TEST(sessions_close_is_idempotent_only_until_it_is_closed) {
  FakeBackend backend;
  SessionManager sessions(backend, AvailableReport());
  RVM_CHECK(sessions.Create(WorkspaceRequest()).ok());

  RVM_CHECK(sessions.Close(1, "done").ok());
  RVM_CHECK_EQ(backend.stops, 1);
  const auto closed = sessions.Get(1);
  RVM_CHECK(closed.ok());
  RVM_CHECK(closed.value().state() == rvm::v1::SESSION_STATE_CLOSED);

  const auto second = sessions.Close(1, "again");
  RVM_CHECK(!second.ok());
  RVM_CHECK(second.error().code == rvm::core::ErrorCode::kInvalidState);

  const auto missing = sessions.Close(99, "never-existed");
  RVM_CHECK(!missing.ok());
  RVM_CHECK(missing.error().code == rvm::core::ErrorCode::kSessionNotFound);
}

RVM_TEST(sessions_limit_live_environments) {
  FakeBackend backend;
  SessionManager sessions(backend, AvailableReport());
  for (int i = 0; i < 8; ++i) {
    RVM_CHECK(sessions.Create(WorkspaceRequest()).ok());
  }
  const auto rejected = sessions.Create(WorkspaceRequest());
  RVM_CHECK(!rejected.ok());
  RVM_CHECK(rejected.error().code == rvm::core::ErrorCode::kBusy);
  RVM_CHECK_EQ(backend.creates, 8);
}

RVM_TEST(sessions_shutdown_stops_every_live_environment) {
  FakeBackend backend;
  SessionManager sessions(backend, AvailableReport());
  RVM_CHECK(sessions.Create(WorkspaceRequest()).ok());
  RVM_CHECK(sessions.Create(WorkspaceRequest()).ok());

  sessions.Shutdown();
  RVM_CHECK_EQ(backend.stops, 2);
  RVM_CHECK(sessions.Get(1).value().state() == rvm::v1::SESSION_STATE_CLOSED);
  RVM_CHECK(sessions.Get(2).value().state() == rvm::v1::SESSION_STATE_CLOSED);
  sessions.Shutdown();
  RVM_CHECK_EQ(backend.stops, 2);
}

RVM_TEST(dispatcher_creates_sessions_through_the_manager) {
  AvailabilityReport report = AvailableReport();
  auto engine = MakeEngine(report);
  FakeBackend backend;
  SessionManager sessions(backend, report);
  RpcDispatcher dispatcher(*engine, "session-token", &sessions);
  RVM_CHECK(!Dispatch(dispatcher, HelloRequest(1, 4242), 4242).close);

  RpcRequest create;
  create.set_protocol(rvm::v1::PROTOCOL_VERSION_1);
  create.set_request_id(2);
  *create.mutable_create_session() = WorkspaceRequest();
  const DispatchResult created = Dispatch(dispatcher, create, 4242);
  RVM_CHECK(!created.close);
  const RpcResponse response = DecodeResponse(created.payload);
  RVM_CHECK(response.body_case() == RpcResponse::kCreateSession);
  RVM_CHECK_EQ(response.create_session().session().session_id(), 1u);
  RVM_CHECK(response.create_session().session().state() == rvm::v1::SESSION_STATE_READY);

  RpcRequest list;
  list.set_protocol(rvm::v1::PROTOCOL_VERSION_1);
  list.set_request_id(3);
  list.mutable_list_sessions();
  const RpcResponse listed = DecodeResponse(Dispatch(dispatcher, list, 4242).payload);
  RVM_CHECK_EQ(listed.list_sessions().sessions_size(), 1);
}

RVM_TEST(dispatcher_session_requests_surface_unavailable_without_a_manager) {
  auto engine = MakeEngine(UnavailableReport());
  RpcDispatcher dispatcher(*engine, "session-token");
  RVM_CHECK(!Dispatch(dispatcher, HelloRequest(1, 4242), 4242).close);

  RpcRequest create;
  create.set_protocol(rvm::v1::PROTOCOL_VERSION_1);
  create.set_request_id(2);
  *create.mutable_create_session() = WorkspaceRequest();
  const RpcResponse response = DecodeResponse(Dispatch(dispatcher, create, 4242).payload);
  RVM_CHECK(response.error().code() == rvm::v1::RVM_UNAVAILABLE);
}