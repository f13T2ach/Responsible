#include "workspace/fsp_host.h"

#include <windows.h>
#include <sddl.h>

#if defined(_ReadWriteBarrier)
#undef _ReadWriteBarrier
#endif

#include <winfsp/winfsp.h>

#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwctype>
#include <cwchar>
#include <filesystem>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "core/error.h"
#include "workspace/union_view.h"

#if defined(__GNUC__)
#pragma GCC diagnostic ignored "-Wcast-function-type"
#endif

namespace rvm::workspace {
namespace {

using core::Error;
using core::ErrorCode;
using core::Result;
using core::Status;

constexpr std::uint64_t kAllocationUnit = 4096;

std::mutex g_trace_mutex;
HANDLE g_trace_file = INVALID_HANDLE_VALUE;

std::wstring TracePath(const wchar_t* name) {
  wchar_t temp[MAX_PATH] = {};
  const DWORD length = GetTempPathW(MAX_PATH, temp);
  std::wstring path(temp, length);
  path += name;
  return path;
}

void OpenTraceFile() {
  std::lock_guard<std::mutex> lock(g_trace_mutex);
  if (g_trace_file != INVALID_HANDLE_VALUE) return;
  const std::wstring path = TracePath(L"rvm-winfsp-trace.log");
  g_trace_file =
      CreateFileW(path.c_str(), FILE_APPEND_DATA,
                  FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, CREATE_ALWAYS,
                  FILE_ATTRIBUTE_NORMAL, nullptr);
}

void CloseTraceFile() {
  std::lock_guard<std::mutex> lock(g_trace_mutex);
  if (g_trace_file != INVALID_HANDLE_VALUE) CloseHandle(g_trace_file);
  g_trace_file = INVALID_HANDLE_VALUE;
}

void Trace(const char* format, ...) {
  char line[1024] = {};
  const int prefix = std::snprintf(line, sizeof(line), "[%llu t%lu] ",
                                   static_cast<unsigned long long>(GetTickCount64()),
                                   GetCurrentThreadId());
  const int start = prefix > 0 && prefix < static_cast<int>(sizeof(line)) ? prefix : 0;
  va_list args;
  va_start(args, format);
  int length = std::vsnprintf(line + start, sizeof(line) - start, format, args);
  va_end(args);
  if (length < 0) length = 0;
  int total = start + length;
  if (total > static_cast<int>(sizeof(line)) - 2) total = sizeof(line) - 2;
  line[total] = '\n';
  line[total + 1] = '\0';

  std::fprintf(stderr, "winfsp: %s", line + start);
  std::fflush(stderr);
  std::lock_guard<std::mutex> lock(g_trace_mutex);
  if (g_trace_file != INVALID_HANDLE_VALUE) {
    DWORD written = 0;
    WriteFile(g_trace_file, line, static_cast<DWORD>(total + 1), &written, nullptr);
  }
}

HANDLE OpenLogFile(const wchar_t* name) {
  const std::wstring path = TracePath(name);
  return CreateFileW(path.c_str(), FILE_APPEND_DATA,
                     FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                     CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
}

struct WinFspApi {
  HMODULE module = nullptr;
  decltype(&FspFileSystemCreate) create = nullptr;
  decltype(&FspFileSystemDelete) destroy = nullptr;
  decltype(&FspFileSystemSetMountPointEx) set_mount_point = nullptr;
  decltype(&FspFileSystemRemoveMountPoint) remove_mount_point = nullptr;
  decltype(&FspFileSystemStartDispatcher) start = nullptr;
  decltype(&FspFileSystemStopDispatcher) stop = nullptr;
  decltype(&FspFileSystemSetOperationGuardStrategyF) guard = nullptr;
  decltype(&FspFileSystemAddDirInfo) add_dir = nullptr;
  decltype(&FspDebugLogSetHandle) debug_log_set_handle = nullptr;
  decltype(&FspFileSystemSetDebugLogF) debug_log_set_mask = nullptr;

  template <typename T>
  static T Resolve(HMODULE module, const char* name) {
    return reinterpret_cast<T>(GetProcAddress(module, name));
  }

  static Result<WinFspApi> Load() {
    std::vector<std::wstring> candidates;
    wchar_t install_dir[1024] = {};
    DWORD size = sizeof(install_dir);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\WOW6432Node\\WinFsp", L"InstallDir",
                     RRF_RT_REG_SZ, nullptr, install_dir, &size) == ERROR_SUCCESS) {
      candidates.emplace_back(std::wstring(install_dir) + L"\\bin\\winfsp-x64.dll");
    }
    candidates.emplace_back(L"winfsp-x64.dll");

    WinFspApi api;
    for (const std::wstring& candidate : candidates) {
      api.module = LoadLibraryW(candidate.c_str());
      if (api.module) break;
    }
    if (!api.module) {
      return Error(ErrorCode::kUnavailable, "WinFsp runtime (winfsp-x64.dll) could not be loaded");
    }

    auto fail = [&api](const char* name) {
      FreeLibrary(api.module);
      api.module = nullptr;
      return Error(ErrorCode::kUnavailable,
                   std::string("WinFsp export missing: ") + name);
    };
    api.create = Resolve<decltype(api.create)>(api.module, "FspFileSystemCreate");
    api.destroy = Resolve<decltype(api.destroy)>(api.module, "FspFileSystemDelete");
    api.set_mount_point =
        Resolve<decltype(api.set_mount_point)>(api.module, "FspFileSystemSetMountPointEx");
    api.remove_mount_point =
        Resolve<decltype(api.remove_mount_point)>(api.module, "FspFileSystemRemoveMountPoint");
    api.start = Resolve<decltype(api.start)>(api.module, "FspFileSystemStartDispatcher");
    api.stop = Resolve<decltype(api.stop)>(api.module, "FspFileSystemStopDispatcher");
    api.guard = Resolve<decltype(api.guard)>(api.module,
                                             "FspFileSystemSetOperationGuardStrategyF");
    api.add_dir = Resolve<decltype(api.add_dir)>(api.module, "FspFileSystemAddDirInfo");
    api.debug_log_set_handle =
        Resolve<decltype(api.debug_log_set_handle)>(api.module, "FspDebugLogSetHandle");
    api.debug_log_set_mask =
        Resolve<decltype(api.debug_log_set_mask)>(api.module, "FspFileSystemSetDebugLogF");

    if (!api.create) return fail("FspFileSystemCreate");
    if (!api.destroy) return fail("FspFileSystemDelete");
    if (!api.set_mount_point) return fail("FspFileSystemSetMountPointEx");
    if (!api.remove_mount_point) return fail("FspFileSystemRemoveMountPoint");
    if (!api.start) return fail("FspFileSystemStartDispatcher");
    if (!api.stop) return fail("FspFileSystemStopDispatcher");
    if (!api.guard) return fail("FspFileSystemSetOperationGuardStrategyF");
    if (!api.add_dir) return fail("FspFileSystemAddDirInfo");
    return api;
  }

