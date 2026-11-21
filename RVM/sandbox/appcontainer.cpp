#include "sandbox/appcontainer.h"

#include <windows.h>

#include <aclapi.h>
#include <sddl.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "core/log.h"

namespace rvm::sandbox {
namespace {

using CreateAppContainerProfileFn = HRESULT(WINAPI*)(PCWSTR, PCWSTR, PCWSTR, PSID_AND_ATTRIBUTES,
                                                    DWORD, PSID*);
using DeleteAppContainerProfileFn = HRESULT(WINAPI*)(PCWSTR);
using DeriveAppContainerSidFn = HRESULT(WINAPI*)(PCWSTR, PSID*);
using DeriveCapabilitySidsFromNameFn = BOOL(WINAPI*)(PCWSTR, PSID**, PDWORD, PSID**, PDWORD);

std::string LastErrorDetail(DWORD code) {
  return "win32=" + std::to_string(code);
}

std::string HresultDetail(HRESULT hr) {
  char buffer[16] = {};
  std::snprintf(buffer, sizeof(buffer), "0x%08X", static_cast<unsigned>(hr));
  return buffer;
}

core::Result<std::wstring> SidToString(PSID sid) {
  LPWSTR text = nullptr;
  if (!ConvertSidToStringSidW(sid, &text)) {
    return core::Error(core::ErrorCode::kBackendFailed, "failed to format the AppContainer SID",
                       false, LastErrorDetail(GetLastError()));
  }
  std::wstring result(text);
  LocalFree(text);
  return core::Ok(std::move(result));
}

bool IsValidProfileName(std::wstring_view name) {
  if (name.empty() || name.size() > 64) return false;
  for (const wchar_t ch : name) {
    const bool allowed = (ch >= L'a' && ch <= L'z') || (ch >= L'A' && ch <= L'Z') ||
                         (ch >= L'0' && ch <= L'9') || ch == L'.' || ch == L'_' || ch == L'-';
    if (!allowed) return false;
  }
  return true;
}

core::Status GrantPathRights(const std::wstring& sid, const std::wstring& path, DWORD rights,
                             bool inherit) {
  PSID psid = nullptr;
  if (!ConvertStringSidToSidW(sid.c_str(), &psid)) {
    return core::Status::Failure(core::Error(core::ErrorCode::kInvalidArgument,
                                             "AppContainer SID is not valid", false,
                                             LastErrorDetail(GetLastError())));
  }

  EXPLICIT_ACCESSW entry{};
  entry.grfAccessPermissions = rights;
  entry.grfAccessMode = GRANT_ACCESS;
  entry.grfInheritance = inherit ? (OBJECT_INHERIT_ACE | CONTAINER_INHERIT_ACE) : NO_INHERITANCE;
  entry.Trustee.TrusteeForm = TRUSTEE_IS_SID;
  entry.Trustee.TrusteeType = TRUSTEE_IS_UNKNOWN;
  entry.Trustee.ptstrName = static_cast<LPWSTR>(psid);

  PSECURITY_DESCRIPTOR descriptor = nullptr;
  PACL old_acl = nullptr;
  DWORD status = GetNamedSecurityInfoW(path.c_str(), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
                                       nullptr, nullptr, &old_acl, nullptr, &descriptor);
  if (status != ERROR_SUCCESS) {
    LocalFree(psid);
    return core::Status::Failure(core::Error(core::ErrorCode::kPolicyDenied,
                                             "failed to read the directory ACL", false,
                                             LastErrorDetail(status) + " path=" +
                                                 std::string(path.begin(), path.end())));
  }

  PACL new_acl = nullptr;
  status = SetEntriesInAclW(1, &entry, old_acl, &new_acl);
  if (status == ERROR_SUCCESS) {
    status = SetNamedSecurityInfoW(const_cast<LPWSTR>(path.c_str()), SE_FILE_OBJECT,
                                   DACL_SECURITY_INFORMATION, nullptr, nullptr, new_acl, nullptr);
  }
  if (new_acl != nullptr) LocalFree(new_acl);
  LocalFree(descriptor);
  LocalFree(psid);

  if (status != ERROR_SUCCESS) {
    return core::Status::Failure(core::Error(core::ErrorCode::kPolicyDenied,
                                             "failed to grant AppContainer access", false,
                                             LastErrorDetail(status)));
  }
  return core::Status::Ok();
}

struct ScopedHandles {
  HANDLE process = nullptr;
  HANDLE thread = nullptr;
  HANDLE job = nullptr;
  HANDLE stdin_read = nullptr;
  HANDLE stdin_write = nullptr;
  HANDLE stdout_read = nullptr;
  HANDLE stdout_write = nullptr;
  HANDLE stderr_read = nullptr;
  HANDLE stderr_write = nullptr;
  HANDLE token = nullptr;

