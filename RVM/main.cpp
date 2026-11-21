#include <windows.h>
#include <sddl.h>
#include <shellapi.h>

#include <fstream>
#include <filesystem>
#include <iostream>
#include <iterator>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

#include "channel/convert.h"
#include "client/shell_bridge.h"
#include "core/engine.h"
#include "core/log.h"
#include "core/version.h"
#include "environment/availability.h"
#include "hyperv/backend.h"
#include "hyperv/hcs_api.h"
#include "hyperv/hcs_config.h"
#include "hyperv/hcs_layer.h"
#include "runtime/backend.h"
#include "sandbox/appcontainer.h"
#include "sandbox/backend.h"
#include "security/auth.h"
#include "server/pipe_server.h"
#include "server/session_manager.h"
#include "workspace/fsp_host.h"
#include "workspace/union_view.h"

namespace {

// AppContainer children cannot create a console of their own; their loader fails with
// STATUS_DLL_INIT_FAILED unless an existing console can be inherited. Keep one hidden.
void EnsureInheritableConsole() {
  if (GetConsoleCP() != 0) return;
  if (!AttachConsole(ATTACH_PARENT_PROCESS) && !AllocConsole()) return;
  const HWND window = GetConsoleWindow();
  if (window != nullptr) ShowWindow(window, SW_HIDE);
}

int PrintTokenInfo() {
  HANDLE token = nullptr;
  if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
    std::cerr << "OpenProcessToken failed: " << GetLastError() << '\n';
    return 1;
  }
  BOOL is_appcontainer = FALSE;
  DWORD returned = 0;
  if (GetTokenInformation(token, static_cast<TOKEN_INFORMATION_CLASS>(29), &is_appcontainer,
                          sizeof(is_appcontainer), &returned)) {
    std::cout << "appcontainer=" << (is_appcontainer ? "true" : "false") << '\n';
  } else {
    std::cout << "appcontainer=query_failed\n";
  }
  BYTE buffer[1024] = {};
  if (GetTokenInformation(token, TokenAppContainerSid, buffer, sizeof(buffer), &returned)) {
    LPWSTR text = nullptr;
    if (ConvertSidToStringSidW(
            reinterpret_cast<PTOKEN_APPCONTAINER_INFORMATION>(buffer)->TokenAppContainer,
            &text)) {
      const std::wstring sid(text);
      LocalFree(text);
      std::cout << "appcontainer_sid=" << std::string(sid.begin(), sid.end()) << '\n';
    }
  }
  if (GetTokenInformation(token, TokenIntegrityLevel, buffer, sizeof(buffer), &returned)) {
    PSID sid = reinterpret_cast<PTOKEN_MANDATORY_LABEL>(buffer)->Label.Sid;
    const DWORD count = *GetSidSubAuthorityCount(sid);
    std::cout << "integrity=" << *GetSidSubAuthority(sid, count - 1) << '\n';
  }
  if (GetTokenInformation(token, TokenUser, buffer, sizeof(buffer), &returned)) {
    LPWSTR text = nullptr;
    if (ConvertSidToStringSidW(reinterpret_cast<PTOKEN_USER>(buffer)->User.Sid, &text)) {
      const std::wstring sid(text);
      LocalFree(text);
      std::cout << "user_sid=" << std::string(sid.begin(), sid.end()) << '\n';
    }
  }
  CloseHandle(token);
  return 0;
}

void PrintUsage() {
  std::cerr << "Usage: RvmEngine --pipe <name> --token-stdin [--log-level debug|info|warn|error]\n"
               "       RvmEngine --probe-availability\n"
               "       RvmEngine --probe-hcs-service\n"
               "       RvmEngine --prepare-base-layer\n"
               "       RvmEngine --hcs-smoke <workspace>\n"
               "       RvmEngine --hcs-probe-system <config.json> [--start]\n"
               "       RvmEngine --sandbox-smoke <workspace>\n"
               "       RvmEngine --sandbox-exec <workspace> <command line>\n"
               "       RvmEngine --sandbox-session <workspace> [--overlay-drive R:]\n"
               "       RvmEngine --shell-bridge --pipe <name> --session <id> "
               "[--timeout-ms N] -- <argv...>\n"
               "       RvmEngine --winfsp-probe [drive]\n"
               "       RvmEngine --winfsp-smoke <workspace> [drive]\n"
               "       RvmEngine --version\n";
}

void PrintError(const rvm::core::Error& error) {
  std::cerr << "error=" << rvm::core::ErrorCodeName(error.code) << " message=" << error.message;
  if (!error.detail.empty()) std::cerr << " detail=" << error.detail;
  std::cerr << '\n';
}

std::string ReadAllText(const std::wstring& path) {
  const HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                  nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (file == INVALID_HANDLE_VALUE) return {};
  std::string content;
  char buffer[4096];
  DWORD read = 0;
  while (ReadFile(file, buffer, sizeof(buffer), &read, nullptr) && read > 0) {
    content.append(buffer, read);
  }
  CloseHandle(file);
  return content;
}

bool WriteAllText(const std::wstring& path, std::string_view content) {
  const HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                                  FILE_ATTRIBUTE_NORMAL, nullptr);
  if (file == INVALID_HANDLE_VALUE) return false;
  DWORD written = 0;
  const BOOL ok = WriteFile(file, content.data(), static_cast<DWORD>(content.size()), &written,
                            nullptr);
  CloseHandle(file);
  return ok != 0 && written == content.size();
}

