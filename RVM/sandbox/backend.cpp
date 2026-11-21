#include "sandbox/backend.h"

#include <windows.h>

#include <algorithm>
#include <cwctype>
#include <filesystem>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "core/log.h"
#include "environment/system_probe.h"
#include "sandbox/appcontainer.h"
#include "workspace/fsp_host.h"
#include "workspace/union_view.h"

namespace rvm::sandbox {
namespace {

namespace fs = std::filesystem;

constexpr std::uint32_t kDefaultMemoryMb = 2048;
constexpr std::uint32_t kDefaultMaxProcesses = 256;
constexpr std::uint32_t kMaxAppContainerName = 64;
constexpr wchar_t kFirstOverlayDrive = L'R';

std::wstring ProfileNameFor(const std::wstring& session_id) {
  std::wstring name = L"ResponsibleRvm" + std::to_wstring(GetCurrentProcessId());
  for (const wchar_t ch : session_id) {
    const bool allowed = (ch >= L'a' && ch <= L'z') || (ch >= L'A' && ch <= L'Z') ||
                         (ch >= L'0' && ch <= L'9');
    if (allowed) name.push_back(ch);
  }
  if (name.size() > kMaxAppContainerName) name.resize(kMaxAppContainerName);
  return name;
}

bool IsAbsolutePath(const std::wstring& path) {
  if (path.size() < 3) return false;
  if (!std::iswalpha(static_cast<wint_t>(path[0]))) return false;
  return path[1] == L':' && (path[2] == L'\\' || path[2] == L'/');
}

std::wstring NormalizeDrive(const std::wstring& value) {
  if (value.size() != 2 || value[1] != L':') return {};
  wchar_t letter = static_cast<wchar_t>(std::towupper(value[0]));
  if (letter < L'C' || letter > L'Z') return {};
  return std::wstring(1, letter) + L":";
}

std::wstring PickOverlayDrive(const std::wstring& preferred) {
  const DWORD mask = GetLogicalDrives();
  const auto available = [mask](wchar_t letter) {
    return (mask & (1u << (letter - L'A'))) == 0;
  };
  const std::wstring normalized = NormalizeDrive(preferred);
  if (!normalized.empty() && available(normalized[0])) return normalized;
  for (wchar_t letter = kFirstOverlayDrive; letter <= L'Z'; ++letter) {
    if (available(letter)) return std::wstring(1, letter) + L":";
  }
  for (wchar_t letter = L'D'; letter < kFirstOverlayDrive; ++letter) {
    if (available(letter)) return std::wstring(1, letter) + L":";
  }
  return {};
}

std::wstring RvmStorageBase() {
  wchar_t local_app_data[MAX_PATH] = {};
  const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local_app_data, MAX_PATH);
  if (length > 0 && length < MAX_PATH) {
    return (fs::path(std::wstring(local_app_data, length)) / L"Responsible" / L"RVM").wstring();
  }
  return (fs::path(fs::temp_directory_path()) / L"Responsible" / L"RVM").wstring();
}

bool IsSafeKeyChar(wchar_t ch) {
  if (ch < 0x20) return false;
  switch (ch) {
    case L'\\': case L'/': case L':': case L'*': case L'?':
    case L'"': case L'<': case L'>': case L'|':
      return false;
    default:
      return true;
  }
}

// Per-workspace root: %LOCALAPPDATA%\Responsible\RVM\<workspace>_<id>. Requests
// without a storage key keep the legacy per-session scratch directory.
std::wstring WorkspaceStorageRoot(const runtime::EnvironmentSpec& spec) {
  const std::wstring base = RvmStorageBase();
  if (!spec.storage_key.empty()) {
    bool safe = spec.storage_key.size() <= 64;
    for (const wchar_t ch : spec.storage_key) safe = safe && IsSafeKeyChar(ch);
    if (safe) return (fs::path(base) / spec.storage_key).wstring();
  }
  std::wstring name = L"session";
  for (const wchar_t ch : spec.session_id) {
    if ((ch >= L'a' && ch <= L'z') || (ch >= L'A' && ch <= L'Z') || (ch >= L'0' && ch <= L'9')) {
      name.push_back(ch);
    }
  }
  name += L"-" + std::to_wstring(GetCurrentProcessId()) + L"-" +
          std::to_wstring(GetTickCount64());
  return (fs::path(base) / L"sessions" / name).wstring();
}

std::wstring WorkspaceTempRoot(const runtime::EnvironmentSpec& spec) {
  std::wstring key = spec.storage_key;
  if (key.empty()) {
    key = L"session-" + std::to_wstring(GetCurrentProcessId()) + L"-" +
          std::to_wstring(GetTickCount64());
  } else {
    for (wchar_t& ch : key) {
      if (!IsSafeKeyChar(ch)) ch = L'_';
    }
  }
  return (fs::path(RvmStorageBase()) / L".rvm_temp" / key).wstring();
}

}  // namespace