  ~ScopedHandles() {
    const HANDLE handles[] = {process, thread, job, stdin_read, stdin_write, stdout_read,
                              stdout_write, stderr_read, stderr_write, token};
    for (const HANDLE handle : handles) {
      if (handle != nullptr && handle != INVALID_HANDLE_VALUE) CloseHandle(handle);
    }
  }
};

core::Status CreateStdioPipe(HANDLE* read_end, HANDLE* write_end) {
  SECURITY_ATTRIBUTES inherit{};
  inherit.nLength = sizeof(inherit);
  inherit.bInheritHandle = TRUE;
  if (!CreatePipe(read_end, write_end, &inherit, 0)) {
    return core::Status::Failure(core::Error(core::ErrorCode::kBackendFailed,
                                             "failed to create a stdio pipe", false,
                                             LastErrorDetail(GetLastError())));
  }
  return core::Status::Ok();
}

struct CaptureState {
  std::mutex sink_mutex;
  const ProcessOptions* options = nullptr;
  std::uint64_t cap = 0;
  std::string output;
  std::string error;
  bool output_truncated = false;
  bool error_truncated = false;
};

void CaptureStream(HANDLE handle, bool is_error, CaptureState& state) {
  std::array<std::uint8_t, 16 * 1024> buffer{};
  for (;;) {
    DWORD read = 0;
    if (!ReadFile(handle, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr) ||
        read == 0) {
      break;
    }
    std::string& target = is_error ? state.error : state.output;
    bool& truncated = is_error ? state.error_truncated : state.output_truncated;
    std::size_t keep = read;
    if (state.cap != 0 && target.size() + keep > state.cap) {
      keep = state.cap > target.size() ? static_cast<std::size_t>(state.cap - target.size()) : 0;
      truncated = true;
    }
    if (keep == 0) continue;
    target.append(reinterpret_cast<const char*>(buffer.data()), keep);
    if (!state.options->sink) continue;
    std::lock_guard<std::mutex> lock(state.sink_mutex);
    try {
      state.options->sink(is_error, buffer.data(), keep);
    } catch (...) {
      // The sink belongs to the caller; a throwing sink must not take down the
      // capture thread. Its own channel reports the failure.
    }
  }
}

void JoinCaptureThread(std::thread& thread) {
  if (!thread.joinable()) return;
  const HANDLE native = reinterpret_cast<HANDLE>(thread.native_handle());
  if (WaitForSingleObject(native, 5000) == WAIT_TIMEOUT) {
    CancelSynchronousIo(native);
  }
  thread.join();
}

}  // namespace

core::Result<AppContainerProfile> CreateProfile(std::wstring_view name) {
  if (!IsValidProfileName(name)) {
    return core::Error(core::ErrorCode::kInvalidArgument,
                       "AppContainer profile names must be 1..64 alphanumeric characters");
  }

  HMODULE module = LoadLibraryW(L"userenv.dll");
  if (module == nullptr) {
    return core::Error(core::ErrorCode::kUnavailable, "userenv.dll is not available", false,
                       LastErrorDetail(GetLastError()));
  }
  const auto create = reinterpret_cast<CreateAppContainerProfileFn>(
      GetProcAddress(module, "CreateAppContainerProfile"));
  const auto derive = reinterpret_cast<DeriveAppContainerSidFn>(
      GetProcAddress(module, "DeriveAppContainerSidFromAppContainerName"));
  if (create == nullptr || derive == nullptr) {
    FreeLibrary(module);
    return core::Error(core::ErrorCode::kNotImplemented,
                       "AppContainer profile APIs are missing from userenv.dll");
  }

  const std::wstring profile_name(name);
  PSID sid = nullptr;
  HRESULT hr = create(profile_name.c_str(), profile_name.c_str(),
                      L"Responsible RVM sandbox profile", nullptr, 0, &sid);
  if (hr == HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)) {
    hr = derive(profile_name.c_str(), &sid);
  }
  if (FAILED(hr) || sid == nullptr) {
    FreeLibrary(module);
    return core::Error(core::ErrorCode::kBackendFailed,
                       "failed to create the AppContainer profile", false, HresultDetail(hr));
  }

