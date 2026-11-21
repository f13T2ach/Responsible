#include <cstdint>
#include <memory>
#include <span>
#include <string>
#include <utility>
#include <vector>

#include "channel/protobuf_codec.h"
#include "core/engine.h"
#include "rvm/v1/control.pb.h"
#include "rvm/v1/rpc.pb.h"
#include "security/auth.h"
#include "server/rpc_dispatcher.h"
#include "test_registry.h"

using rvm::core::Engine;
using rvm::core::EngineConfig;
using rvm::core::EngineState;
using rvm::environment::AvailabilityReport;
using rvm::environment::IAvailabilityProbe;
using rvm::environment::ProbeResult;
using rvm::environment::ProbeState;
using rvm::server::DispatchResult;
using rvm::server::RpcDispatcher;
using rvm::v1::RpcRequest;
using rvm::v1::RpcResponse;

namespace {

class FakeProbe final : public IAvailabilityProbe {
 public:
  explicit FakeProbe(AvailabilityReport report) : report_(std::move(report)) {}
  AvailabilityReport Probe() const override { return report_; }

 private:
  AvailabilityReport report_;
};

AvailabilityReport UnavailableReport() {
  AvailabilityReport report;
  report.appcontainer = ProbeResult{ProbeState::kFailed, "userenv.dll is missing"};
  report.vbs = ProbeResult{ProbeState::kFailed, "VBS is disabled"};
  report.hvci = ProbeResult{ProbeState::kFailed, "HVCI is disabled"};
  return report;
}

AvailabilityReport AvailableReport() {
  AvailabilityReport report;
  report.appcontainer = ProbeResult{ProbeState::kOk, "ok"};
  report.winfsp = ProbeResult{ProbeState::kOk, "ok"};
  report.vbs = ProbeResult{ProbeState::kOk, "ok"};
  report.hvci = ProbeResult{ProbeState::kOk, "ok"};
  return report;
}

std::unique_ptr<Engine> MakeEngine(AvailabilityReport report) {
  auto engine = std::make_unique<Engine>(EngineConfig{},
                                         std::make_unique<FakeProbe>(std::move(report)));
  RVM_CHECK(engine->Initialize().ok());
  return engine;
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

RpcRequest HelloRequest(std::uint64_t request_id, std::uint32_t pid,
                        const std::string& token = "session-token") {
  RpcRequest request;
  request.set_protocol(rvm::v1::PROTOCOL_VERSION_1);
  request.set_request_id(request_id);
  request.mutable_hello()->set_requested_protocol(rvm::v1::PROTOCOL_VERSION_1);
  request.mutable_hello()->set_client_name("responsible-host");
  request.mutable_hello()->set_client_pid(pid);
  request.mutable_hello()->set_token(token);
  return request;
}

RpcRequest PingRequest(std::uint64_t request_id, std::uint64_t nonce) {
  RpcRequest request;
  request.set_protocol(rvm::v1::PROTOCOL_VERSION_1);
  request.set_request_id(request_id);
  request.mutable_ping()->set_nonce(nonce);
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

RVM_TEST(dispatcher_hello_returns_availability) {
  auto engine = MakeEngine(UnavailableReport());
  RpcDispatcher dispatcher(*engine, "session-token");
  const DispatchResult result = Dispatch(dispatcher, HelloRequest(1, 4242), 4242);

  RVM_CHECK(!result.close);
  const RpcResponse response = DecodeResponse(result.payload);
  RVM_CHECK(response.body_case() == RpcResponse::kHello);
  RVM_CHECK_EQ(response.request_id(), 1u);
  RVM_CHECK(response.protocol() == rvm::v1::PROTOCOL_VERSION_1);
  RVM_CHECK(!response.hello().rvm_available());
  RVM_CHECK(!response.hello().unavailable_reason().empty());
  RVM_CHECK_EQ(response.hello().engine_version(), std::string("0.1.0-p0"));
  RVM_CHECK_EQ(response.hello().backends_size(), 1);
  RVM_CHECK(response.hello().backends(0) == rvm::v1::BACKEND_KIND_SANDBOX);
  RVM_CHECK(response.hello().availability().appcontainer().state() ==
            rvm::v1::PROBE_STATE_FAILED);
}

RVM_TEST(dispatcher_rejects_bad_token_and_mismatched_pid) {
  auto engine = MakeEngine(UnavailableReport());

  RpcDispatcher bad_token(*engine, "session-token");
  const DispatchResult token_result =
      Dispatch(bad_token, HelloRequest(1, 4242, "wrong-token"), 4242);
  RVM_CHECK(token_result.close);
  RVM_CHECK(DecodeResponse(token_result.payload).error().code() ==
            rvm::v1::RVM_UNAUTHENTICATED);

  auto other_engine = MakeEngine(UnavailableReport());
  RpcDispatcher bad_pid(*other_engine, "session-token");
  const DispatchResult pid_result = Dispatch(bad_pid, HelloRequest(1, 4242), 9999);
  RVM_CHECK(pid_result.close);
  RVM_CHECK(DecodeResponse(pid_result.payload).error().code() ==
            rvm::v1::RVM_UNAUTHENTICATED);
}

RVM_TEST(dispatcher_rejects_unsupported_protocol) {
  auto engine = MakeEngine(UnavailableReport());
  RpcDispatcher dispatcher(*engine, "session-token");
  RpcRequest request = HelloRequest(1, 4242);
  request.mutable_hello()->set_requested_protocol(rvm::v1::PROTOCOL_VERSION_UNSPECIFIED);

  const DispatchResult result = Dispatch(dispatcher, request, 4242);
  RVM_CHECK(result.close);
  RVM_CHECK(DecodeResponse(result.payload).error().code() ==
            rvm::v1::RVM_PROTOCOL_ERROR);

  auto other_engine = MakeEngine(UnavailableReport());
  RpcDispatcher other(*other_engine, "session-token");
  RpcRequest legacy;
  legacy.set_protocol(rvm::v1::PROTOCOL_VERSION_UNSPECIFIED);
  legacy.set_request_id(2);
  legacy.mutable_hello()->set_requested_protocol(rvm::v1::PROTOCOL_VERSION_1);
  legacy.mutable_hello()->set_client_pid(4242);
  legacy.mutable_hello()->set_token("session-token");
  const auto encoded = EncodeRequest(legacy);
  const auto handle = other.Handle(std::span<const std::uint8_t>(encoded.data(), encoded.size()),
                                   4242, {}, [] { return true; });
  RVM_CHECK(handle.ok());
  RVM_CHECK(handle.value().close);
  RVM_CHECK(DecodeResponse(handle.value().payload).error().code() ==
            rvm::v1::RVM_PROTOCOL_ERROR);
}

RVM_TEST(dispatcher_requires_handshake_before_ping) {
  auto engine = MakeEngine(UnavailableReport());
  RpcDispatcher dispatcher(*engine, "session-token");
  const DispatchResult result = Dispatch(dispatcher, PingRequest(1, 7), 4242);
  RVM_CHECK(result.close);
  RVM_CHECK(DecodeResponse(result.payload).error().code() ==
            rvm::v1::RVM_UNAUTHENTICATED);
}

RVM_TEST(dispatcher_ping_after_hello_and_rejects_second_hello) {
  auto engine = MakeEngine(UnavailableReport());
  RpcDispatcher dispatcher(*engine, "session-token");
  RVM_CHECK(!Dispatch(dispatcher, HelloRequest(1, 4242), 4242).close);

  const DispatchResult ping = Dispatch(dispatcher, PingRequest(2, 77), 4242);
  RVM_CHECK(!ping.close);
  const RpcResponse pong = DecodeResponse(ping.payload);
  RVM_CHECK(pong.body_case() == RpcResponse::kPong);
  RVM_CHECK_EQ(pong.pong().nonce(), 77u);

  const DispatchResult second = Dispatch(dispatcher, HelloRequest(3, 4242), 4242);
  RVM_CHECK(DecodeResponse(second.payload).error().code() ==
            rvm::v1::RVM_INVALID_STATE);
}

RVM_TEST(dispatcher_reports_unavailable_for_engine_requests) {
  auto engine = MakeEngine(UnavailableReport());
  RpcDispatcher dispatcher(*engine, "session-token");
  RVM_CHECK(!Dispatch(dispatcher, HelloRequest(1, 4242), 4242).close);

  RpcRequest session;
  session.set_protocol(rvm::v1::PROTOCOL_VERSION_1);
  session.set_request_id(2);
  session.mutable_create_session()->set_workspace_root("C:\\work");
  const DispatchResult result = Dispatch(dispatcher, session, 4242);
  RVM_CHECK(!result.close);
  const RpcResponse response = DecodeResponse(result.payload);
  RVM_CHECK(response.error().code() == rvm::v1::RVM_UNAVAILABLE);
  RVM_CHECK(!response.error().detail().empty());
}

RVM_TEST(dispatcher_does_not_fake_execution_when_available) {
  auto engine = MakeEngine(AvailableReport());
  RpcDispatcher dispatcher(*engine, "session-token");
  RVM_CHECK(!Dispatch(dispatcher, HelloRequest(1, 4242), 4242).close);

  RpcRequest session;
  session.set_protocol(rvm::v1::PROTOCOL_VERSION_1);
  session.set_request_id(2);
  session.mutable_create_session()->set_workspace_root("C:\\work");
  const DispatchResult result = Dispatch(dispatcher, session, 4242);
  RVM_CHECK(DecodeResponse(result.payload).error().code() ==
            rvm::v1::RVM_NOT_IMPLEMENTED);
}

RVM_TEST(dispatcher_shutdown_stops_the_engine) {
  auto engine = MakeEngine(UnavailableReport());
  RpcDispatcher dispatcher(*engine, "session-token");
  RVM_CHECK(!Dispatch(dispatcher, HelloRequest(1, 4242), 4242).close);

  RpcRequest shutdown;
  shutdown.set_protocol(rvm::v1::PROTOCOL_VERSION_1);
  shutdown.set_request_id(2);
  shutdown.mutable_shutdown()->set_reason("test");
  const DispatchResult result = Dispatch(dispatcher, shutdown, 4242);
  RVM_CHECK(result.close);

  const RpcResponse response = DecodeResponse(result.payload);
  RVM_CHECK(response.body_case() == RpcResponse::kShutdown);
  RVM_CHECK(response.shutdown().accepted());
  RVM_CHECK(engine->state() == EngineState::kStopped);
}