class SandboxEnvironment final : public runtime::IEnvironment {
 public:
  explicit SandboxEnvironment(runtime::EnvironmentSpec spec) : spec_(std::move(spec)) {}
  ~SandboxEnvironment() override {
    const core::Status status = Stop();
    if (!status.ok()) {
      core::LogWarn("sandbox", std::string("environment release failed: ") +
                                   status.error().message);
    }
  }

  SandboxEnvironment(const SandboxEnvironment&) = delete;
  SandboxEnvironment& operator=(const SandboxEnvironment&) = delete;

  core::Result<runtime::EnvironmentFacts> Start() override {
    if (phase_ == runtime::EnvironmentPhase::kReady ||
        phase_ == runtime::EnvironmentPhase::kCreating) {
      return core::Error(core::ErrorCode::kInvalidState, "sandbox is already active");
    }
    if (spec_.workspace_root.empty() || !IsAbsolutePath(spec_.workspace_root)) {
      return core::Error(core::ErrorCode::kInvalidArgument,
                         "workspace root must be an absolute local path");
    }
    std::error_code error;
    if (!fs::is_directory(spec_.workspace_root, error) || error) {
      return core::Error(core::ErrorCode::kInvalidArgument, "workspace root is not a directory",
                         false, std::string(spec_.workspace_root.begin(),
                                            spec_.workspace_root.end()));
    }
    if (spec_.policy.network_access) {
      return core::Error(core::ErrorCode::kNotImplemented,
                         "the sandbox does not expose network access; disable network access");
    }

    phase_ = runtime::EnvironmentPhase::kCreating;

    auto profile = CreateProfile(ProfileNameFor(spec_.session_id));
    if (!profile.ok()) {
      phase_ = runtime::EnvironmentPhase::kFailed;
      return profile.error();
    }
    profile_ = profile.Take();

    temp_root_ = WorkspaceTempRoot(spec_);
    std::error_code temp_error;
    fs::create_directories(temp_root_, temp_error);
    if (temp_error) {
      StopProfile();
      phase_ = runtime::EnvironmentPhase::kFailed;
      return core::Error(core::ErrorCode::kBackendFailed,
                         "failed to create the session temp directory", false,
                         temp_error.message());
    }

    const bool write = spec_.policy.mode != core::SandboxMode::kReadOnly;
    if (spec_.overlay_workspace) {
      const core::Status mounted = MountOverlay(profile_.sid, write);
      if (!mounted.ok()) {
        StopProfile();
        RemoveTempRoot();
        phase_ = runtime::EnvironmentPhase::kFailed;
        return mounted.error();
      }
    } else {
      core::Status granted = GrantDirectory(profile_.sid, spec_.workspace_root, write, true);
      if (!granted.ok()) {
        StopProfile();
        phase_ = runtime::EnvironmentPhase::kFailed;
        return granted.error();
      }
      granted_directories_.push_back(spec_.workspace_root);

      const core::Status temp = GrantDirectory(profile_.sid, temp_root_, true, true);
      if (!temp.ok()) {
        Stop();
        phase_ = runtime::EnvironmentPhase::kFailed;
        return temp.error();
      }
      granted_directories_.push_back(temp_root_);

      const core::Status ancestors =
          GrantTraverseAncestors(profile_.sid, spec_.workspace_root, &granted_ancestors_);
      if (!ancestors.ok()) {
        core::LogWarn("sandbox", "could not grant traverse rights to every ancestor: " +
                                     ancestors.error().message + " " + ancestors.error().detail);
      }

      const core::Status reachable = VerifyWorkspaceAccess(write);
      if (!reachable.ok()) {
        Stop();
        phase_ = runtime::EnvironmentPhase::kFailed;
        return reachable.error();
      }
    }

    phase_ = runtime::EnvironmentPhase::kReady;
    runtime::EnvironmentFacts facts;
    facts.workspace_path =
        overlay_mount_ ? session_workspace_ : spec_.workspace_root;
    facts.temp_path = temp_root_;
    const std::string build = environment::CurrentWindowsBuild();
    facts.windows_build = std::wstring(build.begin(), build.end());
    facts.memory_mb = spec_.memory_mb == 0 ? kDefaultMemoryMb : spec_.memory_mb;
    facts.vcpu_count = spec_.vcpu_count == 0
                           ? std::max(1u, std::thread::hardware_concurrency())
                           : spec_.vcpu_count;
    facts.vbs_enabled = environment::ProbeVbs().ok();
    facts.hvci_enabled = environment::ProbeHvci().ok();
    facts.network_enabled = false;
    facts.appcontainer_sid = profile_.sid;
    facts.restricted_token = overlay_mount_ != nullptr;
    core::LogInfo("sandbox", "sandbox environment ready");
    return facts;
  }

