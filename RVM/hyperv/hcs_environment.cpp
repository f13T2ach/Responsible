#include "hyperv/hcs_environment.h"

#include <filesystem>
#include <utility>

#include "core/log.h"
#include "environment/system_probe.h"
#include "hyperv/hcs_config.h"

namespace rvm::hyperv {
namespace fs = std::filesystem;

namespace {

constexpr std::uint32_t kCreateTimeoutMs = 180000;
constexpr std::uint32_t kStartTimeoutMs = 180000;
constexpr std::uint32_t kStopTimeoutMs = 20000;

core::Error AtStage(std::string_view stage, core::Error error) {
  error.detail += error.detail.empty() ? "" : " ";
  error.detail += "stage=";
  error.detail += stage;
  return error;
}

std::string SessionIdFor(const runtime::EnvironmentSpec& spec) {
  if (!spec.session_id.empty()) return WideToUtf8(spec.session_id);
  return NewGuid();
}

}  // namespace

HcsEnvironment::HcsEnvironment(runtime::EnvironmentSpec spec, BaseLayerStore store)
    : spec_(std::move(spec)), store_(std::move(store)) {}

HcsEnvironment::~HcsEnvironment() {
  const core::Status status = ReleaseSystem(false);
  if (!status.ok()) {
    core::LogWarn("hcs", std::string("environment release failed: ") + status.error().message);
  }
}

core::Result<runtime::EnvironmentFacts> HcsEnvironment::Start() {
  if (system_ != nullptr || phase_ == runtime::EnvironmentPhase::kReady ||
      phase_ == runtime::EnvironmentPhase::kBooting || phase_ == runtime::EnvironmentPhase::kCreating) {
    return core::Error(core::ErrorCode::kInvalidState, "HCS environment is already active");
  }
  if (spec_.workspace_root.empty()) {
    return core::Error(core::ErrorCode::kInvalidArgument, "workspace root must not be empty");
  }
  if (!IsAbsoluteWindowsPath(spec_.workspace_root)) {
    return core::Error(core::ErrorCode::kInvalidArgument,
                       "workspace root must be an absolute local path", false,
                       WideToUtf8(spec_.workspace_root));
  }
  std::error_code fs_error;
  if (!fs::is_directory(spec_.workspace_root, fs_error) || fs_error) {
    return core::Error(core::ErrorCode::kInvalidArgument, "workspace root is not a directory",
                       false, WideToUtf8(spec_.workspace_root));
  }
  if (spec_.policy.network_access) {
    return core::Error(core::ErrorCode::kNotImplemented,
                       "guest network access is not implemented yet; disable network access");
  }
  if (!IsAbsoluteWindowsPath(spec_.guest_workspace_path)) {
    return core::Error(core::ErrorCode::kInvalidArgument,
                       "guest workspace path must be absolute");
  }

  phase_ = runtime::EnvironmentPhase::kCreating;

  auto api = HcsApi::Load();
  if (!api.ok()) {
    phase_ = runtime::EnvironmentPhase::kFailed;
    return AtStage("load_hcs", api.error());
  }
  api_ = api.Take();

  auto base_layer = store_.Ensure(spec_.allow_base_layer_creation);
  if (!base_layer.ok()) {
    phase_ = runtime::EnvironmentPhase::kFailed;
    return AtStage("prepare_base_layer", base_layer.error());
  }

  const std::string session_id = SessionIdFor(spec_);
  auto scratch = store_.PrepareScratch(session_id);
  if (!scratch.ok()) {
    phase_ = runtime::EnvironmentPhase::kFailed;
    return AtStage("prepare_scratch", scratch.error());
  }
  scratch_path_ = scratch.value();

  ComputeSystemSpec config;
  system_id_ = L"rvm-" + std::wstring(session_id.begin(), session_id.end());
  config.name = system_id_;
  config.layers.push_back({base_layer.value().id, base_layer.value().path});
  config.scratch_path = scratch_path_;
  config.memory_mb = spec_.memory_mb;
  MappedDirectorySpec workspace;
  workspace.host_path = spec_.workspace_root;
  workspace.guest_path = spec_.guest_workspace_path;
  workspace.read_only = spec_.policy.mode == core::SandboxMode::kReadOnly;
  config.mapped_directories.push_back(std::move(workspace));

  auto json = BuildComputeSystemJson(config);
  if (!json.ok()) {
    phase_ = runtime::EnvironmentPhase::kFailed;
    return AtStage("build_config", json.error());
  }

  auto created = api_.CreateComputeSystem(system_id_, json.value(), kCreateTimeoutMs);
  if (!created.ok()) {
    phase_ = runtime::EnvironmentPhase::kFailed;
    RemoveScratch();
    return AtStage("create_system", created.error());
  }
  system_ = created.value();
  phase_ = runtime::EnvironmentPhase::kBooting;

  const core::Status started = api_.StartComputeSystem(system_, kStartTimeoutMs);
  if (!started.ok()) {
    ReleaseSystem(false);
    RemoveScratch();
    phase_ = runtime::EnvironmentPhase::kFailed;
    return AtStage("start_system", started.error());
  }

  const auto properties =
      api_.GetComputeSystemProperties(system_, R"({"PropertyTypes":["Basic"]})");
  if (!properties.ok()) {
    core::LogWarn("hcs", std::string("system properties unavailable: ") +
                             properties.error().message);
  } else {
    core::LogDebug("hcs", "compute system properties: " + properties.value());
  }

  phase_ = runtime::EnvironmentPhase::kReady;
  core::LogInfo("hcs", "HCS environment " + WideToUtf8(system_id_) + " started");

  runtime::EnvironmentFacts facts;
  const std::string build = BaseLayerStore::CurrentWindowsBuild();
  facts.windows_build = std::wstring(build.begin(), build.end());
  facts.memory_mb = spec_.memory_mb;
  facts.vcpu_count = spec_.vcpu_count;
  facts.vbs_enabled = environment::ProbeVbs().ok();
  facts.hvci_enabled = environment::ProbeHvci().ok();
  facts.network_enabled = false;
  return facts;
}

core::Status HcsEnvironment::ReleaseSystem(bool graceful) {
  if (system_ == nullptr) {
    return core::Status::Ok();
  }

  core::Status result = core::Status::Ok();
  if (graceful) {
    result = api_.ShutdownComputeSystem(system_, kStopTimeoutMs);
  }
  if (!graceful || !result.ok()) {
    const core::Status terminated = api_.TerminateComputeSystem(system_, kStopTimeoutMs);
    if (!terminated.ok()) result = terminated;
  }
  api_.CloseComputeSystem(system_);
  system_ = nullptr;
  return result;
}

core::Status HcsEnvironment::RemoveScratch() {
  if (scratch_path_.empty()) return core::Status::Ok();
  const core::Status status = store_.RemoveScratch(scratch_path_);
  if (status.ok()) scratch_path_.clear();
  return status;
}

core::Status HcsEnvironment::Stop() {
  if (system_ == nullptr) {
    phase_ = runtime::EnvironmentPhase::kStopped;
    return core::Status::Ok();
  }
  phase_ = runtime::EnvironmentPhase::kStopping;
  const core::Status status = ReleaseSystem(true);
  phase_ = runtime::EnvironmentPhase::kStopped;
  if (!status.ok()) {
    return core::Status::Failure(
        core::Error(status.error().code, status.error().message, status.error().retryable,
                    status.error().detail + " stage=stop_system"));
  }
  return core::Status::Ok();
}

core::Status HcsEnvironment::Reset() {
  const core::Status stopped = Stop();
  const core::Status scratch = RemoveScratch();
  if (!stopped.ok()) return stopped;
  if (!scratch.ok()) return scratch;
  phase_ = runtime::EnvironmentPhase::kAbsent;
  return core::Status::Ok();
}

}  // namespace rvm::hyperv