  void Unload() {
    if (module) FreeLibrary(module);
    module = nullptr;
  }
};

NTSTATUS MapError(const Error& error) {
  switch (error.code) {
    case ErrorCode::kNotFound:
      return STATUS_OBJECT_NAME_NOT_FOUND;
    case ErrorCode::kAlreadyExists:
      return STATUS_OBJECT_NAME_COLLISION;
    case ErrorCode::kInvalidArgument:
      return STATUS_OBJECT_NAME_INVALID;
    case ErrorCode::kPathOutOfScope:
    case ErrorCode::kMaskedFile:
      return STATUS_ACCESS_DENIED;
    default:
      return STATUS_UNSUCCESSFUL;
  }
}

std::wstring NormalizeRelative(PWSTR file_name) {
  if (!file_name) return {};
  std::wstring relative(file_name);
  while (!relative.empty() && relative.front() == L'\\') relative.erase(relative.begin());
  while (!relative.empty() && relative.back() == L'\\') relative.pop_back();
  return relative;
}

std::wstring LeafName(const std::wstring& relative) {
  const std::size_t slash = relative.find_last_of(L'\\');
  return slash == std::wstring::npos ? relative : relative.substr(slash + 1);
}

// WinFsp passes the file node (not a per-handle context) to the interface:
// every open of one name shares the node until the last close. The node owns
// the host handle, and refs counts the opens that have not been closed yet.
struct FileContext {
  std::wstring relative;
  HANDLE handle = INVALID_HANDLE_VALUE;
  bool directory = false;
  bool writable = false;
  unsigned refs = 0;
};

std::wstring FoldName(const std::wstring& relative) {
  std::wstring folded = relative;
  for (wchar_t& ch : folded) ch = static_cast<wchar_t>(std::towlower(ch));
  return folded;
}

void FillFileInfo(const UnionEntry& entry, FSP_FSCTL_FILE_INFO* info) {
  std::memset(info, 0, sizeof(*info));
  if (entry.is_directory) {
    info->FileAttributes = FILE_ATTRIBUTE_DIRECTORY;
    info->LastWriteTime = entry.last_write_time;
    info->CreationTime = entry.last_write_time;
    info->LastAccessTime = entry.last_write_time;
    info->ChangeTime = entry.last_write_time;
    return;
  }
  info->FileAttributes = FILE_ATTRIBUTE_NORMAL;
  info->FileSize = entry.size;
  info->AllocationSize = (entry.size + kAllocationUnit - 1) / kAllocationUnit * kAllocationUnit;
  info->CreationTime = entry.last_write_time;
  info->LastAccessTime = entry.last_write_time;
  info->LastWriteTime = entry.last_write_time;
  info->ChangeTime = entry.last_write_time;
}

bool ContainsSidCharacters(const std::wstring& sid) {
  for (wchar_t ch : sid) {
    if ((ch < L'0' || ch > L'9') && ch != L'-') return false;
  }
  return true;
}

bool ValidAppContainerSid(const std::wstring& sid) {
  static constexpr wchar_t kPrefix[] = L"S-1-15-2-";
  if (sid.rfind(kPrefix, 0) != 0) return false;
  return ContainsSidCharacters(sid.substr(std::wcslen(kPrefix)));
}

Result<std::wstring> CurrentUserSid() {
  HANDLE token = nullptr;
  if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
    return Error(ErrorCode::kBackendFailed, "OpenProcessToken failed");
  }
  DWORD size = 0;
  GetTokenInformation(token, TokenUser, nullptr, 0, &size);
  std::vector<unsigned char> buffer(size);
  if (!GetTokenInformation(token, TokenUser, buffer.data(), size, &size)) {
    CloseHandle(token);
    return Error(ErrorCode::kBackendFailed, "GetTokenInformation(TokenUser) failed");
  }
  CloseHandle(token);
  const auto* user = reinterpret_cast<const TOKEN_USER*>(buffer.data());
  LPWSTR sid = nullptr;
  if (!ConvertSidToStringSidW(user->User.Sid, &sid)) {
    return Error(ErrorCode::kBackendFailed, "ConvertSidToStringSidW failed");
  }
  std::wstring result(sid);
  LocalFree(sid);
  return result;
}