  core::Status Stop() override {
    if (profile_.name.empty() && !overlay_mount_ && temp_root_.empty()) {
      phase_ = runtime::EnvironmentPhase::kStopped;
      return core::Status::Ok();
    }
    phase_ = runtime::EnvironmentPhase::kStopping;
    core::Status result = core::Status::Ok();
    UnmountOverlay();
    for (auto it = granted_ancestors_.rbegin(); it != granted_ancestors_.rend(); ++it) {
      const core::Status revoked = RevokeDirectory(profile_.sid, *it, false);
      if (!revoked.ok()) {
        core::LogWarn("sandbox", revoked.error().message);
        result = revoked;
      }
    }
    granted_ancestors_.clear();
    for (auto it = granted_directories_.rbegin(); it != granted_directories_.rend(); ++it) {
      const core::Status revoked = RevokeDirectory(profile_.sid, *it, true);
      if (!revoked.ok()) {
        core::LogWarn("sandbox", revoked.error().message);
        result = revoked;
      }
    }
    granted_directories_.clear();
    RemoveTempRoot();
    core::Status deleted = core::Status::Ok();
    if (!profile_.name.empty()) deleted = DeleteProfile(profile_.name);
    if (!deleted.ok()) {
      core::LogWarn("sandbox", deleted.error().message);
      result = deleted;
    }
    profile_ = {};
    phase_ = runtime::EnvironmentPhase::kStopped;
    return result;
  }

  core::Status Reset() override {
    const core::Status status = Stop();
    phase_ = runtime::EnvironmentPhase::kAbsent;
    return status;
  }

  runtime::EnvironmentPhase phase() const override { return phase_; }

 private:
  core::Status MountOverlay(const std::wstring& sid, bool write) {
    const std::wstring drive = PickOverlayDrive(spec_.overlay_drive);
    if (drive.empty()) {
      return core::Status::Failure(core::Error(
          core::ErrorCode::kBackendFailed, "no free drive letter is available for the workspace",
          false, "checked D: through Z:"));
    }

    overlay_root_persistent_ = !spec_.storage_key.empty();
    overlay_session_root_ = WorkspaceStorageRoot(spec_);
    std::error_code error;
    const fs::path diff_root = fs::path(overlay_session_root_) / L"diff";
    const fs::path tombstone_root = fs::path(overlay_session_root_) / L"tombstones";
    fs::create_directories(diff_root, error);
    if (!error) fs::create_directories(tombstone_root, error);
    if (error) {
      RemoveOverlayRoot();
      return core::Status::Failure(core::Error(
          core::ErrorCode::kBackendFailed, "failed to prepare the workspace overlay directories",
          false, error.message()));
    }

    rvm::workspace::UnionPaths paths;
    paths.base_root = spec_.workspace_root;
    paths.diff_root = diff_root.wstring();
    paths.tombstone_root = tombstone_root.wstring();
    auto view = rvm::workspace::UnionView::Create(std::move(paths));
    if (!view.ok()) {
      RemoveOverlayRoot();
      return view.error();
    }
    overlay_view_ = std::make_unique<rvm::workspace::UnionView>(view.Take());

    rvm::workspace::MountOptions mount_options;
    mount_options.mount_point = drive;
    mount_options.appcontainer_sid = sid;
    auto mounted = rvm::workspace::MountedWorkspace::Mount(overlay_view_.get(), mount_options);
    if (!mounted.ok()) {
      overlay_view_.reset();
      RemoveOverlayRoot();
      return mounted.error();
    }
    overlay_mount_ = mounted.Take();
    session_workspace_ = drive + L"\\";

    const core::Status reachable = VerifyOverlayAccess(write);
    if (!reachable.ok()) {
      UnmountOverlay();
      return reachable.error();
    }
    core::LogInfo("sandbox", "workspace overlay mounted on " +
                                 std::string(drive.begin(), drive.end()));
    return core::Status::Ok();
  }