  auto text = SidToString(sid);
  FreeSid(sid);
  FreeLibrary(module);
  if (!text.ok()) return text.error();
  return core::Ok(AppContainerProfile{profile_name, text.Take()});
}

core::Result<std::wstring> DeriveCapabilitySid(std::wstring_view name) {
  // The export lives in kernelbase.dll on current builds and is not forwarded
// by kernel32.dll, so probe both before declaring the API unavailable.
  DeriveCapabilitySidsFromNameFn derive = nullptr;
  for (const wchar_t* module_name : {L"kernel32.dll", L"kernelbase.dll"}) {
    HMODULE module = GetModuleHandleW(module_name);
    if (module == nullptr) module = LoadLibraryW(module_name);
    if (module == nullptr) continue;
    derive = reinterpret_cast<DeriveCapabilitySidsFromNameFn>(
        GetProcAddress(module, "DeriveCapabilitySidsFromName"));
    if (derive != nullptr) break;
  }
  if (derive == nullptr) {
    return core::Error(core::ErrorCode::kNotImplemented,
                       "DeriveCapabilitySidsFromName is missing from the system");
  }

  PSID* group_sids = nullptr;
  DWORD group_count = 0;
  PSID* capability_sids = nullptr;
  DWORD capability_count = 0;
  const std::wstring capability_name(name);
  if (!derive(capability_name.c_str(), &group_sids, &group_count, &capability_sids,
              &capability_count)) {
    return core::Error(core::ErrorCode::kBackendFailed,
                       "failed to derive the capability SID", false,
                       LastErrorDetail(GetLastError()));
  }
  core::Result<std::wstring> sid_text = core::Error(
      core::ErrorCode::kBackendFailed, "capability derivation returned no SID");
  if (capability_count > 0 && capability_sids != nullptr && capability_sids[0] != nullptr) {
    sid_text = SidToString(capability_sids[0]);
  }
  for (DWORD i = 0; i < capability_count; ++i) {
    if (capability_sids != nullptr && capability_sids[i] != nullptr) {
      LocalFree(capability_sids[i]);
    }
  }
  for (DWORD i = 0; i < group_count; ++i) {
    if (group_sids != nullptr && group_sids[i] != nullptr) LocalFree(group_sids[i]);
  }
  LocalFree(capability_sids);
  LocalFree(group_sids);
  return sid_text;
}

core::Status DeleteProfile(std::wstring_view name) {
  HMODULE module = LoadLibraryW(L"userenv.dll");
  if (module == nullptr) {
    return core::Status::Failure(core::Error(core::ErrorCode::kUnavailable,
                                             "userenv.dll is not available", false,
                                             LastErrorDetail(GetLastError())));
  }
  const auto remove = reinterpret_cast<DeleteAppContainerProfileFn>(
      GetProcAddress(module, "DeleteAppContainerProfile"));
  if (remove == nullptr) {
    FreeLibrary(module);
    return core::Status::Failure(core::Error(core::ErrorCode::kNotImplemented,
                                             "DeleteAppContainerProfile is missing"));
  }
  const HRESULT hr = remove(std::wstring(name).c_str());
  FreeLibrary(module);
  if (FAILED(hr)) {
    return core::Status::Failure(core::Error(core::ErrorCode::kBackendFailed,
                                             "failed to delete the AppContainer profile", false,
                                             HresultDetail(hr)));
  }
  return core::Status::Ok();
}