NTSTATUS CopySecurityDescriptor(PSECURITY_DESCRIPTOR source, PSECURITY_DESCRIPTOR target,
                                SIZE_T* target_size) {
  const DWORD length = GetSecurityDescriptorLength(source);
  if (!target || !target_size) {
    if (target_size) *target_size = length;
    return STATUS_SUCCESS;
  }
  if (*target_size < length) {
    *target_size = length;
    return STATUS_BUFFER_OVERFLOW;
  }
  std::memcpy(target, source, length);
  *target_size = length;
  return STATUS_SUCCESS;
}

std::uint64_t AllocationRound(std::uint64_t value) {
  return (value + kAllocationUnit - 1) / kAllocationUnit * kAllocationUnit;
}

struct FileSystemImpl {
  WinFspApi api;
  UnionView* view = nullptr;
  FSP_FILE_SYSTEM* fs = nullptr;
  std::mutex mutex;
  std::unordered_map<std::wstring, FileContext*> nodes;
  std::unordered_set<const void*> live_nodes;
  PSECURITY_DESCRIPTOR file_sd = nullptr;
  PSECURITY_DESCRIPTOR mount_sd = nullptr;
  HANDLE debug_log = INVALID_HANDLE_VALUE;
  std::wstring volume_label;
  std::wstring mount_point_text;

  ~FileSystemImpl() {
    for (auto& node : nodes) {
      if (node.second->handle != INVALID_HANDLE_VALUE) CloseHandle(node.second->handle);
      delete node.second;
    }
    nodes.clear();
  }
};

FileSystemImpl* Impl(FSP_FILE_SYSTEM* fs) {
  return static_cast<FileSystemImpl*>(fs->UserContext);
}

FileContext* ValidatedNode(FileSystemImpl* impl, PVOID pointer) {
  if (!pointer || impl->live_nodes.count(pointer) == 0) return nullptr;
  return static_cast<FileContext*>(pointer);
}

NTSTATUS BuildFileContext(FileSystemImpl* impl, const std::wstring& relative, bool directory,
                          bool writable, HANDLE handle, PVOID* out) {
  auto* context = new (std::nothrow) FileContext();
  if (!context) {
    if (handle != INVALID_HANDLE_VALUE) CloseHandle(handle);
    return STATUS_INSUFFICIENT_RESOURCES;
  }
  context->relative = relative;
  context->directory = directory;
  context->writable = writable;
  context->handle = handle;
  context->refs = 1;
  impl->nodes[FoldName(relative)] = context;
  impl->live_nodes.insert(context);
  *out = context;
  return STATUS_SUCCESS;
}