  core::Status VerifyOverlayAccess(bool write) {
    ProcessOptions probe;
    probe.command_line = write ? L"cmd.exe /c \"echo rvm> .rvm-overlay-probe\""
                               : L"cmd.exe /c \"dir /b > nul\"";
    probe.working_directory = session_workspace_;
    // WinFsp volumes reject AppContainer device opens (winfsp/winfsp#705).
    probe.containment = Containment::kRestrictedToken;
    probe.timeout_ms = 20000;
    probe.memory_limit_bytes = 512ull * 1024 * 1024;
    probe.max_processes = 4;

    auto result = RunProcess(probe);
    if (!result.ok()) {
      return core::Status::Failure(core::Error(
          core::ErrorCode::kBackendFailed, "failed to verify the workspace overlay from the sandbox",
          false, result.error().detail));
    }
    if (write) {
      DeleteFileW((session_workspace_ + L".rvm-overlay-probe").c_str());
    }
    if (result.value().timed_out || result.value().exit_code != 0) {
      std::string detail = "exit=" + std::to_string(result.value().exit_code);
      if (!result.value().error.empty()) detail += " " + result.value().error;
      return core::Status::Failure(core::Error(
          core::ErrorCode::kPolicyDenied, "the sandbox cannot access the workspace overlay", false,
          detail));
    }
    return core::Status::Ok();
  }

  void UnmountOverlay() {
    if (overlay_mount_) {
      overlay_mount_->Unmount();
      overlay_mount_.reset();
    }
    overlay_view_.reset();
    session_workspace_.clear();
    RemoveOverlayRoot();
  }

  void RemoveOverlayRoot() {
    if (overlay_session_root_.empty()) return;
    if (!overlay_root_persistent_) {
      std::error_code ignored;
      fs::remove_all(overlay_session_root_, ignored);
    }
    overlay_session_root_.clear();
  }

  void RemoveTempRoot() {
    if (temp_root_.empty()) return;
    std::error_code ignored;
    fs::remove_all(temp_root_, ignored);
    temp_root_.clear();
  }

  core::Status VerifyWorkspaceAccess(bool write) {
    ProcessOptions probe;
    probe.command_line = write ? L"cmd.exe /c \"echo rvm> .rvm-sandbox-probe\""
                               : L"cmd.exe /c \"dir /b > nul\"";
    probe.working_directory = spec_.workspace_root;
    probe.appcontainer_sid = profile_.sid;
    probe.timeout_ms = 20000;
    probe.memory_limit_bytes = 512ull * 1024 * 1024;
    probe.max_processes = 4;

    auto result = RunProcess(probe);
    if (!result.ok()) {
      return core::Status::Failure(core::Error(
          core::ErrorCode::kBackendFailed, "failed to verify workspace access from the sandbox",
          false, result.error().detail));
    }
    if (write) {
      DeleteFileW((spec_.workspace_root + L"\\.rvm-sandbox-probe").c_str());
    }
    if (result.value().timed_out || result.value().exit_code != 0) {
      std::string detail = "exit=" + std::to_string(result.value().exit_code);
      if (!result.value().error.empty()) detail += " " + result.value().error;
      return core::Status::Failure(core::Error(
          core::ErrorCode::kPolicyDenied, "the sandbox cannot access the workspace directory",
          false, detail));
    }
    return core::Status::Ok();
  }

  void StopProfile() {
    if (profile_.name.empty()) return;
    const core::Status deleted = DeleteProfile(profile_.name);
    if (!deleted.ok()) core::LogWarn("sandbox", deleted.error().message);
    profile_ = {};
  }

  runtime::EnvironmentSpec spec_;
  AppContainerProfile profile_;
  std::vector<std::wstring> granted_directories_;
  std::vector<std::wstring> granted_ancestors_;
  std::wstring overlay_session_root_;
  bool overlay_root_persistent_ = false;
  std::wstring temp_root_;
  std::unique_ptr<rvm::workspace::UnionView> overlay_view_;
  std::unique_ptr<rvm::workspace::MountedWorkspace> overlay_mount_;
  std::wstring session_workspace_;
  runtime::EnvironmentPhase phase_ = runtime::EnvironmentPhase::kAbsent;
};

runtime::BackendProbe SandboxBackend::Probe() const {
  const environment::AppContainerProbe probe = environment::ProbeAppContainerApi();
  runtime::BackendProbe result;
  result.ready = probe.available;
  result.detail = probe.detail;
  return result;
}

core::Result<std::unique_ptr<runtime::IEnvironment>> SandboxBackend::Create(
    const runtime::EnvironmentSpec& spec) {
  std::unique_ptr<runtime::IEnvironment> environment =
      std::make_unique<SandboxEnvironment>(spec);
  return environment;
}

std::unique_ptr<runtime::IEnvironmentBackend> CreateSandboxBackend() {
  return std::make_unique<SandboxBackend>();
}

}  // namespace rvm::sandbox