core::Status GrantDirectory(const std::wstring& sid, const std::wstring& path, bool write,
                            bool inherit) {
  const DWORD rights = (write ? (GENERIC_READ | GENERIC_WRITE | DELETE | FILE_TRAVERSE)
                              : (GENERIC_READ | FILE_TRAVERSE));
  return GrantPathRights(sid, path, rights, inherit);
}

core::Status GrantDirectoryReadExecute(const std::wstring& sid, const std::wstring& path,
                                       bool inherit) {
  return GrantPathRights(sid, path, GENERIC_READ | GENERIC_EXECUTE | FILE_TRAVERSE, inherit);
}

core::Status GrantTraverseAncestors(const std::wstring& sid, const std::wstring& path,
                                    std::vector<std::wstring>* granted) {
  const std::filesystem::path root(path);
  std::error_code error;
  for (std::filesystem::path current = root.parent_path(); !current.empty();
       current = current.parent_path()) {
    if (!std::filesystem::is_directory(current, error) || error) break;
    const std::wstring current_text = current.wstring();
    core::LogDebug("appcontainer", "grant traverse " +
                                       std::string(current_text.begin(), current_text.end()));
    const core::Status entry =
        GrantPathRights(sid, current.wstring(), FILE_TRAVERSE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
                        false);
    if (!entry.ok()) return entry;
    if (granted != nullptr) granted->push_back(current.wstring());
    if (current == current.root_path()) break;
  }
  return core::Status::Ok();
}

core::Status RevokeDirectory(const std::wstring& sid, const std::wstring& path, bool inherit) {
  PSID psid = nullptr;
  if (!ConvertStringSidToSidW(sid.c_str(), &psid)) {
    return core::Status::Failure(core::Error(core::ErrorCode::kInvalidArgument,
                                             "AppContainer SID is not valid", false,
                                             LastErrorDetail(GetLastError())));
  }

  EXPLICIT_ACCESSW entry{};
  entry.grfAccessPermissions = 0;
  entry.grfAccessMode = REVOKE_ACCESS;
  entry.grfInheritance = inherit ? (OBJECT_INHERIT_ACE | CONTAINER_INHERIT_ACE) : NO_INHERITANCE;
  entry.Trustee.TrusteeForm = TRUSTEE_IS_SID;
  entry.Trustee.TrusteeType = TRUSTEE_IS_UNKNOWN;
  entry.Trustee.ptstrName = static_cast<LPWSTR>(psid);

  PSECURITY_DESCRIPTOR descriptor = nullptr;
  PACL old_acl = nullptr;
  DWORD status = GetNamedSecurityInfoW(path.c_str(), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
                                       nullptr, nullptr, &old_acl, nullptr, &descriptor);
  if (status != ERROR_SUCCESS) {
    LocalFree(psid);
    return core::Status::Failure(core::Error(core::ErrorCode::kPolicyDenied,
                                             "failed to read the directory ACL", false,
                                             LastErrorDetail(status)));
  }

  PACL new_acl = nullptr;
  status = SetEntriesInAclW(1, &entry, old_acl, &new_acl);
  if (status == ERROR_SUCCESS) {
    status = SetNamedSecurityInfoW(const_cast<LPWSTR>(path.c_str()), SE_FILE_OBJECT,
                                   DACL_SECURITY_INFORMATION, nullptr, nullptr, new_acl, nullptr);
  }
  if (new_acl != nullptr) LocalFree(new_acl);
  LocalFree(descriptor);
  LocalFree(psid);

  if (status != ERROR_SUCCESS) {
    return core::Status::Failure(core::Error(core::ErrorCode::kPolicyDenied,
                                             "failed to revoke AppContainer access", false,
                                             LastErrorDetail(status)));
  }
  return core::Status::Ok();
}