NTSTATUS OpenHostHandle(FileSystemImpl* impl, const std::wstring& relative, bool directory,
                        bool writable, HANDLE* handle) {
  *handle = INVALID_HANDLE_VALUE;
  if (directory) return STATUS_SUCCESS;
  if (writable) {
    const auto copied = impl->view->CopyUp(relative);
    if (!copied.ok()) return MapError(copied.error());
  }
  const auto path =
      writable ? impl->view->DiffPathFor(relative) : impl->view->ResolvePath(relative);
  if (!path.ok()) return MapError(path.error());
  const DWORD access = writable ? (GENERIC_READ | GENERIC_WRITE) : GENERIC_READ;
  const HANDLE opened =
      CreateFileW(path.value().c_str(), access, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                  nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (opened == INVALID_HANDLE_VALUE) {
    const DWORD last = GetLastError();
    if (last == ERROR_FILE_NOT_FOUND) return STATUS_OBJECT_NAME_NOT_FOUND;
    if (last == ERROR_ACCESS_DENIED) return STATUS_ACCESS_DENIED;
    return STATUS_UNSUCCESSFUL;
  }
  *handle = opened;
  return STATUS_SUCCESS;
}

NTSTATUS EnsureWritable(FileSystemImpl* impl, FileContext* context) {
  if (context->directory || context->writable) return STATUS_SUCCESS;
  HANDLE handle = INVALID_HANDLE_VALUE;
  const NTSTATUS opened = OpenHostHandle(impl, context->relative, false, true, &handle);
  if (opened != STATUS_SUCCESS) return opened;
  if (context->handle != INVALID_HANDLE_VALUE) CloseHandle(context->handle);
  context->handle = handle;
  context->writable = true;
  return STATUS_SUCCESS;
}

NTSTATUS AttachNode(FileSystemImpl* impl, const std::wstring& relative, bool directory,
                    bool writable, PVOID* out, FSP_FSCTL_FILE_INFO* file_info) {
  auto existing = impl->nodes.find(FoldName(relative));
  if (existing == impl->nodes.end()) return STATUS_OBJECT_NAME_NOT_FOUND;
  FileContext* context = existing->second;
  if (context->directory != directory) return STATUS_OBJECT_NAME_COLLISION;
  if (writable) {
    const NTSTATUS upgraded = EnsureWritable(impl, context);
    if (upgraded != STATUS_SUCCESS) return upgraded;
  }
  context->refs += 1;
  const auto entry = impl->view->Stat(relative);
  if (entry.ok()) FillFileInfo(entry.value(), file_info);
  *out = context;
  return STATUS_SUCCESS;
}

NTSTATUS GetVolumeInfo(FSP_FILE_SYSTEM* fs, FSP_FSCTL_VOLUME_INFO* volume_info) {
  auto* impl = Impl(fs);
  Trace("GetVolumeInfo");
  std::memset(volume_info, 0, sizeof(*volume_info));
  ULARGE_INTEGER free_bytes{};
  ULARGE_INTEGER total_bytes{};
  if (GetDiskFreeSpaceExW(impl->view->paths().diff_root.c_str(), &free_bytes, &total_bytes,
                          nullptr)) {
    volume_info->TotalSize = total_bytes.QuadPart;
    volume_info->FreeSize = free_bytes.QuadPart;
  } else {
    volume_info->TotalSize = 1ULL << 40;
    volume_info->FreeSize = 1ULL << 39;
  }
  const std::size_t label_length =
      impl->volume_label.size() < 31 ? impl->volume_label.size() : std::size_t{31};
  std::memcpy(volume_info->VolumeLabel, impl->volume_label.data(),
              label_length * sizeof(wchar_t));
  volume_info->VolumeLabelLength = static_cast<UINT16>(label_length * sizeof(wchar_t));
  return STATUS_SUCCESS;
}

NTSTATUS SetVolumeLabel(FSP_FILE_SYSTEM*, PWSTR, FSP_FSCTL_VOLUME_INFO*) {
  return STATUS_ACCESS_DENIED;
}

NTSTATUS GetSecurityByName(FSP_FILE_SYSTEM* fs, PWSTR file_name, PUINT32 attributes,
                           PSECURITY_DESCRIPTOR security, SIZE_T* security_size) {
  auto* impl = Impl(fs);
  std::lock_guard<std::mutex> lock(impl->mutex);
  const std::wstring relative = NormalizeRelative(file_name);
  Trace("GetSecurityByName %ls", relative.c_str());
  const auto entry = impl->view->Stat(relative);
  if (!entry.ok()) {
    Trace("GetSecurityByName stat failed");
    return MapError(entry.error());
  }
  if (attributes) {
    *attributes = entry.value().is_directory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
  }
  const DWORD length = GetSecurityDescriptorLength(impl->file_sd);
  Trace("GetSecurityByName %ls security=%d size_in=%llu length=%lu valid=%d", relative.c_str(),
        security != nullptr, security_size ? static_cast<unsigned long long>(*security_size) : 0,
        length, IsValidSecurityDescriptor(impl->file_sd) ? 1 : 0);
  if (security) {
    const NTSTATUS copied = CopySecurityDescriptor(impl->file_sd, security, security_size);
    Trace("GetSecurityByName copy status=%ld size_out=%llu", copied,
          security_size ? static_cast<unsigned long long>(*security_size) : 0);
    return copied;
  }
  if (security_size) *security_size = length;
  return STATUS_SUCCESS;
}

NTSTATUS Create(FSP_FILE_SYSTEM* fs, PWSTR file_name, UINT32 create_options, UINT32,
                UINT32, PSECURITY_DESCRIPTOR, UINT64, PVOID* file_context,
                FSP_FSCTL_FILE_INFO* file_info) {
  auto* impl = Impl(fs);
  std::lock_guard<std::mutex> lock(impl->mutex);
  const std::wstring relative = NormalizeRelative(file_name);
  const bool directory = (create_options & FILE_DIRECTORY_FILE) != 0;
  Trace("Create %ls dir=%d", relative.c_str(), directory);

  const NTSTATUS attached =
      AttachNode(impl, relative, directory, !directory, file_context, file_info);
  if (attached != STATUS_OBJECT_NAME_NOT_FOUND) return attached;

  const Status created = impl->view->CreateEntry(relative, directory);
  if (!created.ok()) return MapError(created.error());

  HANDLE handle = INVALID_HANDLE_VALUE;
  const NTSTATUS opened = OpenHostHandle(impl, relative, directory, !directory, &handle);
  if (opened != STATUS_SUCCESS) {
    impl->view->RemoveEntry(relative);
    return opened;
  }
  const auto entry = impl->view->Stat(relative);
  if (!entry.ok()) {
    Trace("Create stat failed");
    if (handle != INVALID_HANDLE_VALUE) CloseHandle(handle);
    return MapError(entry.error());
  }
  FillFileInfo(entry.value(), file_info);
  Trace("Create done %ls", relative.c_str());
  return BuildFileContext(impl, relative, directory, !directory, handle, file_context);
}

NTSTATUS Open(FSP_FILE_SYSTEM* fs, PWSTR file_name, UINT32 create_options, UINT32 granted_access,
              PVOID* file_context, FSP_FSCTL_FILE_INFO* file_info) {
  auto* impl = Impl(fs);
  std::lock_guard<std::mutex> lock(impl->mutex);
  const std::wstring relative = NormalizeRelative(file_name);
  Trace("Open %ls access=0x%lx", relative.c_str(), granted_access);
  const auto entry = impl->view->Stat(relative);
  if (!entry.ok()) return MapError(entry.error());

  const bool directory = entry.value().is_directory;
  constexpr UINT32 kWriteMask =
      GENERIC_WRITE | GENERIC_ALL | FILE_WRITE_DATA | FILE_APPEND_DATA | FILE_WRITE_ATTRIBUTES |
      FILE_WRITE_EA;
  const bool writable = !directory && ((granted_access & kWriteMask) != 0);
  (void)create_options;

  const NTSTATUS attached =
      AttachNode(impl, relative, directory, writable, file_context, file_info);
  if (attached != STATUS_OBJECT_NAME_NOT_FOUND) return attached;

  HANDLE handle = INVALID_HANDLE_VALUE;
  const NTSTATUS opened = OpenHostHandle(impl, relative, directory, writable, &handle);
  if (opened != STATUS_SUCCESS) {
    Trace("Open host handle failed status=%ld", opened);
    return opened;
  }
  FillFileInfo(entry.value(), file_info);
  Trace("Open done %ls writable=%d", relative.c_str(), writable);
  return BuildFileContext(impl, relative, directory, writable, handle, file_context);
}

NTSTATUS Overwrite(FSP_FILE_SYSTEM* fs, PVOID file_context, UINT32, BOOLEAN, UINT64,
                   FSP_FSCTL_FILE_INFO* file_info) {
  auto* impl = Impl(fs);
  std::lock_guard<std::mutex> lock(impl->mutex);
  auto* context = ValidatedNode(impl, file_context);
  if (!context || context->directory) return STATUS_INVALID_PARAMETER;
  Trace("Overwrite %ls", context->relative.c_str());
  if (!context->writable || context->handle == INVALID_HANDLE_VALUE) return STATUS_ACCESS_DENIED;
  LARGE_INTEGER zero{};
  if (!SetFilePointerEx(context->handle, zero, nullptr, FILE_BEGIN) ||
      !SetEndOfFile(context->handle)) {
    return STATUS_UNSUCCESSFUL;
  }
  const auto entry = impl->view->Stat(context->relative);
  if (!entry.ok()) return MapError(entry.error());
  if (file_info) FillFileInfo(entry.value(), file_info);
  return STATUS_SUCCESS;
}

VOID Cleanup(FSP_FILE_SYSTEM* fs, PVOID file_context, PWSTR, ULONG flags) {
  auto* impl = Impl(fs);
  std::lock_guard<std::mutex> lock(impl->mutex);
  auto* context = ValidatedNode(impl, file_context);
  if (!context) {
    Trace("Cleanup ignored unknown context %p", file_context);
    return;
  }
  if (flags & FspCleanupDelete) {
    impl->view->RemoveEntry(context->relative);
  }
}

VOID Close(FSP_FILE_SYSTEM* fs, PVOID file_context) {
  auto* impl = Impl(fs);
  std::lock_guard<std::mutex> lock(impl->mutex);
  auto* context = ValidatedNode(impl, file_context);
  if (!context) {
    Trace("Close ignored unknown context %p", file_context);
    return;
  }
  if (context->refs > 0) context->refs -= 1;
  if (context->refs > 0) return;
  impl->live_nodes.erase(context);
  impl->nodes.erase(FoldName(context->relative));
  if (context->handle != INVALID_HANDLE_VALUE) CloseHandle(context->handle);
  delete context;
}

NTSTATUS Read(FSP_FILE_SYSTEM* fs, PVOID file_context, PVOID buffer, UINT64 offset, ULONG length,
              PULONG bytes_transferred) {
  auto* impl = Impl(fs);
  std::lock_guard<std::mutex> lock(impl->mutex);
  auto* context = ValidatedNode(impl, file_context);
  if (!context) {
    Trace("Read unknown context %p", file_context);
    return STATUS_INVALID_PARAMETER;
  }
  Trace("Read %ls offset=%llu length=%lu", context->relative.c_str(),
        static_cast<unsigned long long>(offset), length);
  if (context->handle == INVALID_HANDLE_VALUE) return STATUS_INVALID_PARAMETER;
  *bytes_transferred = 0;
  LARGE_INTEGER position{};
  position.QuadPart = static_cast<LONGLONG>(offset);
  if (!SetFilePointerEx(context->handle, position, nullptr, FILE_BEGIN)) {
    return STATUS_UNSUCCESSFUL;
  }
  DWORD read = 0;
  if (!ReadFile(context->handle, buffer, length, &read, nullptr)) {
    const DWORD last = GetLastError();
    if (last == ERROR_HANDLE_EOF) return STATUS_SUCCESS;
    return STATUS_UNSUCCESSFUL;
  }
  *bytes_transferred = read;
  return STATUS_SUCCESS;
}

NTSTATUS Write(FSP_FILE_SYSTEM* fs, PVOID file_context, PVOID buffer, UINT64 offset, ULONG length,
               BOOLEAN write_to_end, BOOLEAN, PULONG bytes_transferred,
               FSP_FSCTL_FILE_INFO* file_info) {
  auto* impl = Impl(fs);
  std::lock_guard<std::mutex> lock(impl->mutex);
  auto* context = ValidatedNode(impl, file_context);
  if (!context || context->handle == INVALID_HANDLE_VALUE) return STATUS_INVALID_PARAMETER;
  Trace("Write %ls offset=%llu length=%lu", context->relative.c_str(),
        static_cast<unsigned long long>(offset), length);
  if (!context->writable) return STATUS_ACCESS_DENIED;
  *bytes_transferred = 0;
  if (write_to_end) {
    LARGE_INTEGER zero{};
    SetFilePointerEx(context->handle, zero, nullptr, FILE_END);
  } else {
    LARGE_INTEGER position{};
    position.QuadPart = static_cast<LONGLONG>(offset);
    if (!SetFilePointerEx(context->handle, position, nullptr, FILE_BEGIN)) {
      return STATUS_UNSUCCESSFUL;
    }
  }
  DWORD written = 0;
  if (!WriteFile(context->handle, buffer, length, &written, nullptr)) {
    return GetLastError() == ERROR_DISK_FULL ? STATUS_DISK_FULL : STATUS_UNSUCCESSFUL;
  }
  *bytes_transferred = written;
  if (file_info) {
    const auto entry = impl->view->Stat(context->relative);
    if (entry.ok()) FillFileInfo(entry.value(), file_info);
  }
  return STATUS_SUCCESS;
}

NTSTATUS Flush(FSP_FILE_SYSTEM* fs, PVOID file_context, FSP_FSCTL_FILE_INFO* file_info) {
  auto* impl = Impl(fs);
  std::lock_guard<std::mutex> lock(impl->mutex);
  auto* context = ValidatedNode(impl, file_context);
  if (!context) return STATUS_SUCCESS;
  Trace("Flush %ls", context->relative.c_str());
  if (context->handle != INVALID_HANDLE_VALUE) FlushFileBuffers(context->handle);
  if (file_info) {
    const auto entry = impl->view->Stat(context->relative);
    if (entry.ok()) FillFileInfo(entry.value(), file_info);
  }
  return STATUS_SUCCESS;
}

NTSTATUS GetFileInfo(FSP_FILE_SYSTEM* fs, PVOID file_context, FSP_FSCTL_FILE_INFO* file_info) {
  auto* impl = Impl(fs);
  std::lock_guard<std::mutex> lock(impl->mutex);
  auto* context = ValidatedNode(impl, file_context);
  if (!context) return STATUS_INVALID_PARAMETER;
  Trace("GetFileInfo %ls", context->relative.c_str());
  const auto entry = impl->view->Stat(context->relative);
  if (!entry.ok()) return MapError(entry.error());
  FillFileInfo(entry.value(), file_info);
  return STATUS_SUCCESS;
}

NTSTATUS SetBasicInfo(FSP_FILE_SYSTEM* fs, PVOID file_context, UINT32 attributes,
                      UINT64 creation_time, UINT64 last_access_time, UINT64 last_write_time,
                      UINT64 change_time, FSP_FSCTL_FILE_INFO* file_info) {
  auto* impl = Impl(fs);
  std::lock_guard<std::mutex> lock(impl->mutex);
  auto* context = ValidatedNode(impl, file_context);
  if (!context) return STATUS_INVALID_PARAMETER;
  if (context->handle != INVALID_HANDLE_VALUE) {
    FILE_BASIC_INFO basic{};
    basic.CreationTime.QuadPart = static_cast<LONGLONG>(creation_time);
    basic.LastAccessTime.QuadPart = static_cast<LONGLONG>(last_access_time);
    basic.LastWriteTime.QuadPart = static_cast<LONGLONG>(last_write_time);
    basic.ChangeTime.QuadPart = static_cast<LONGLONG>(change_time);
    basic.FileAttributes = attributes == INVALID_FILE_ATTRIBUTES ? 0 : attributes;
    if (!SetFileInformationByHandle(context->handle, FileBasicInfo, &basic, sizeof(basic))) {
      return STATUS_ACCESS_DENIED;
    }
  }
  const auto entry = impl->view->Stat(context->relative);
  if (!entry.ok()) return MapError(entry.error());
  if (file_info) FillFileInfo(entry.value(), file_info);
  return STATUS_SUCCESS;
}

NTSTATUS SetFileSize(FSP_FILE_SYSTEM* fs, PVOID file_context, UINT64 new_size,
                     BOOLEAN set_allocation_size, FSP_FSCTL_FILE_INFO* file_info) {
  auto* impl = Impl(fs);
  std::lock_guard<std::mutex> lock(impl->mutex);
  auto* context = ValidatedNode(impl, file_context);
  if (!context || context->directory) return STATUS_INVALID_PARAMETER;
  Trace("SetFileSize %ls size=%llu alloc=%d", context->relative.c_str(),
        static_cast<unsigned long long>(new_size), set_allocation_size != 0);
  if (!context->writable || context->handle == INVALID_HANDLE_VALUE) return STATUS_ACCESS_DENIED;
  if (set_allocation_size) {
    FILE_ALLOCATION_INFO allocation{};
    allocation.AllocationSize.QuadPart = static_cast<LONGLONG>(AllocationRound(new_size));
    if (!SetFileInformationByHandle(context->handle, FileAllocationInfo, &allocation,
                                    sizeof(allocation))) {
      return STATUS_UNSUCCESSFUL;
    }
  } else {
    FILE_END_OF_FILE_INFO end{};
    end.EndOfFile.QuadPart = static_cast<LONGLONG>(new_size);
    if (!SetFileInformationByHandle(context->handle, FileEndOfFileInfo, &end, sizeof(end))) {
      return STATUS_UNSUCCESSFUL;
    }
  }
  const auto entry = impl->view->Stat(context->relative);
  if (!entry.ok()) return MapError(entry.error());
  if (file_info) FillFileInfo(entry.value(), file_info);
  return STATUS_SUCCESS;
}

NTSTATUS Rename(FSP_FILE_SYSTEM* fs, PVOID file_context, PWSTR file_name, PWSTR new_file_name,
                BOOLEAN replace_if_exists) {
  auto* impl = Impl(fs);
  std::lock_guard<std::mutex> lock(impl->mutex);
  auto* context = ValidatedNode(impl, file_context);
  if (!context) return STATUS_INVALID_PARAMETER;
  const std::wstring source = NormalizeRelative(file_name);
  const std::wstring destination = NormalizeRelative(new_file_name);
  Trace("Rename %ls -> %ls", source.c_str(), destination.c_str());
  const Status renamed =
      impl->view->Rename(source.empty() ? context->relative : source, destination,
                         replace_if_exists != 0);
  if (!renamed.ok()) return MapError(renamed.error());
  impl->nodes.erase(FoldName(context->relative));
  context->relative = destination;
  impl->nodes[FoldName(context->relative)] = context;
  return STATUS_SUCCESS;
}

NTSTATUS GetSecurity(FSP_FILE_SYSTEM* fs, PVOID file_context, PSECURITY_DESCRIPTOR security,
                     SIZE_T* security_size) {
  auto* impl = Impl(fs);
  std::lock_guard<std::mutex> lock(impl->mutex);
  auto* context = ValidatedNode(impl, file_context);
  if (!context) return STATUS_INVALID_PARAMETER;
  if (!security) {
    if (security_size) *security_size = GetSecurityDescriptorLength(impl->file_sd);
    return STATUS_SUCCESS;
  }
  return CopySecurityDescriptor(impl->file_sd, security, security_size);
}

NTSTATUS SetSecurity(FSP_FILE_SYSTEM*, PVOID, SECURITY_INFORMATION, PSECURITY_DESCRIPTOR) {
  return STATUS_ACCESS_DENIED;
}

NTSTATUS ReadDirectory(FSP_FILE_SYSTEM* fs, PVOID file_context, PWSTR, PWSTR marker,
                       PVOID buffer, ULONG length, PULONG bytes_transferred) {
  auto* impl = Impl(fs);
  std::lock_guard<std::mutex> lock(impl->mutex);
  auto* context = ValidatedNode(impl, file_context);
  if (!context) return STATUS_INVALID_PARAMETER;
  Trace("ReadDirectory %ls marker=%ls", context->relative.c_str(), marker ? marker : L"");
  if (!context->directory) return STATUS_INVALID_PARAMETER;

  const auto listing = impl->view->List(context->relative);
  if (!listing.ok()) {
    Trace("ReadDirectory list failed");
    return MapError(listing.error());
  }
  Trace("ReadDirectory entries=%zu", listing.value().size());

  const std::wstring marker_name = marker ? std::wstring(marker) : std::wstring();
  bool past_marker = marker_name.empty();
  ULONG total = 0;
  for (const UnionEntry& entry : listing.value()) {
    const std::wstring name = LeafName(entry.relative);
    if (!past_marker) {
      if (name == marker_name) past_marker = true;
      continue;
    }
    const std::size_t name_bytes = name.size() * sizeof(wchar_t);
    std::vector<unsigned char> storage(sizeof(FSP_FSCTL_DIR_INFO) + name_bytes);
    auto* dir_info = reinterpret_cast<FSP_FSCTL_DIR_INFO*>(storage.data());
    std::memset(dir_info, 0, sizeof(FSP_FSCTL_DIR_INFO));
    FillFileInfo(entry, &dir_info->FileInfo);
    std::memcpy(dir_info->FileNameBuf, name.data(), name_bytes);
    dir_info->Size = static_cast<UINT16>(sizeof(FSP_FSCTL_DIR_INFO) + name_bytes);
    if (!impl->api.add_dir(dir_info, buffer, length, &total)) break;
  }

  *bytes_transferred = total;
  Trace("ReadDirectory done bytes=%lu", total);
  return STATUS_SUCCESS;
}

FSP_FILE_SYSTEM_INTERFACE MakeInterface() {
  FSP_FILE_SYSTEM_INTERFACE interface{};
  interface.GetVolumeInfo = GetVolumeInfo;
  interface.SetVolumeLabel = SetVolumeLabel;
  interface.GetSecurityByName = GetSecurityByName;
  interface.Create = Create;
  interface.Open = Open;
  interface.Overwrite = Overwrite;
  interface.Cleanup = Cleanup;
  interface.Close = Close;
  interface.Read = Read;
  interface.Write = Write;
  interface.Flush = Flush;
  interface.GetFileInfo = GetFileInfo;
  interface.SetBasicInfo = SetBasicInfo;
  interface.SetFileSize = SetFileSize;
  interface.Rename = Rename;
  interface.GetSecurity = GetSecurity;
  interface.SetSecurity = SetSecurity;
  interface.ReadDirectory = ReadDirectory;
  return interface;
}

// FSP_FILE_SYSTEM stores this pointer for its lifetime, so it must outlive every mount.
const FSP_FILE_SYSTEM_INTERFACE& SharedInterface() {
  static const FSP_FILE_SYSTEM_INTERFACE instance = MakeInterface();
  return instance;
}

Result<PSECURITY_DESCRIPTOR> MakeSecurityDescriptor(const std::wstring& sddl) {
  PSECURITY_DESCRIPTOR descriptor = nullptr;
  if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1,
                                                            &descriptor, nullptr)) {
    return Error(ErrorCode::kBackendFailed, "security descriptor could not be built from SDDL");
  }
  return descriptor;
}

}  // namespace

