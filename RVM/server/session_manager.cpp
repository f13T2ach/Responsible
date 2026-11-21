#include "server/session_manager.h"

#include <chrono>
#include <cstddef>
#include <utility>

#include "channel/convert.h"
#include "core/log.h"

namespace rvm::server {
namespace {

constexpr std::size_t kMaxLiveSessions = 8;
constexpr std::uint32_t kDefaultMemoryMb = 2048;
constexpr std::uint32_t kDefaultVcpuCount = 2;
constexpr std::uint32_t kMinMemoryMb = 256;
constexpr std::uint32_t kMaxVcpuCount = 64;

std::uint64_t NowUnixMs() {
  return static_cast<std::uint64_t>(
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::system_clock::now().time_since_epoch())
          .count());
}

bool IsReservedDeviceName(const std::wstring& name) {
  std::wstring upper;
  upper.reserve(name.size());
  for (const wchar_t ch : name) {
    upper.push_back(ch >= L'a' && ch <= L'z' ? static_cast<wchar_t>(ch - L'a' + L'A') : ch);
  }
  const std::size_t dot = upper.find(L'.');
  const std::wstring stem = dot == std::wstring::npos ? upper : upper.substr(0, dot);
  if (stem == L"CON" || stem == L"PRN" || stem == L"AUX" || stem == L"NUL") return true;
  if (stem.size() == 4 && (stem.compare(0, 3, L"COM") == 0 || stem.compare(0, 3, L"LPT") == 0) &&
      stem[3] >= L'1' && stem[3] <= L'9') {
    return true;
  }
  return false;
}

// The storage key becomes a directory name under %LOCALAPPDATA%\Responsible\RVM,
// so reject anything that could escape or collide with the reserved temp area.
bool IsValidStorageKey(const std::wstring& key) {
  if (key.empty() || key.size() > 64) return false;
  if (key.front() == L'.' || key.back() == L'.' || key.back() == L' ') return false;
  for (const wchar_t ch : key) {
    if (ch < 0x20) return false;
    switch (ch) {
      case L'\\': case L'/': case L':': case L'*': case L'?':
      case L'"': case L'<': case L'>': case L'|':
        return false;
      default:
        break;
    }
  }
  return !IsReservedDeviceName(key);
}

}  // namespace

SessionManager::SessionManager(runtime::IEnvironmentBackend& backend,
                               environment::AvailabilityReport availability)
    : backend_(backend), availability_(std::move(availability)) {}

SessionManager::~SessionManager() { Shutdown(); }

core::Result<v1::Session> SessionManager::Create(const v1::CreateSessionRequest& request) {
  std::lock_guard<std::mutex> lock(mutex_);

  if (!availability_.rvm_available()) {
    return core::Error(core::ErrorCode::kUnavailable, "RVM is unavailable", false,
                       availability_.reason());
  }
  if (request.workspace_root().empty()) {
    return core::Error(core::ErrorCode::kInvalidArgument, "workspace root must not be empty");
  }
  if (request.memory_mb() != 0 && request.memory_mb() < kMinMemoryMb) {
    return core::Error(core::ErrorCode::kInvalidArgument,
                       "memory must be at least " + std::to_string(kMinMemoryMb) + " MB");
  }
  if (request.vcpu_count() > kMaxVcpuCount) {
    return core::Error(core::ErrorCode::kInvalidArgument,
                       "vcpu count must not exceed " + std::to_string(kMaxVcpuCount));
  }

  auto policy = channel::FromProto(request.policy());
  if (!policy.ok()) return policy.error();
  if (!policy.value().writable_roots.empty()) {
    return core::Error(core::ErrorCode::kNotImplemented,
                       "additional writable roots are not supported by the sandbox backend yet");
  }
  if (policy.value().mode == core::SandboxMode::kDangerFullAccess) {
    return core::Error(core::ErrorCode::kNotImplemented,
                       "danger-full-access sessions are not supported by the sandbox backend yet");
  }

  auto workspace = channel::FromUtf8(request.workspace_root());
  if (!workspace.ok()) return workspace.error();

  std::size_t live = 0;
  for (const auto& entry : sessions_) {
    if (entry.second.environment != nullptr) ++live;
  }
  if (live >= kMaxLiveSessions) {
    return core::Error(core::ErrorCode::kBusy, "too many live sessions",
                       false, std::to_string(live) + " live");
  }

  runtime::EnvironmentSpec spec;
  spec.session_id = L"s" + std::to_wstring(next_id_);
  if (!request.storage_key().empty()) {
    auto key = channel::FromUtf8(request.storage_key());
    if (!key.ok()) return key.error();
    if (!IsValidStorageKey(key.value())) {
      return core::Error(core::ErrorCode::kInvalidArgument,
                         "storage key must be a safe directory name");
    }
    spec.storage_key = std::move(key.value());
  }
  spec.workspace_root = workspace.value();
  spec.policy = policy.value();
  spec.memory_mb = request.memory_mb() == 0 ? kDefaultMemoryMb : request.memory_mb();
  spec.vcpu_count = request.vcpu_count() == 0 ? kDefaultVcpuCount : request.vcpu_count();
  if (request.network_access()) {
    return core::Error(core::ErrorCode::kNotImplemented,
                       "network access is not supported by the sandbox backend");
  }
  spec.overlay_workspace = request.overlay_workspace();
  if (request.overlay_workspace()) {
    auto drive = channel::FromUtf8(request.overlay_drive());
    if (!drive.ok()) return drive.error();
    spec.overlay_drive = drive.value();
  }
  spec.masked_patterns.reserve(static_cast<std::size_t>(request.masked_patterns_size()));
  for (const std::string& pattern : request.masked_patterns()) {
    auto decoded = channel::FromUtf8(pattern);
    if (!decoded.ok()) return decoded.error();
    spec.masked_patterns.push_back(std::move(decoded.value()));
  }

  auto environment = backend_.Create(spec);
  if (!environment.ok()) return environment.error();

  auto facts = environment.value()->Start();
  if (!facts.ok()) {
    const core::Status stopped = environment.value()->Stop();
    if (!stopped.ok()) {
      core::LogWarn("session", "failed to stop the environment after a failed start: " +
                                   stopped.error().message);
    }
    return facts.error();
  }

  auto build = channel::ToUtf8(facts.value().windows_build);
  if (!build.ok()) {
    environment.value()->Stop();
    return build.error();
  }
  const std::wstring& session_workspace = facts.value().workspace_path.empty()
                                              ? spec.guest_workspace_path
                                              : facts.value().workspace_path;
  auto virtual_root = channel::ToUtf8(session_workspace);
  if (!virtual_root.ok()) {
    environment.value()->Stop();
    return virtual_root.error();
  }

  const std::uint64_t session_id = next_id_++;
  v1::Session record;
  record.set_session_id(session_id);
  record.set_workspace_root(request.workspace_root());
  record.set_backend(backend_.kind() == runtime::BackendKind::kHcs ? v1::BACKEND_KIND_HCS
                                                               : v1::BACKEND_KIND_SANDBOX);
  record.set_state(v1::SESSION_STATE_READY);
  record.set_created_unix_ms(NowUnixMs());
  record.set_virtual_workspace_root(virtual_root.value());
  v1::EnvironmentFacts* proto_facts = record.mutable_environment();
  proto_facts->set_windows_build(build.value());
  proto_facts->set_vbs_enabled(facts.value().vbs_enabled);
  proto_facts->set_hvci_enabled(facts.value().hvci_enabled);
  proto_facts->set_memory_mb(facts.value().memory_mb);
  proto_facts->set_vcpu_count(facts.value().vcpu_count);
  proto_facts->set_network_enabled(facts.value().network_enabled);

  sessions_.emplace(session_id, Entry{record, facts.value(), spec.workspace_root, environment.Take()});
  return record;
}