std::wstring NormalizeDrive(const std::wstring& value) {
  std::wstring drive = value;
  if (drive.size() == 1) drive.push_back(L':');
  if (drive.size() != 2 || drive[1] != L':') return {};
  wchar_t letter = drive[0];
  if (letter >= L'a' && letter <= L'z') letter = static_cast<wchar_t>(letter - L'a' + L'A');
  if (letter < L'A' || letter > L'Z') return {};
  drive[0] = letter;
  return drive;
}

int RunWinFspSmoke(const std::wstring& workspace, const std::wstring& drive_argument) {
  const std::wstring drive = NormalizeDrive(drive_argument.empty() ? L"R:" : drive_argument);
  if (drive.empty()) {
    std::cerr << "invalid drive letter: " << std::string(drive_argument.begin(), drive_argument.end())
              << '\n';
    return 64;
  }
  std::error_code fs_error;
  if (!std::filesystem::is_directory(workspace, fs_error)) {
    std::cerr << "workspace directory does not exist\n";
    return 64;
  }

  const std::wstring pid = std::to_wstring(GetCurrentProcessId());
  const std::wstring seed_name = L"rvm-winfsp-seed-" + pid + L".txt";
  const std::wstring new_name = L"rvm-winfsp-new-" + pid + L".txt";
  const std::string seed_base_original = "seed-original\n";

  wchar_t temp_path[MAX_PATH] = {};
  const DWORD temp_length = GetTempPathW(MAX_PATH, temp_path);
  const std::filesystem::path session_root =
      std::filesystem::path(std::wstring(temp_path, temp_length)) /
      (L"rvm-winfsp-" + pid + L"-" + std::to_wstring(GetTickCount64()));
  const std::filesystem::path diff_root = session_root / L"diff";
  const std::filesystem::path tombstone_root = session_root / L"tombstones";
  const std::filesystem::path base_root = session_root / L"base";
  const std::wstring seed_host_path = (base_root / seed_name).wstring();

  auto remove_session = [&]() {
    std::error_code ignored;
    std::filesystem::remove_all(session_root, ignored);
  };

  std::filesystem::create_directories(diff_root, fs_error);
  std::filesystem::create_directories(tombstone_root, fs_error);
  std::filesystem::create_directories(base_root, fs_error);
  if (!WriteAllText(seed_host_path, seed_base_original)) {
    std::cerr << "seed file could not be written into the workspace\n";
    remove_session();
    return 1;
  }

  auto profile = rvm::sandbox::CreateProfile(L"ResponsibleRvmWinFsp" + pid);
  if (!profile.ok()) {
    PrintError(profile.error());
    remove_session();
    return 1;
  }
  std::cout << "appcontainer_sid=" << std::string(profile.value().sid.begin(),
                                                  profile.value().sid.end())
            << '\n';

  rvm::workspace::UnionPaths union_paths;
  union_paths.base_root = base_root.wstring();
  union_paths.diff_root = diff_root.wstring();
  union_paths.tombstone_root = tombstone_root.wstring();
  auto view_result = rvm::workspace::UnionView::Create(std::move(union_paths));
  if (!view_result.ok()) {
    PrintError(view_result.error());
    rvm::sandbox::DeleteProfile(profile.value().name);
    remove_session();
    return 1;
  }
  rvm::workspace::UnionView view = view_result.Take();

  rvm::workspace::MountOptions mount_options;
  mount_options.mount_point = drive;
  mount_options.appcontainer_sid = profile.value().sid;
  auto mounted = rvm::workspace::MountedWorkspace::Mount(&view, mount_options);
  if (!mounted.ok()) {
    PrintError(mounted.error());
    rvm::sandbox::DeleteProfile(profile.value().name);
    remove_session();
    return 1;
  }
  std::cout << "mount_point=" << std::string(drive.begin(), drive.end()) << std::endl;

  const std::wstring seed_drive_path = drive + L"\\" + seed_name;
  const std::wstring new_drive_path = drive + L"\\" + new_name;
  rvm::sandbox::ProcessOptions options;
  options.command_line = L"cmd.exe /c \"type " + seed_drive_path + L" & echo diff-written> " +
                         seed_drive_path + L" & type " + seed_drive_path + L" & echo new-file> " +
                         new_drive_path + L" & del /f /q " + new_drive_path + L" & dir /b " +
                         drive + L"\\\"";
  options.working_directory = drive + L"\\";
  options.containment = rvm::sandbox::Containment::kRestrictedToken;
  options.timeout_ms = 60000;
  options.memory_limit_bytes = 2ull * 1024 * 1024 * 1024;
  options.max_processes = 16;

  std::cout << "sandbox_started=true" << std::endl;
  auto process = rvm::sandbox::RunProcess(options);
  if (!process.ok()) {
    PrintError(process.error());
    mounted.Take()->Unmount();
    rvm::sandbox::DeleteProfile(profile.value().name);
    remove_session();
    return 1;
  }

  const std::string base_after = ReadAllText(seed_host_path);
  const std::string diff_after =
      ReadAllText((diff_root / seed_name).wstring());
  const auto seed_entry = view.Stat(seed_name);
  const auto deleted_entry = view.Stat(new_name);

  const bool base_untouched = base_after == seed_base_original;
  const bool diff_written =
      diff_after.rfind("diff-written", 0) == 0 && diff_after != seed_base_original;
  const bool copy_up_visible = seed_entry.ok() && seed_entry.value().from_diff;
  const bool tombstone_hidden =
      !deleted_entry.ok() && deleted_entry.error().code == rvm::core::ErrorCode::kNotFound;

  std::cout << "sandbox_exit_code=" << process.value().exit_code << '\n';
  std::cout << "sandbox_timed_out=" << (process.value().timed_out ? "true" : "false") << '\n';
  std::cout << "sandbox_output=" << process.value().output << '\n';
  if (!process.value().error.empty()) {
    std::cout << "sandbox_error=" << process.value().error << '\n';
  }
  std::cout << "base_untouched=" << (base_untouched ? "true" : "false") << '\n';
  std::cout << "diff_written=" << (diff_written ? "true" : "false") << '\n';
  std::cout << "copy_up_visible=" << (copy_up_visible ? "true" : "false") << '\n';
  std::cout << "tombstone_hidden=" << (tombstone_hidden ? "true" : "false") << '\n';

  const bool passed = base_untouched && diff_written && copy_up_visible && tombstone_hidden &&
                      process.value().exit_code == 0 && !process.value().timed_out;
  std::cout << "winfsp_smoke=" << (passed ? "passed" : "failed") << '\n';

  mounted.Take()->Unmount();
  const rvm::core::Status deleted = rvm::sandbox::DeleteProfile(profile.value().name);
  if (!deleted.ok()) std::cerr << "warning: " << deleted.error().message << '\n';
  remove_session();
  return passed ? 0 : 1;
}