core::Result<ProcessResult> RunProcess(const ProcessOptions& options) {
  if (options.command_line.empty()) {
    return core::Error(core::ErrorCode::kInvalidArgument, "command line is required");
  }
  const bool appcontainer = options.containment == Containment::kAppContainer;
  if (options.appcontainer_sid.empty()) {
    return core::Error(core::ErrorCode::kInvalidArgument,
                       "a sandbox SID is required for every containment mode");
  }

  ScopedHandles handles;
  PSID sid = nullptr;
  std::unique_ptr<void, decltype(&LocalFree)> sid_guard(nullptr, &LocalFree);
  if (!ConvertStringSidToSidW(options.appcontainer_sid.c_str(), &sid)) {
    return core::Error(core::ErrorCode::kInvalidArgument, "sandbox SID is not valid", false,
                       LastErrorDetail(GetLastError()));
  }
  sid_guard.reset(sid);

  std::vector<std::unique_ptr<void, decltype(&LocalFree)>> capability_guards;
  std::vector<SID_AND_ATTRIBUTES> capability_entries;
  for (const std::wstring& capability : options.capabilities) {
    PSID capability_sid = nullptr;
    if (!ConvertStringSidToSidW(capability.c_str(), &capability_sid)) {
      return core::Error(core::ErrorCode::kInvalidArgument,
                         "a sandbox capability SID is not valid", false,
                         LastErrorDetail(GetLastError()));
    }
    capability_guards.emplace_back(capability_sid, &LocalFree);
    capability_entries.push_back({capability_sid, SE_GROUP_ENABLED});
  }

  if (!appcontainer) {
    // AppContainer cannot open WinFsp volumes (winfsp/winfsp#705), so mounted
    // workspaces run under a restricted token. Access must satisfy both the
    // normal token check and a restricting check that only accepts
    // BUILTIN\Users (OS binaries) or the session SID granted on the
    // workspace/temp/tools. The host user profile grants neither to the
    // restriction, so it stays unreadable.
    HANDLE self = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY,
                          &self)) {
      return core::Error(core::ErrorCode::kBackendFailed,
                         "failed to open the process token for the sandbox", false,
                         LastErrorDetail(GetLastError()));
    }

    DWORD token_is_restricted = 0;
    DWORD token_info_size = 0;
    if (GetTokenInformation(self, TokenIsRestricted, &token_is_restricted,
                            sizeof(token_is_restricted), &token_info_size)) {
      core::LogDebug("sandbox", std::string("engine token restricted=") +
                                    (token_is_restricted != 0 ? "true" : "false"));
    }

    BYTE users_buffer[SECURITY_MAX_SID_SIZE] = {};
    DWORD users_size = sizeof(users_buffer);
    if (!CreateWellKnownSid(WinBuiltinUsersSid, nullptr, users_buffer, &users_size)) {
      const DWORD error = GetLastError();
      CloseHandle(self);
      return core::Error(core::ErrorCode::kBackendFailed,
                         "failed to build the sandbox restricting SID", false,
                         LastErrorDetail(error));
    }

    // AppContainer SIDs are rejected by CreateRestrictedToken, so only attach
    // the session SID when it is a regular SID. Capability SIDs are regular
    // SIDs and act as additional allowing restrictions for authorized tools.
    std::vector<SID_AND_ATTRIBUTES> restricting = {{users_buffer, 0}, {sid, 0}};
    for (const SID_AND_ATTRIBUTES& capability : capability_entries) {
      restricting.push_back({capability.Sid, 0});
    }
    BOOL restricted = CreateRestrictedToken(
        self, DISABLE_MAX_PRIVILEGE, 0, nullptr, 0, nullptr,
        static_cast<DWORD>(restricting.size()), restricting.data(), &handles.token);
    if (!restricted && GetLastError() == ERROR_INVALID_PARAMETER) {
      core::LogWarn("sandbox",
                    "session/capability SIDs rejected as restricting SIDs; falling back to "
                    "BUILTIN\\Users");
      restricting.resize(1);
      restricted = CreateRestrictedToken(self, DISABLE_MAX_PRIVILEGE, 0, nullptr, 0, nullptr,
                                         static_cast<DWORD>(restricting.size()),
                                         restricting.data(), &handles.token);
    }
    const DWORD token_error = GetLastError();
    CloseHandle(self);
    if (!restricted) {
      return core::Error(core::ErrorCode::kBackendFailed,
                         "failed to create the restricted sandbox token", false,
                         LastErrorDetail(token_error));
    }
  }

  core::Status pipes_status = CreateStdioPipe(&handles.stdin_read, &handles.stdin_write);
  if (!pipes_status.ok()) return pipes_status.error();
  pipes_status = CreateStdioPipe(&handles.stdout_read, &handles.stdout_write);
  if (!pipes_status.ok()) return pipes_status.error();
  pipes_status = CreateStdioPipe(&handles.stderr_read, &handles.stderr_write);
  if (!pipes_status.ok()) return pipes_status.error();
  // Only the child ends stay inheritable; parent ends must remain private so
  // the child's EOF tracks its own handles, not ours.
  SetHandleInformation(handles.stdin_write, HANDLE_FLAG_INHERIT, 0);
  SetHandleInformation(handles.stdout_read, HANDLE_FLAG_INHERIT, 0);
  SetHandleInformation(handles.stderr_read, HANDLE_FLAG_INHERIT, 0);

  SECURITY_CAPABILITIES capabilities{};
  capabilities.AppContainerSid = sid;
  capabilities.Capabilities =
      capability_entries.empty() ? nullptr : capability_entries.data();
  capabilities.CapabilityCount = static_cast<DWORD>(capability_entries.size());
  capabilities.Reserved = 0;

  const int attribute_count = appcontainer ? 2 : 1;
  SIZE_T attribute_size = 0;
  InitializeProcThreadAttributeList(nullptr, attribute_count, 0, &attribute_size);
  std::vector<std::uint8_t> attribute_buffer(attribute_size);
  auto* attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attribute_buffer.data());
  HANDLE inherited_handles[] = {handles.stdin_read, handles.stdout_write, handles.stderr_write};
  const BOOL attributes_initialized =
      InitializeProcThreadAttributeList(attributes, attribute_count, 0, &attribute_size);
  const bool capabilities_added =
      attributes_initialized &&
      (!appcontainer ||
       UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES,
                                 &capabilities, sizeof(capabilities), nullptr, nullptr));
  if (!attributes_initialized || !capabilities_added ||
      !UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                                 inherited_handles, sizeof(inherited_handles), nullptr,
                                 nullptr)) {
    const DWORD error = GetLastError();
    if (attributes_initialized) DeleteProcThreadAttributeList(attributes);
    return core::Error(core::ErrorCode::kBackendFailed,
                       "failed to build the AppContainer process attributes", false,
                       LastErrorDetail(error));
  }

  STARTUPINFOEXW startup{};
  startup.StartupInfo.cb = sizeof(startup);
  startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
  startup.StartupInfo.hStdInput = handles.stdin_read;
  startup.StartupInfo.hStdOutput = handles.stdout_write;
  startup.StartupInfo.hStdError = handles.stderr_write;
  startup.lpAttributeList = attributes;

  std::vector<wchar_t> command_line(options.command_line.begin(), options.command_line.end());
  command_line.push_back(L'\0');

  std::vector<wchar_t> environment_block;
  if (!options.environment.empty()) {
    for (const auto& [key, value] : options.environment) {
      environment_block.insert(environment_block.end(), key.begin(), key.end());
      environment_block.push_back(L'=');
      environment_block.insert(environment_block.end(), value.begin(), value.end());
      environment_block.push_back(L'\0');
    }
    environment_block.push_back(L'\0');
  }
  LPVOID environment = environment_block.empty() ? nullptr : environment_block.data();

  PROCESS_INFORMATION process{};
  const DWORD creation_flags =
      EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT;
  const BOOL created =
      appcontainer
          ? CreateProcessW(nullptr, command_line.data(), nullptr, nullptr, TRUE, creation_flags,
                           environment,
                           options.working_directory.empty() ? nullptr
                                                             : options.working_directory.c_str(),
                           &startup.StartupInfo, &process)
          : CreateProcessAsUserW(handles.token, nullptr, command_line.data(), nullptr, nullptr,
                                 TRUE, creation_flags, environment,
                                 options.working_directory.empty()
                                     ? nullptr
                                     : options.working_directory.c_str(),
                                 &startup.StartupInfo, &process);

  DeleteProcThreadAttributeList(attributes);
  // The child owns its ends now; dropping ours is what lets its stdout reach EOF.
  CloseHandle(handles.stdin_read);
  handles.stdin_read = nullptr;
  CloseHandle(handles.stdout_write);
  handles.stdout_write = nullptr;
  CloseHandle(handles.stderr_write);
  handles.stderr_write = nullptr;
  if (!created) {
    const DWORD error = GetLastError();
    return core::Error(core::ErrorCode::kBackendFailed,
                       "failed to start a process inside the sandbox", false,
                       LastErrorDetail(error));
  }
  handles.process = process.hProcess;
  handles.thread = process.hThread;

  handles.job = CreateJobObjectW(nullptr, nullptr);
  if (handles.job == nullptr) {
    TerminateProcess(handles.process, 1);
    return core::Error(core::ErrorCode::kBackendFailed, "failed to create the sandbox job",
                       false, LastErrorDetail(GetLastError()));
  }

  JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
  limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
  if (options.memory_limit_bytes != 0) {
    limits.ProcessMemoryLimit = options.memory_limit_bytes;
    limits.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_PROCESS_MEMORY;
  }
  if (options.max_processes != 0) {
    limits.BasicLimitInformation.ActiveProcessLimit = options.max_processes;
    limits.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
  }
  if (!SetInformationJobObject(handles.job, JobObjectExtendedLimitInformation, &limits,
                               sizeof(limits)) ||
      !AssignProcessToJobObject(handles.job, handles.process)) {
    const DWORD error = GetLastError();
    TerminateProcess(handles.process, 1);
    return core::Error(core::ErrorCode::kBackendFailed,
                       "failed to confine the sandbox process in a job object", false,
                       LastErrorDetail(error));
  }

  ResumeThread(handles.thread);

  CaptureState capture;
  capture.options = &options;
  capture.cap = options.max_output_bytes;
  std::thread stdout_thread(CaptureStream, handles.stdout_read, false, std::ref(capture));
  std::thread stderr_thread(CaptureStream, handles.stderr_read, true, std::ref(capture));
  std::thread stdin_thread([&handles, &options] {
    std::size_t offset = 0;
    while (offset < options.stdin_data.size()) {
      const DWORD chunk = static_cast<DWORD>(
          std::min<std::size_t>(options.stdin_data.size() - offset, 64 * 1024));
      DWORD written = 0;
      if (!WriteFile(handles.stdin_write, options.stdin_data.data() + offset, chunk, &written,
                     nullptr) ||
          written == 0) {
        break;
      }
      offset += written;
    }
    CloseHandle(handles.stdin_write);
    handles.stdin_write = nullptr;
  });

  ProcessResult result;
  const ULONGLONG started = GetTickCount64();
  for (;;) {
    const DWORD waited = WaitForSingleObject(handles.process, 100);
    if (waited == WAIT_OBJECT_0) break;
    if (waited == WAIT_FAILED) {
      result.exit_code = 1;
      break;
    }
    const bool expired =
        options.timeout_ms != 0 && GetTickCount64() - started >= options.timeout_ms;
    const bool cancelled = !expired && options.should_cancel && options.should_cancel();
    if (!expired && !cancelled) continue;
    result.timed_out = expired;
    result.cancelled = cancelled && !expired;
    TerminateJobObject(handles.job, 1);
    WaitForSingleObject(handles.process, 5000);
    break;
  }

  JoinCaptureThread(stdout_thread);
  JoinCaptureThread(stderr_thread);
  JoinCaptureThread(stdin_thread);

  DWORD exit_code = 1;
  GetExitCodeProcess(handles.process, &exit_code);
  result.exit_code = exit_code;
  result.process_id = GetProcessId(handles.process);
  result.output = std::move(capture.output);
  result.error = std::move(capture.error);
  result.output_truncated = capture.output_truncated;
  result.error_truncated = capture.error_truncated;
  return core::Ok(std::move(result));
}

}  // namespace rvm::sandbox