struct MountedWorkspace::Impl {
  FileSystemImpl system;
  std::wstring mount_point;
  bool mounted = false;

  ~Impl() { Release(); }

  void Release() {
    if (mounted && system.fs) {
      Trace("unmount begin");
      system.api.remove_mount_point(system.fs);
      Trace("mount point removed");
      system.api.stop(system.fs);
      Trace("dispatcher stopped");
      mounted = false;
    }
    if (system.fs) {
      system.api.destroy(system.fs);
      system.fs = nullptr;
      Trace("file system deleted");
    }
    if (system.debug_log != INVALID_HANDLE_VALUE) {
      if (system.api.debug_log_set_handle) system.api.debug_log_set_handle(nullptr);
      CloseHandle(system.debug_log);
      system.debug_log = INVALID_HANDLE_VALUE;
    }
    if (system.file_sd) LocalFree(system.file_sd);
    if (system.mount_sd) LocalFree(system.mount_sd);
    system.file_sd = nullptr;
    system.mount_sd = nullptr;
    system.api.Unload();
    Trace("winfsp runtime unloaded");
    CloseTraceFile();
  }
};

MountedWorkspace::MountedWorkspace(std::unique_ptr<Impl> impl, std::wstring mount_point)
    : impl_(std::move(impl)), mount_point_(std::move(mount_point)) {}