int RunWinFspProbe(const std::wstring& drive_argument) {
  const std::wstring drive = NormalizeDrive(drive_argument.empty() ? L"R:" : drive_argument);
  if (drive.empty()) {
    std::cerr << "probe: invalid drive letter\n";
    return 64;
  }

  std::error_code fs_error;
  wchar_t temp_path[MAX_PATH] = {};
  const DWORD temp_length = GetTempPathW(MAX_PATH, temp_path);
  const std::filesystem::path session_root =
      std::filesystem::path(std::wstring(temp_path, temp_length)) /
      (L"rvm-probe-" + std::to_wstring(GetCurrentProcessId()) + L"-" +
       std::to_wstring(GetTickCount64()));
  const std::filesystem::path base_root = session_root / L"base";
  const std::filesystem::path diff_root = session_root / L"diff";
  const std::filesystem::path tombstone_root = session_root / L"tombstones";
  std::filesystem::create_directories(base_root, fs_error);
  std::filesystem::create_directories(diff_root, fs_error);
  std::filesystem::create_directories(tombstone_root, fs_error);

  const std::wstring seed_name = L"probe-seed.txt";
  const std::wstring seed_host = (base_root / seed_name).wstring();
  if (!WriteAllText(seed_host, "probe-seed\n")) {
    std::cerr << "probe: seed file could not be written\n";
    return 1;
  }

  rvm::workspace::UnionPaths union_paths;
  union_paths.base_root = base_root.wstring();
  union_paths.diff_root = diff_root.wstring();
  union_paths.tombstone_root = tombstone_root.wstring();
  auto view_result = rvm::workspace::UnionView::Create(std::move(union_paths));
  if (!view_result.ok()) {
    PrintError(view_result.error());
    std::filesystem::remove_all(session_root, fs_error);
    return 1;
  }
  rvm::workspace::UnionView view = view_result.Take();

  rvm::workspace::MountOptions mount_options;
  mount_options.mount_point = drive;
  std::cerr << "probe: mounting " << std::string(drive.begin(), drive.end()) << std::endl;
  auto mounted = rvm::workspace::MountedWorkspace::Mount(&view, mount_options);
  if (!mounted.ok()) {
    PrintError(mounted.error());
    std::filesystem::remove_all(session_root, fs_error);
    return 1;
  }
  std::cerr << "probe: mounted" << std::endl;

  bool passed = true;
  const std::wstring root = drive + L"\\";

  std::cerr << "probe: stat root" << std::endl;
  const DWORD attributes = GetFileAttributesW(root.c_str());
  std::cerr << "probe: root_attributes=" << attributes << " error="
            << (attributes == INVALID_FILE_ATTRIBUTES ? GetLastError() : 0) << std::endl;
  passed = passed && attributes != INVALID_FILE_ATTRIBUTES;

  std::cerr << "probe: enumerate root" << std::endl;
  WIN32_FIND_DATAW find{};
  HANDLE search = FindFirstFileW((root + L"*").c_str(), &find);
  std::size_t entries = 0;
  bool seed_seen = false;
  if (search != INVALID_HANDLE_VALUE) {
    do {
      ++entries;
      if (seed_name == find.cFileName) seed_seen = true;
    } while (FindNextFileW(search, &find));
    FindClose(search);
  }
  std::cerr << "probe: entries=" << entries << " seed_seen=" << (seed_seen ? "true" : "false")
            << std::endl;
  passed = passed && seed_seen;

  std::cerr << "probe: read seed" << std::endl;
  const std::string content = ReadAllText(root + seed_name);
  std::cerr << "probe: seed_content=" << content;
  passed = passed && content == "probe-seed\n";

  std::cerr << "probe: unmount" << std::endl;
  mounted.Take()->Unmount();
  std::cerr << "probe: unmounted" << std::endl;
  std::filesystem::remove_all(session_root, fs_error);
  std::cerr << "winfsp_probe=" << (passed ? "passed" : "failed") << std::endl;
  return passed ? 0 : 1;
}