core::Status SessionManager::Close(std::uint64_t session_id, const std::string& reason) {
  std::lock_guard<std::mutex> lock(mutex_);

  auto found = sessions_.find(session_id);
  if (found == sessions_.end()) {
    return core::Status::Failure(
        core::Error(core::ErrorCode::kSessionNotFound, "session does not exist"));
  }
  if (found->second.environment == nullptr) {
    return core::Status::Failure(
        core::Error(core::ErrorCode::kInvalidState, "session is already closed"));
  }

  const core::Status stopped = found->second.environment->Stop();
  if (!stopped.ok()) return stopped;
  found->second.environment.reset();
  found->second.record.set_state(v1::SESSION_STATE_CLOSED);
  core::LogInfo("session", "closed session " + std::to_string(session_id) +
                               (reason.empty() ? "" : ": " + reason));
  return core::Status::Ok();
}

core::Result<v1::Session> SessionManager::Get(std::uint64_t session_id) const {
  std::lock_guard<std::mutex> lock(mutex_);
  auto found = sessions_.find(session_id);
  if (found == sessions_.end()) {
    return core::Error(core::ErrorCode::kSessionNotFound, "session does not exist");
  }
  return found->second.record;
}

std::vector<v1::Session> SessionManager::List() const {
  std::lock_guard<std::mutex> lock(mutex_);
  std::vector<v1::Session> records;
  records.reserve(sessions_.size());
  for (const auto& entry : sessions_) records.push_back(entry.second.record);
  return records;
}

core::Result<ShellContext> SessionManager::DescribeForShell(std::uint64_t session_id) const {
  std::lock_guard<std::mutex> lock(mutex_);
  auto found = sessions_.find(session_id);
  if (found == sessions_.end()) {
    return core::Error(core::ErrorCode::kSessionNotFound, "session does not exist");
  }
  if (found->second.environment == nullptr) {
    return core::Error(core::ErrorCode::kInvalidState, "session is closed");
  }
  ShellContext context;
  context.host_workspace_root = found->second.host_workspace_root;
  context.workspace_path = found->second.facts.workspace_path;
  context.appcontainer_sid = found->second.facts.appcontainer_sid;
  context.temp_path = found->second.facts.temp_path;
  context.restricted_token = found->second.facts.restricted_token;
  return context;
}

void SessionManager::Shutdown() {
  std::lock_guard<std::mutex> lock(mutex_);
  for (auto& [id, entry] : sessions_) {
    if (entry.environment == nullptr) continue;
    const core::Status stopped = entry.environment->Stop();
    if (!stopped.ok()) {
      core::LogWarn("session", "session " + std::to_string(id) +
                                   " did not stop cleanly: " + stopped.error().message);
    }
    entry.environment.reset();
    entry.record.set_state(v1::SESSION_STATE_CLOSED);
  }
}

}  // namespace rvm::server