MountedWorkspace::~MountedWorkspace() { Unmount(); }

Result<std::unique_ptr<MountedWorkspace>> MountedWorkspace::Mount(UnionView* view,
                                                                  const MountOptions& options) {
  if (!view) return Error(ErrorCode::kInvalidArgument, "UnionView is required");
  const bool is_drive = options.mount_point.size() == 2 && options.mount_point[1] == L':' &&
                        ((options.mount_point[0] | 0x20) >= L'a') &&
                        ((options.mount_point[0] | 0x20) <= L'z');
  if (!is_drive) {
    std::error_code error;
    if (options.mount_point.empty() || !std::filesystem::is_directory(options.mount_point, error) ||
        error) {
      return Error(ErrorCode::kInvalidArgument,
                   "mount point must be a drive letter or an existing directory");
    }
  }
  if (!options.appcontainer_sid.empty() && !ValidAppContainerSid(options.appcontainer_sid)) {
    return Error(ErrorCode::kInvalidArgument, "AppContainer SID is malformed");
  }

  OpenTraceFile();
  Trace("mount begin point=%ls sid=%ls", options.mount_point.c_str(),
        options.appcontainer_sid.c_str());

  auto api = WinFspApi::Load();
  if (!api.ok()) return api.error();
  Trace("winfsp runtime loaded");

  auto impl = std::make_unique<Impl>();
  impl->system.api = api.Take();
  impl->system.view = view;
  impl->system.volume_label = options.volume_label;
  std::wstring mount_point = options.mount_point;
  if (is_drive) {
    mount_point[0] = static_cast<wchar_t>(mount_point[0] | 0x20);
    mount_point[0] = static_cast<wchar_t>(mount_point[0] - L'a' + L'A');
  }

  auto user_sid = CurrentUserSid();
  if (!user_sid.ok()) return user_sid.error();
  auto file_sd = MakeSecurityDescriptor([&options, &user_sid] {
    const wchar_t* override_sddl = _wgetenv(L"RVM_FILE_SDDL");
    if (override_sddl && *override_sddl) {
      Trace("file sddl override=%ls", override_sddl);
      return std::wstring(override_sddl);
    }
    // AppContainer restricted tokens only match explicit package/AC SIDs; Everyone is not enough.
    std::wstring sddl = L"O:SYG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;AC)(A;;FA;;;" +
                        user_sid.value() + L")";
    if (!options.appcontainer_sid.empty()) {
      sddl += L"(A;;FA;;;" + options.appcontainer_sid + L")";
    }
    for (const std::wstring& sid : options.extra_sids) {
      sddl += L"(A;;FA;;;" + sid + L")";
    }
    return sddl;
  }());
  if (!file_sd.ok()) return file_sd.error();
  impl->system.file_sd = file_sd.Take();

  std::wstring mount_sddl =
      L"O:SYG:SYD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;WD)(A;;GA;;;AC)(A;;GA;;;" +
      user_sid.value() + L")";
  if (!options.appcontainer_sid.empty()) {
    mount_sddl += L"(A;;GA;;;" + options.appcontainer_sid + L")";
  }
  for (const std::wstring& sid : options.extra_sids) {
    mount_sddl += L"(A;;GA;;;" + sid + L")";
  }
  auto mount_sd = MakeSecurityDescriptor(mount_sddl);
  if (!mount_sd.ok()) return mount_sd.error();
  impl->system.mount_sd = mount_sd.Take();

  FSP_FSCTL_VOLUME_PARAMS params{};
  params.Version = 0;
  params.SectorSize = 512;
  params.SectorsPerAllocationUnit = 1;
  params.MaxComponentLength = 255;
  params.FileInfoTimeout = 1000;
  params.CasePreservedNames = 1;
  params.PersistentAcls = 1;
  params.UnicodeOnDisk = 1;
  params.VolumeSerialNumber = 0x52564D31;
  FILETIME now{};
  GetSystemTimeAsFileTime(&now);
  params.VolumeCreationTime =
      (static_cast<std::uint64_t>(now.dwHighDateTime) << 32) | now.dwLowDateTime;
  std::wcsncpy(params.FileSystemName, L"RVM",
               sizeof(params.FileSystemName) / sizeof(wchar_t) - 1);

  const FSP_FILE_SYSTEM_INTERFACE& iface = SharedInterface();
  wchar_t device_path[] = L"WinFsp.Disk";
  if (impl->system.api.debug_log_set_handle && impl->system.api.debug_log_set_mask) {
    impl->system.debug_log = OpenLogFile(L"rvm-winfsp-fsp.log");
    if (impl->system.debug_log != INVALID_HANDLE_VALUE) {
      impl->system.api.debug_log_set_handle(impl->system.debug_log);
      Trace("fsp debug log attached");
    }
  }
  Trace("create begin");
  NTSTATUS status =
      impl->system.api.create(device_path, &params, &iface, &impl->system.fs);
  if (status != STATUS_SUCCESS) {
    Trace("create failed ntstatus=%ld", status);
    return Error(ErrorCode::kBackendFailed,
                 "FspFileSystemCreate failed with NTSTATUS " + std::to_string(status));
  }
  Trace("create done");
  impl->system.fs->UserContext = &impl->system;
  impl->system.api.guard(impl->system.fs, FSP_FILE_SYSTEM_OPERATION_GUARD_STRATEGY_FINE);
  if (impl->system.api.debug_log_set_mask) {
    impl->system.api.debug_log_set_mask(impl->system.fs, 0xFFFFFFFF);
    Trace("fsp debug log mask enabled");
  }

  Trace("set mount point begin");
  impl->system.mount_point_text = mount_point;
  status = impl->system.api.set_mount_point(impl->system.fs,
                                            impl->system.mount_point_text.data(),
                                            impl->system.mount_sd);
  if (status != STATUS_SUCCESS) {
    Trace("set mount point failed ntstatus=%ld", status);
    impl->system.api.destroy(impl->system.fs);
    impl->system.fs = nullptr;
    return Error(ErrorCode::kBackendFailed,
                 "FspFileSystemSetMountPointEx failed with NTSTATUS " + std::to_string(status));
  }
  Trace("set mount point done");
  const NTSTATUS started = impl->system.api.start(impl->system.fs, 0);
  Trace("dispatcher started status=%ld", started);
  impl->mounted = true;
  impl->mount_point = mount_point;

  return std::unique_ptr<MountedWorkspace>(
      new MountedWorkspace(std::move(impl), std::move(mount_point)));
}

void MountedWorkspace::Unmount() {
  if (!impl_) return;
  impl_->Release();
  impl_.reset();
}


}  // namespace rvm::workspace