const char* EngineStateName(rvm::core::EngineState state) {
  switch (state) {
    case rvm::core::EngineState::kCreated:
      return "created";
    case rvm::core::EngineState::kStarting:
      return "starting";
    case rvm::core::EngineState::kReady:
      return "ready";
    case rvm::core::EngineState::kDegraded:
      return "degraded";
    case rvm::core::EngineState::kStopping:
      return "stopping";
    case rvm::core::EngineState::kStopped:
      return "stopped";
    case rvm::core::EngineState::kFailed:
      return "failed";
  }
  return "unknown";
}

bool ParseLogLevel(std::string_view value, rvm::core::LogLevel& level) {
  if (value == "debug") {
    level = rvm::core::LogLevel::kDebug;
    return true;
  }
  if (value == "info") {
    level = rvm::core::LogLevel::kInfo;
    return true;
  }
  if (value == "warn") {
    level = rvm::core::LogLevel::kWarn;
    return true;
  }
  if (value == "error") {
    level = rvm::core::LogLevel::kError;
    return true;
  }
  return false;
}

}  // namespace

int main(int argc, char** argv) {
  EnsureInheritableConsole();

  if (argc > 1 && std::string_view(argv[1]) == "--shell-bridge") {
    int wide_count = 0;
    wchar_t** wide = CommandLineToArgvW(GetCommandLineW(), &wide_count);
    const int result =
        wide != nullptr ? rvm::client::RunShellBridge(wide_count, wide) : 127;
    if (wide != nullptr) LocalFree(wide);
    return result;
  }

  std::string pipe_name;
  bool token_stdin = false;
  bool probe_availability = false;
  bool probe_hcs_service = false;
  bool prepare_base_layer = false;
  bool hcs_smoke = false;
  std::string smoke_workspace;
  bool hcs_probe_system = false;
  bool hcs_probe_start = false;
  bool sandbox_smoke = false;
  bool sandbox_exec = false;
  bool sandbox_debug = false;
  bool sandbox_session = false;
  bool winfsp_smoke = false;
  bool winfsp_probe = false;
  std::string winfsp_drive;
  std::string overlay_drive;
  std::string sandbox_command;
  std::string probe_system_config;
  bool show_version = false;
  bool token_info = false;

  for (int i = 1; i < argc; ++i) {
    const std::string_view argument = argv[i];
    if (argument == "--version") {
      show_version = true;
    } else if (argument == "--token-info") {
      token_info = true;
    } else if (argument == "--probe-availability") {
      probe_availability = true;
    } else if (argument == "--probe-hcs-service") {
      probe_hcs_service = true;
    } else if (argument == "--prepare-base-layer") {
      prepare_base_layer = true;
    } else if (argument == "--hcs-smoke") {
      if (i + 1 >= argc) {
        std::cerr << "--hcs-smoke requires a workspace path\n";
        return 64;
      }
      hcs_smoke = true;
      smoke_workspace = argv[++i];
    } else if (argument == "--hcs-probe-system") {
      if (i + 1 >= argc) {
        std::cerr << "--hcs-probe-system requires a config path\n";
        return 64;
      }
      hcs_probe_system = true;
      probe_system_config = argv[++i];
    } else if (argument == "--sandbox-smoke") {
      if (i + 1 >= argc) {
        std::cerr << "--sandbox-smoke requires a workspace path\n";
        return 64;
      }
      sandbox_smoke = true;
      smoke_workspace = argv[++i];
    } else if (argument == "--sandbox-exec") {
      if (i + 2 >= argc) {
        std::cerr << "--sandbox-exec requires a workspace path and a command line\n";
        return 64;
      }
      sandbox_exec = true;
      smoke_workspace = argv[++i];
      sandbox_command = argv[++i];
    } else if (argument == "--sandbox-session") {
      if (i + 1 >= argc) {
        std::cerr << "--sandbox-session requires a workspace path\n";
        return 64;
      }
      sandbox_session = true;
      smoke_workspace = argv[++i];
    } else if (argument == "--overlay-drive") {
      if (i + 1 >= argc) {
        std::cerr << "--overlay-drive requires a drive letter\n";
        return 64;
      }
      overlay_drive = argv[++i];
    } else if (argument == "--winfsp-probe") {
      winfsp_probe = true;
      if (i + 1 < argc) {
        const std::string_view next = argv[i + 1];
        const bool looks_like_drive =
            (next.size() == 1 && (next[0] | 0x20) >= 'a' && (next[0] | 0x20) <= 'z') ||
            (next.size() == 2 && (next[0] | 0x20) >= 'a' && (next[0] | 0x20) <= 'z' &&
             next[1] == ':');
        if (looks_like_drive) winfsp_drive = argv[++i];
      }
    } else if (argument == "--winfsp-smoke") {
      if (i + 1 >= argc) {
        std::cerr << "--winfsp-smoke requires a workspace path\n";
        return 64;
      }
      winfsp_smoke = true;
      smoke_workspace = argv[++i];
      if (i + 1 < argc) {
        const std::string_view next = argv[i + 1];
        const bool looks_like_drive =
            (next.size() == 1 && (next[0] | 0x20) >= 'a' && (next[0] | 0x20) <= 'z') ||
            (next.size() == 2 && (next[0] | 0x20) >= 'a' && (next[0] | 0x20) <= 'z' &&
             next[1] == ':');
        if (looks_like_drive) winfsp_drive = argv[++i];
      }
    } else if (argument == "--sandbox-debug") {
      if (i + 2 >= argc) {
        std::cerr << "--sandbox-debug requires a workspace path and a command line\n";
        return 64;
      }
      sandbox_debug = true;
      smoke_workspace = argv[++i];
      sandbox_command = argv[++i];
    } else if (argument == "--start") {
      hcs_probe_start = true;
    } else if (argument == "--help" || argument == "-h") {
      PrintUsage();
      return 0;
    } else if (argument == "--pipe") {
      if (i + 1 >= argc) {
        std::cerr << "--pipe requires a value\n";
        return 64;
      }
      pipe_name = argv[++i];
    } else if (argument == "--token-stdin") {
      token_stdin = true;
    } else if (argument == "--log-level") {
      if (i + 1 >= argc) {
        std::cerr << "--log-level requires a value\n";
        return 64;
      }
      rvm::core::LogLevel level = rvm::core::LogLevel::kInfo;
      if (!ParseLogLevel(argv[i + 1], level)) {
        std::cerr << "unknown log level: " << argv[i + 1] << '\n';
        return 64;
      }
      rvm::core::SetLogLevel(level);
      ++i;
    } else {
      std::cerr << "unknown argument: " << argument << '\n';
      PrintUsage();
      return 64;
    }
  }

  if (token_info) {
    return PrintTokenInfo();
  }

  if (show_version) {
    std::cout << "engine_version=" << rvm::core::kEngineVersion << '\n';
    std::cout << "protocol_version=" << rvm::core::kProtocolVersion << '\n';
    std::cout << "capability=" << rvm::core::kCapability << '\n';
    return 0;
  }

  if (probe_availability) {
    const rvm::environment::SystemAvailabilityProbe probe;
    std::cout << probe.Probe().Summary();
    return 0;
  }

  if (probe_hcs_service) {
    auto hcs = rvm::hyperv::HcsApi::Load();
    if (!hcs.ok()) {
      PrintError(hcs.error());
      return 1;
    }
    const auto properties =
        hcs.value().GetServiceProperties(R"({"PropertyTypes":["Basic"]})");
    if (!properties.ok()) {
      PrintError(properties.error());
      return 1;
    }
    std::cout << "hcs_service_properties=" << properties.value() << '\n';
    return 0;
  }

  if (prepare_base_layer) {
    const rvm::hyperv::BaseLayerStore store = rvm::hyperv::BaseLayerStore::Default();
    const auto layer = store.Ensure(true);
    if (!layer.ok()) {
      PrintError(layer.error());
      return 1;
    }
    std::cout << "base_layer_path=" << rvm::hyperv::WideToUtf8(layer.value().path) << '\n';
    std::cout << "base_layer_id=" << layer.value().id << '\n';
    std::cout << "windows_build=" << layer.value().windows_build << '\n';
    return 0;
  }

  if (hcs_smoke) {
    const auto wide_workspace = rvm::channel::FromUtf8(smoke_workspace);
    if (!wide_workspace.ok()) {
      std::cerr << "workspace path is not valid UTF-8\n";
      return 64;
    }
    rvm::runtime::EnvironmentSpec spec;
    spec.session_id = L"smoke";
    spec.workspace_root = wide_workspace.value();
    spec.memory_mb = 2048;
    spec.vcpu_count = 2;
    spec.policy.mode = rvm::core::SandboxMode::kWorkspaceWrite;
    spec.policy.approval = rvm::core::ApprovalMode::kAsk;
    spec.policy.network_access = false;

    std::unique_ptr<rvm::runtime::IEnvironmentBackend> backend =
        rvm::hyperv::CreateHcsBackend();
    auto environment = backend->Create(spec);
    if (!environment.ok()) {
      PrintError(environment.error());
      return 1;
    }
    auto facts = environment.value()->Start();
    if (!facts.ok()) {
      PrintError(facts.error());
      return 1;
    }
    std::cout << "hcs_state=ready\n";
    std::cout << "windows_build="
              << std::string(facts.value().windows_build.begin(), facts.value().windows_build.end())
              << '\n';
    std::cout << "memory_mb=" << facts.value().memory_mb << '\n';
    std::cout << "vbs=" << (facts.value().vbs_enabled ? "true" : "false") << '\n';
    std::cout << "hvci=" << (facts.value().hvci_enabled ? "true" : "false") << '\n';
    const rvm::core::Status reset = environment.value()->Reset();
    if (!reset.ok()) {
      PrintError(reset.error());
      return 1;
    }
    std::cout << "hcs_state=reset\n";
    return 0;
  }

  if (winfsp_probe) {
    auto wide_drive = rvm::channel::FromUtf8(winfsp_drive);
    if (!wide_drive.ok()) {
      std::cerr << "drive must be valid UTF-8\n";
      return 64;
    }
    return RunWinFspProbe(wide_drive.value());
  }

  if (winfsp_smoke) {
    auto wide_workspace = rvm::channel::FromUtf8(smoke_workspace);
    auto wide_drive = rvm::channel::FromUtf8(winfsp_drive);
    if (!wide_workspace.ok() || !wide_drive.ok()) {
      std::cerr << "workspace and drive must be valid UTF-8\n";
      return 64;
    }
    return RunWinFspSmoke(wide_workspace.value(), wide_drive.value());
  }

  if (sandbox_debug) {
    const auto wide_workspace = rvm::channel::FromUtf8(smoke_workspace);
    auto decoded_command = rvm::channel::FromUtf8(sandbox_command);
    if (!wide_workspace.ok() || !decoded_command.ok()) {
      std::cerr << "workspace and command line must be valid UTF-8\n";
      return 64;
    }
    auto profile = rvm::sandbox::CreateProfile(
        L"ResponsibleRvmDebug" + std::to_wstring(GetCurrentProcessId()));
    if (!profile.ok()) {
      PrintError(profile.error());
      return 1;
    }
    std::cout << "appcontainer_sid=" << std::string(profile.value().sid.begin(),
                                                    profile.value().sid.end())
              << '\n';
    std::vector<std::wstring> granted_ancestors;
    auto revoke_grants = [&profile, &granted_ancestors]() {
      for (auto it = granted_ancestors.rbegin(); it != granted_ancestors.rend(); ++it) {
        rvm::sandbox::RevokeDirectory(profile.value().sid, *it, false);
      }
    };
    const rvm::core::Status granted = rvm::sandbox::GrantDirectory(
        profile.value().sid, wide_workspace.value(), true, true);
    if (!granted.ok()) {
      PrintError(granted.error());
      rvm::sandbox::DeleteProfile(profile.value().name);
      return 1;
    }
    const rvm::core::Status ancestors = rvm::sandbox::GrantTraverseAncestors(
        profile.value().sid, wide_workspace.value(), &granted_ancestors);
    std::cout << "ancestors_granted=" << (ancestors.ok() ? "true" : "false") << '\n';

    PSID sid = nullptr;
    if (!ConvertStringSidToSidW(profile.value().sid.c_str(), &sid)) {
      std::cerr << "failed to parse the AppContainer SID\n";
      revoke_grants();
      rvm::sandbox::RevokeDirectory(profile.value().sid, wide_workspace.value(), true);
      rvm::sandbox::DeleteProfile(profile.value().name);
      return 1;
    }
    SECURITY_CAPABILITIES capabilities{};
    capabilities.AppContainerSid = sid;

    SIZE_T attribute_size = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &attribute_size);
    std::vector<std::uint8_t> attribute_buffer(attribute_size);
    auto* attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attribute_buffer.data());
    InitializeProcThreadAttributeList(attributes, 1, 0, &attribute_size);
    UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES,
                              &capabilities, sizeof(capabilities), nullptr, nullptr);

    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    SECURITY_ATTRIBUTES inherit{};
    inherit.nLength = sizeof(inherit);
    inherit.bInheritHandle = TRUE;
    const HANDLE nul = CreateFileW(L"NUL", GENERIC_READ | GENERIC_WRITE,
                                   FILE_SHARE_READ | FILE_SHARE_WRITE, &inherit, OPEN_EXISTING, 0,
                                   nullptr);
    startup.StartupInfo.hStdInput = nul;
    startup.StartupInfo.hStdOutput = nul;
    startup.StartupInfo.hStdError = nul;
    startup.lpAttributeList = attributes;

    std::wstring command = decoded_command.Take();
    command.push_back(L'\0');
    PROCESS_INFORMATION process{};
    const BOOL created = CreateProcessW(
        nullptr, command.data(), nullptr, nullptr, TRUE,
        DEBUG_ONLY_THIS_PROCESS | EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
        nullptr, wide_workspace.value().c_str(), &startup.StartupInfo, &process);
    DeleteProcThreadAttributeList(attributes);
    LocalFree(sid);
    if (!created) {
      std::cerr << "CreateProcessW failed: " << GetLastError() << '\n';
      if (nul != INVALID_HANDLE_VALUE) CloseHandle(nul);
      revoke_grants();
      rvm::sandbox::RevokeDirectory(profile.value().sid, wide_workspace.value(), true);
      rvm::sandbox::DeleteProfile(profile.value().name);
      return 1;
    }
    std::cout << "process_created pid=" << process.dwProcessId << '\n';

    bool exit_seen = false;
    while (!exit_seen) {
      DEBUG_EVENT event{};
      if (!WaitForDebugEvent(&event, 30000)) {
        std::cout << "debug_wait_timeout\n";
        TerminateProcess(process.hProcess, 1);
        break;
      }
      DWORD continue_status = DBG_CONTINUE;
      if (event.dwDebugEventCode == CREATE_PROCESS_DEBUG_EVENT) {
        if (event.u.CreateProcessInfo.hFile != nullptr) {
          CloseHandle(event.u.CreateProcessInfo.hFile);
        }
      } else if (event.dwDebugEventCode == LOAD_DLL_DEBUG_EVENT) {
        wchar_t path[1024] = {};
        std::string name = "?";
        if (event.u.LoadDll.hFile != nullptr) {
          const DWORD length = GetFinalPathNameByHandleW(
              event.u.LoadDll.hFile, path, static_cast<DWORD>(std::size(path)),
              FILE_NAME_NORMALIZED);
          if (length > 0) {
            auto utf8 = rvm::channel::ToUtf8(std::wstring(path, length));
            if (utf8.ok()) name = utf8.value();
          }
          CloseHandle(event.u.LoadDll.hFile);
        }
        std::cout << "load_dll=" << name << '\n';
      } else if (event.dwDebugEventCode == EXCEPTION_DEBUG_EVENT) {
        std::cout << "exception code=" << event.u.Exception.ExceptionRecord.ExceptionCode
                  << " address=" << event.u.Exception.ExceptionRecord.ExceptionAddress
                  << " first_chance=" << event.u.Exception.dwFirstChance << '\n';
        continue_status = DBG_EXCEPTION_NOT_HANDLED;
      } else if (event.dwDebugEventCode == EXIT_PROCESS_DEBUG_EVENT) {
        std::cout << "exit_code=" << event.u.ExitProcess.dwExitCode << '\n';
        exit_seen = true;
      }
      ContinueDebugEvent(event.dwProcessId, event.dwThreadId, continue_status);
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    if (nul != INVALID_HANDLE_VALUE) CloseHandle(nul);
    revoke_grants();
    rvm::sandbox::RevokeDirectory(profile.value().sid, wide_workspace.value(), true);
    rvm::sandbox::DeleteProfile(profile.value().name);
    return 0;
  }

  if (sandbox_session) {
    const auto wide_workspace = rvm::channel::FromUtf8(smoke_workspace);
    if (!wide_workspace.ok()) {
      std::cerr << "workspace path is not valid UTF-8\n";
      return 64;
    }
    auto backend = rvm::sandbox::CreateSandboxBackend();
    const rvm::runtime::BackendProbe probe = backend->Probe();
    std::cout << "backend_ready=" << (probe.ready ? "true" : "false") << '\n';
    std::cout << "backend_detail=" << probe.detail << '\n';

    rvm::runtime::EnvironmentSpec spec;
    spec.session_id = L"session-smoke";
    spec.workspace_root = wide_workspace.value();
    spec.policy.mode = rvm::core::SandboxMode::kWorkspaceWrite;
    spec.policy.approval = rvm::core::ApprovalMode::kAsk;
    spec.policy.network_access = false;
    if (!overlay_drive.empty()) {
      auto wide_drive = rvm::channel::FromUtf8(overlay_drive);
      if (!wide_drive.ok()) {
        std::cerr << "--overlay-drive must be valid UTF-8\n";
        return 64;
      }
      spec.overlay_workspace = true;
      spec.overlay_drive = wide_drive.value();
    }

    auto environment = backend->Create(spec);
    if (!environment.ok()) {
      PrintError(environment.error());
      return 1;
    }
    auto facts = environment.value()->Start();
    if (!facts.ok()) {
      PrintError(facts.error());
      return 1;
    }
    std::cout << "session_state=ready\n";
    std::cout << "workspace_path="
              << std::string(facts.value().workspace_path.begin(),
                             facts.value().workspace_path.end())
              << '\n';
    std::cout << "windows_build="
              << std::string(facts.value().windows_build.begin(),
                             facts.value().windows_build.end())
              << '\n';
    std::cout << "memory_mb=" << facts.value().memory_mb << '\n';
    std::cout << "vcpu_count=" << facts.value().vcpu_count << '\n';
    std::cout << "vbs=" << (facts.value().vbs_enabled ? "true" : "false") << '\n';
    std::cout << "hvci=" << (facts.value().hvci_enabled ? "true" : "false") << '\n';
    const rvm::core::Status stopped = environment.value()->Stop();
    if (!stopped.ok()) {
      PrintError(stopped.error());
      return 1;
    }
    std::cout << "session_state=stopped\n";
    return 0;
  }

  if (sandbox_smoke || sandbox_exec) {
    const bool exec_mode = sandbox_exec;
    const auto wide_workspace = rvm::channel::FromUtf8(smoke_workspace);
    if (!wide_workspace.ok()) {
      std::cerr << "workspace path is not valid UTF-8\n";
      return 64;
    }
    auto profile = rvm::sandbox::CreateProfile(
        (exec_mode ? L"ResponsibleRvmExec" : L"ResponsibleRvmSmoke") +
        std::to_wstring(GetCurrentProcessId()));
    if (!profile.ok()) {
      PrintError(profile.error());
      return 1;
    }
    std::cout << "appcontainer_sid=" << std::string(profile.value().sid.begin(),
                                                    profile.value().sid.end())
              << '\n';

    std::vector<std::wstring> granted_ancestors;
    auto cleanup = [&profile, &granted_ancestors, &wide_workspace]() {
      for (auto it = granted_ancestors.rbegin(); it != granted_ancestors.rend(); ++it) {
        rvm::sandbox::RevokeDirectory(profile.value().sid, *it, false);
      }
      rvm::sandbox::RevokeDirectory(profile.value().sid, wide_workspace.value(), true);
      const rvm::core::Status deleted = rvm::sandbox::DeleteProfile(profile.value().name);
      if (!deleted.ok()) {
        std::cerr << "warning: " << deleted.error().message << '\n';
      }
    };

    const rvm::core::Status granted = rvm::sandbox::GrantDirectory(
        profile.value().sid, wide_workspace.value(), true, true);
    if (!granted.ok()) {
      PrintError(granted.error());
      cleanup();
      return 1;
    }
    std::cout << "workspace_granted=true\n";

    const rvm::core::Status ancestors = rvm::sandbox::GrantTraverseAncestors(
        profile.value().sid, wide_workspace.value(), &granted_ancestors);
    std::cout << "ancestors_granted=" << (ancestors.ok() ? "true" : "false") << '\n';
    if (!ancestors.ok()) {
      std::cerr << "warning: " << ancestors.error().message
                << " detail=" << ancestors.error().detail << '\n';
    }

    std::wstring marker;
    std::wstring command;
    if (exec_mode) {
      auto decoded = rvm::channel::FromUtf8(sandbox_command);
      if (!decoded.ok()) {
        std::cerr << "command line is not valid UTF-8\n";
        cleanup();
        return 64;
      }
      command = decoded.Take();
    } else {
      marker = wide_workspace.value() + L"\\rvm-smoke.txt";
      command = L"cmd.exe /c \"echo cwd=%CD% & dir C:\\Users & echo ---users-end--- & "
                L"ping -n 1 baidu.com & echo ---ping-end--- & "
                L"echo rvm-smoke> \"" + marker + L"\" & type \"" + marker + L"\" & "
                L"echo ---write-end---\"";
    }
    rvm::sandbox::ProcessOptions options;
    options.command_line = std::move(command);
    options.working_directory = wide_workspace.value();
    options.appcontainer_sid = profile.value().sid;
    options.timeout_ms = 60000;
    options.memory_limit_bytes = 2ull * 1024 * 1024 * 1024;
    options.max_processes = 16;

    auto result = rvm::sandbox::RunProcess(options);
    if (!result.ok()) {
      PrintError(result.error());
      cleanup();
      return 1;
    }
    std::cout << "exit_code=" << result.value().exit_code << '\n';
    std::cout << "timed_out=" << (result.value().timed_out ? "true" : "false") << '\n';
    std::cout << "--- stdout ---\n" << result.value().output << "\n--- stderr ---\n"
              << result.value().error << "\n--- end ---\n";

    if (!marker.empty()) DeleteFileW(marker.c_str());
    cleanup();
    return result.value().exit_code == 0 ? 0 : 1;
  }

  if (hcs_probe_system) {
    std::ifstream file(probe_system_config, std::ios::binary);
    if (!file) {
      std::cerr << "failed to open the compute system config\n";
      return 64;
    }
    const std::string json((std::istreambuf_iterator<char>(file)),
                           std::istreambuf_iterator<char>());
    auto hcs = rvm::hyperv::HcsApi::Load();
    if (!hcs.ok()) {
      PrintError(hcs.error());
      return 1;
    }
    const std::wstring id =
        L"rvm-probe-" + std::to_wstring(GetCurrentProcessId()) + L"-" +
        std::to_wstring(GetTickCount64());
    auto created = hcs.value().CreateComputeSystem(id, json, 180000);
    if (!created.ok()) {
      PrintError(created.error());
      return 1;
    }
    std::cout << "system_created=true\n" << std::flush;
    if (hcs_probe_start) {
      const rvm::core::Status started = hcs.value().StartComputeSystem(created.value(), 180000);
      if (!started.ok()) {
        PrintError(started.error());
        hcs.value().TerminateComputeSystem(created.value(), 30000);
        hcs.value().CloseComputeSystem(created.value());
        return 1;
      }
      std::cout << "system_started=true\n";
      const auto properties =
          hcs.value().GetComputeSystemProperties(created.value(), R"({"PropertyTypes":["Basic"]})");
      if (properties.ok()) std::cout << "system_properties=" << properties.value() << '\n';
    }
    hcs.value().TerminateComputeSystem(created.value(), 30000);
    hcs.value().CloseComputeSystem(created.value());
    return 0;
  }

  if (pipe_name.empty()) {
    PrintUsage();
    return 64;
  }
  if (!token_stdin) {
    std::cerr << "--token-stdin is required in serve mode\n";
    return 64;
  }
  std::string token;
  if (!std::getline(std::cin, token)) {
    std::cerr << "failed to read the engine token from stdin\n";
    return 64;
  }
  token = rvm::security::TrimTokenLine(token);
  if (token.empty()) {
    std::cerr << "engine token must not be empty\n";
    return 64;
  }

  const auto wide_pipe = rvm::channel::FromUtf8(pipe_name);
  if (!wide_pipe.ok()) {
    std::cerr << "pipe name is not valid UTF-8\n";
    return 64;
  }

  rvm::core::EngineConfig config;
  config.pipe_name = wide_pipe.value();

  rvm::core::Engine engine(config);
  const rvm::core::Status status = engine.Initialize();
  if (!status.ok()) {
    std::cerr << "engine initialization failed: " << status.error().message << '\n';
    return 1;
  }

  std::cout << "state=" << EngineStateName(engine.state()) << std::endl;
  if (!engine.availability().rvm_available()) {
    std::cerr << "reason=" << engine.availability().reason() << '\n';
  }

  std::unique_ptr<rvm::runtime::IEnvironmentBackend> backend =
      rvm::sandbox::CreateSandboxBackend();
  rvm::server::SessionManager sessions(*backend, engine.availability());

  rvm::server::PipeServer server(engine, {wide_pipe.value(), token}, &sessions);
  const rvm::core::Status serve_status = server.Run();
  if (!serve_status.ok()) {
    std::cerr << "engine pipe failed: " << serve_status.error().message;
    if (!serve_status.error().detail.empty()) {
      std::cerr << " (" << serve_status.error().detail << ")";
    }
    std::cerr << '\n';
    return 1;
  }
  return 0;
}