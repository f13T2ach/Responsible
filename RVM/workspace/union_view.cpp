#include "workspace/union_view.h"

#include <windows.h>

#include <algorithm>
#include <cstddef>
#include <cwctype>
#include <functional>
#include <map>
#include <utility>

namespace rvm::workspace {
namespace {

constexpr std::size_t kMaxRelativePath = 4096;
constexpr std::size_t kMaxComponent = 255;
constexpr int kMaxCopyDepth = 64;

core::Error Win32Failure(core::ErrorCode code, const char* context, DWORD error) {
  return core::Error(code, std::string(context) + " failed", false,
                     "win32=" + std::to_string(error));
}

core::Error NotFound(std::string detail = {}) {
  return core::Error(core::ErrorCode::kNotFound, "entry does not exist", false,
                     std::move(detail));
}

bool Exists(const std::wstring& path) {
  return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
}

bool IsDirectory(const std::wstring& path) {
  const DWORD attributes = GetFileAttributesW(path.c_str());
  return attributes != INVALID_FILE_ATTRIBUTES &&
         (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

std::wstring Join(const std::wstring& root, std::wstring_view relative) {
  if (relative.empty()) return root;
  return root + L"\\" + std::wstring(relative);
}

std::wstring ParentOf(std::wstring_view relative) {
  const std::size_t pos = relative.find_last_of(L'\\');
  if (pos == std::wstring_view::npos) return std::wstring();
  return std::wstring(relative.substr(0, pos));
}

std::wstring ToUpper(std::wstring_view value) {
  std::wstring result(value);
  std::transform(result.begin(), result.end(), result.begin(),
                 [](wchar_t c) { return static_cast<wchar_t>(std::towupper(c)); });
  return result;
}

bool IsReservedComponent(std::wstring_view component) {
  static const wchar_t* const kReserved[] = {
      L"CON",  L"PRN",  L"AUX",  L"NUL",  L"COM1", L"COM2", L"COM3", L"COM4",
      L"COM5", L"COM6", L"COM7", L"COM8", L"COM9", L"LPT1", L"LPT2", L"LPT3",
      L"LPT4", L"LPT5", L"LPT6", L"LPT7", L"LPT8", L"LPT9",
  };
  const std::size_t dot = component.find(L'.');
  const std::wstring stem =
      ToUpper(dot == std::wstring_view::npos ? component : component.substr(0, dot));
  for (const wchar_t* reserved : kReserved) {
    if (stem == reserved) return true;
  }
  return false;
}

core::Status EnsureDirectories(const std::wstring& path) {
  if (IsDirectory(path)) return core::Status::Ok();
  if (Exists(path)) {
    return core::Error(core::ErrorCode::kBackendFailed, "path is not a directory", false,
                       "path exists");
  }
  const std::wstring parent = ParentOf(path);
  if (!parent.empty() && parent.size() < path.size() && !IsDirectory(parent)) {
    const core::Status created = EnsureDirectories(parent);
    if (!created.ok()) return created;
  }
  if (!CreateDirectoryW(path.c_str(), nullptr)) {
    const DWORD error = GetLastError();
    if (error != ERROR_ALREADY_EXISTS || !IsDirectory(path)) {
      return Win32Failure(core::ErrorCode::kBackendFailed, "create directory", error);
    }
  }
  return core::Status::Ok();
}

core::Status RemoveTree(const std::wstring& path) {
  const DWORD attributes = GetFileAttributesW(path.c_str());
  if (attributes == INVALID_FILE_ATTRIBUTES) {
    const DWORD error = GetLastError();
    if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) {
      return core::Status::Ok();
    }
    return Win32Failure(core::ErrorCode::kBackendFailed, "query path attributes", error);
  }

  if ((attributes & FILE_ATTRIBUTE_DIRECTORY) == 0 ||
      (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) {
    if (!DeleteFileW(path.c_str()) && !RemoveDirectoryW(path.c_str())) {
      return Win32Failure(core::ErrorCode::kBackendFailed, "delete file", GetLastError());
    }
    return core::Status::Ok();
  }

  WIN32_FIND_DATAW data{};
  const std::wstring pattern = path + L"\\*";
  HANDLE find = FindFirstFileW(pattern.c_str(), &data);
  if (find == INVALID_HANDLE_VALUE) {
    const DWORD error = GetLastError();
    if (error == ERROR_FILE_NOT_FOUND) {
      if (!RemoveDirectoryW(path.c_str())) {
        return Win32Failure(core::ErrorCode::kBackendFailed, "remove directory",
                            GetLastError());
      }
      return core::Status::Ok();
    }
    return Win32Failure(core::ErrorCode::kBackendFailed, "enumerate directory", error);
  }

  core::Status result = core::Status::Ok();
  do {
    if (wcscmp(data.cFileName, L".") == 0 || wcscmp(data.cFileName, L"..") == 0) continue;
    const core::Status removed = RemoveTree(path + L"\\" + data.cFileName);
    if (!removed.ok()) result = removed;
  } while (FindNextFileW(find, &data));
  FindClose(find);
  if (!result.ok()) return result;

  if (!RemoveDirectoryW(path.c_str())) {
    return Win32Failure(core::ErrorCode::kBackendFailed, "remove directory", GetLastError());
  }
  return core::Status::Ok();
}

struct CaseInsensitiveLess {
  bool operator()(const std::wstring& left, const std::wstring& right) const {
    return ToUpper(left) < ToUpper(right);
  }
};

bool ReadFindEntry(const WIN32_FIND_DATAW& data, std::wstring_view relative,
                   bool from_diff, UnionEntry* entry) {
  std::wstring child(relative);
  if (!child.empty()) child += L'\\';
  child += data.cFileName;
  entry->relative = std::move(child);
  entry->is_directory = (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
  entry->from_diff = from_diff;
  entry->size = (static_cast<std::uint64_t>(data.nFileSizeHigh) << 32) | data.nFileSizeLow;
  entry->last_write_time =
      (static_cast<std::uint64_t>(data.ftLastWriteTime.dwHighDateTime) << 32) |
      data.ftLastWriteTime.dwLowDateTime;
  return true;
}

}  // namespace

core::Result<std::wstring> UnionView::Normalize(std::wstring_view relative) {
  std::wstring text(relative);
  std::replace(text.begin(), text.end(), L'/', L'\\');
  while (!text.empty() && text.front() == L'\\') text.erase(text.begin());
  while (!text.empty() && text.back() == L'\\') text.pop_back();
  if (text.empty()) return core::Ok(std::wstring());
  if (text.size() > kMaxRelativePath) {
    return core::Error(core::ErrorCode::kInvalidArgument, "path is too long");
  }

  std::wstring result;
  std::size_t start = 0;
  while (true) {
    const std::size_t pos = text.find(L'\\', start);
    const std::size_t end = pos == std::wstring::npos ? text.size() : pos;
    const std::wstring_view component(text.data() + start, end - start);
    if (component.empty()) {
      return core::Error(core::ErrorCode::kInvalidArgument, "path has an empty component");
    }
    if (component == L"." || component == L"..") {
      return core::Error(core::ErrorCode::kInvalidArgument,
                         "path has a relative component");
    }
    if (component.size() > kMaxComponent) {
      return core::Error(core::ErrorCode::kInvalidArgument, "path component is too long");
    }
    for (const wchar_t c : component) {
      if (c < 0x20 || wcschr(L":*?\"<>|", c) != nullptr) {
        return core::Error(core::ErrorCode::kInvalidArgument,
                           "path has an invalid character");
      }
    }
    if (component.back() == L'.' || component.back() == L' ') {
      return core::Error(core::ErrorCode::kInvalidArgument,
                         "path component ends with a dot or space");
    }
    if (IsReservedComponent(component)) {
      return core::Error(core::ErrorCode::kInvalidArgument, "path uses a reserved name");
    }
    if (!result.empty()) result += L'\\';
    result.append(component);
    if (pos == std::wstring::npos) break;
    start = pos + 1;
  }
  return core::Ok(std::move(result));
}

core::Result<UnionView> UnionView::Create(UnionPaths paths) {
  if (!IsDirectory(paths.base_root)) {
    return core::Error(core::ErrorCode::kInvalidArgument,
                       "workspace base directory does not exist");
  }
  if (paths.opaque_root.empty()) paths.opaque_root = paths.tombstone_root + L"-opaque";
  const core::Status diff = EnsureDirectories(paths.diff_root);
  if (!diff.ok()) return diff.error();
  const core::Status tombstones = EnsureDirectories(paths.tombstone_root);
  if (!tombstones.ok()) return tombstones.error();
  const core::Status opaque = EnsureDirectories(paths.opaque_root);
  if (!opaque.ok()) return opaque.error();
  return UnionView(std::move(paths));
}

std::wstring UnionView::BasePath(std::wstring_view relative) const {
  return Join(paths_.base_root, relative);
}

std::wstring UnionView::DiffPath(std::wstring_view relative) const {
  return Join(paths_.diff_root, relative);
}

std::wstring UnionView::TombstonePath(std::wstring_view relative) const {
  return Join(paths_.tombstone_root, relative);
}

std::wstring UnionView::OpaquePath(std::wstring_view relative) const {
  return Join(paths_.opaque_root, relative);
}

bool UnionView::IsOpaque(std::wstring_view relative) const {
  return !relative.empty() && Exists(OpaquePath(relative));
}

core::Status UnionView::RecordOpaque(std::wstring_view relative) const {
  const std::wstring marker = OpaquePath(relative);
  const core::Status parent = EnsureDirectories(ParentOf(marker));
  if (!parent.ok()) return parent;
  HANDLE handle = CreateFileW(marker.c_str(), GENERIC_WRITE,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (handle == INVALID_HANDLE_VALUE) {
    return Win32Failure(core::ErrorCode::kBackendFailed, "create opaque marker",
                        GetLastError());
  }
  CloseHandle(handle);
  return core::Status::Ok();
}

bool UnionView::IsTombstoned(std::wstring_view relative) const {
  auto normalized = Normalize(relative);
  if (!normalized.ok()) return true;
  std::wstring current = normalized.Take();
  while (!current.empty()) {
    if (Exists(TombstonePath(current))) return true;
    current = ParentOf(current);
  }
  return false;
}

bool UnionView::HasAncestorTombstone(std::wstring_view relative) const {
  const std::wstring parent = ParentOf(relative);
  return !parent.empty() && IsTombstoned(parent);
}

core::Result<UnionEntry> UnionView::Stat(std::wstring_view relative) const {
  auto normalized = Normalize(relative);
  if (!normalized.ok()) return normalized.error();
  const std::wstring path = normalized.Take();
  if (IsTombstoned(path)) return NotFound("hidden by tombstone");

  WIN32_FILE_ATTRIBUTE_DATA data{};
  bool from_diff = true;
  std::wstring host = DiffPath(path);
  if (!GetFileAttributesExW(host.c_str(), GetFileExInfoStandard, &data)) {
    from_diff = false;
    host = BasePath(path);
    if (!GetFileAttributesExW(host.c_str(), GetFileExInfoStandard, &data)) {
      return NotFound();
    }
  }

  UnionEntry entry;
  entry.relative = path;
  entry.is_directory = (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
  entry.from_diff = from_diff;
  entry.size = (static_cast<std::uint64_t>(data.nFileSizeHigh) << 32) | data.nFileSizeLow;
  entry.last_write_time =
      (static_cast<std::uint64_t>(data.ftLastWriteTime.dwHighDateTime) << 32) |
      data.ftLastWriteTime.dwLowDateTime;
  return entry;
}

core::Result<std::vector<UnionEntry>> UnionView::List(std::wstring_view relative) const {
  auto normalized = Normalize(relative);
  if (!normalized.ok()) return normalized.error();
  const std::wstring path = normalized.Take();

  const auto directory = Stat(path);
  if (!directory.ok()) return directory.error();
  if (!directory.value().is_directory) {
    return core::Error(core::ErrorCode::kInvalidArgument, "entry is not a directory");
  }

  std::map<std::wstring, UnionEntry, CaseInsensitiveLess> merged;

  const auto scan = [&](const std::wstring& host, bool from_diff) -> core::Status {
    if (!IsDirectory(host)) return core::Status::Ok();
    WIN32_FIND_DATAW data{};
    const std::wstring pattern = host + L"\\*";
    HANDLE find = FindFirstFileW(pattern.c_str(), &data);
    if (find == INVALID_HANDLE_VALUE) {
      const DWORD error = GetLastError();
      if (error == ERROR_FILE_NOT_FOUND) return core::Status::Ok();
      return Win32Failure(core::ErrorCode::kBackendFailed, "enumerate directory", error);
    }
    do {
      if (wcscmp(data.cFileName, L".") == 0 || wcscmp(data.cFileName, L"..") == 0) continue;
      UnionEntry entry;
      ReadFindEntry(data, path, from_diff, &entry);
      if (IsTombstoned(entry.relative)) continue;
      merged.emplace(entry.relative, std::move(entry));
    } while (FindNextFileW(find, &data));
    FindClose(find);
    return core::Status::Ok();
  };

  const core::Status diff = scan(DiffPath(path), true);
  if (!diff.ok()) return diff.error();
  if (!IsOpaque(path)) {
    const core::Status base = scan(BasePath(path), false);
    if (!base.ok()) return base.error();
  }

  std::vector<UnionEntry> entries;
  entries.reserve(merged.size());
  for (auto& [name, entry] : merged) entries.push_back(std::move(entry));
  return entries;
}

core::Status UnionView::ClearExactTombstone(std::wstring_view relative) const {
  const std::wstring marker = TombstonePath(relative);
  const DWORD attributes = GetFileAttributesW(marker.c_str());
  if (attributes == INVALID_FILE_ATTRIBUTES) return core::Status::Ok();
  if ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0) return RemoveTree(marker);
  if (!DeleteFileW(marker.c_str())) {
    return Win32Failure(core::ErrorCode::kBackendFailed, "clear tombstone", GetLastError());
  }
  return core::Status::Ok();
}

core::Status UnionView::RecordTombstone(std::wstring_view relative) const {
  const std::wstring marker = TombstonePath(relative);
  const DWORD attributes = GetFileAttributesW(marker.c_str());
  if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
    const core::Status removed = RemoveTree(marker);
    if (!removed.ok()) return removed;
  }
  const core::Status parent = EnsureDirectories(ParentOf(marker));
  if (!parent.ok()) return parent;
  HANDLE handle = CreateFileW(marker.c_str(), GENERIC_WRITE,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (handle == INVALID_HANDLE_VALUE) {
    return Win32Failure(core::ErrorCode::kBackendFailed, "create tombstone", GetLastError());
  }
  CloseHandle(handle);
  return core::Status::Ok();
}

core::Status UnionView::EnsureDiffParent(std::wstring_view relative) const {
  const std::wstring parent = ParentOf(relative);
  if (parent.empty()) return EnsureDirectories(paths_.diff_root);
  return EnsureDirectories(DiffPath(parent));
}

core::Status UnionView::CreateEntry(std::wstring_view relative, bool directory) {
  auto normalized = Normalize(relative);
  if (!normalized.ok()) return normalized.error();
  const std::wstring path = normalized.Take();
  if (path.empty()) {
    return core::Status(core::Error(core::ErrorCode::kInvalidArgument,
                                    "cannot create the workspace root"));
  }

  bool was_tombstoned = false;
  if (Exists(TombstonePath(path))) {
    if (HasAncestorTombstone(path)) {
      return core::Status(NotFound("parent directory was deleted"));
    }
    was_tombstoned = true;
    const core::Status cleared = ClearExactTombstone(path);
    if (!cleared.ok()) return cleared;
  }

  if (Exists(DiffPath(path)) || (!was_tombstoned && Exists(BasePath(path)))) {
    return core::Status(core::Error(core::ErrorCode::kAlreadyExists, "entry already exists"));
  }

  const core::Status parent = EnsureDiffParent(path);
  if (!parent.ok()) return parent;

  if (directory) {
    if (!CreateDirectoryW(DiffPath(path).c_str(), nullptr)) {
      const DWORD error = GetLastError();
      if (error != ERROR_ALREADY_EXISTS) {
        return Win32Failure(core::ErrorCode::kBackendFailed, "create directory", error);
      }
    }
    if (was_tombstoned) {
      const core::Status opaque = RecordOpaque(path);
      if (!opaque.ok()) return opaque;
    }
    return core::Status::Ok();
  }

  HANDLE handle = CreateFileW(DiffPath(path).c_str(), GENERIC_WRITE | GENERIC_READ,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (handle == INVALID_HANDLE_VALUE) {
    return Win32Failure(core::ErrorCode::kBackendFailed, "create file", GetLastError());
  }
  CloseHandle(handle);
  return core::Status::Ok();
}

core::Status UnionView::EnsureDirectory(std::wstring_view relative) {
  auto normalized = Normalize(relative);
  if (!normalized.ok()) return normalized.error();
  const std::wstring path = normalized.Take();
  if (path.empty()) return core::Status::Ok();

  bool was_tombstoned = false;
  if (Exists(TombstonePath(path))) {
    if (HasAncestorTombstone(path)) {
      return core::Status(NotFound("parent directory was deleted"));
    }
    was_tombstoned = true;
    const core::Status cleared = ClearExactTombstone(path);
    if (!cleared.ok()) return cleared;
  }

  const core::Status created = EnsureDirectories(DiffPath(path));
  if (!created.ok()) return created;
  return was_tombstoned ? RecordOpaque(path) : core::Status::Ok();
}

core::Status UnionView::RemoveEntry(std::wstring_view relative) {
  auto normalized = Normalize(relative);
  if (!normalized.ok()) return normalized.error();
  const std::wstring path = normalized.Take();
  if (path.empty()) {
    return core::Status(core::Error(core::ErrorCode::kInvalidArgument,
                                    "cannot delete the workspace root"));
  }

  const auto entry = Stat(path);
  if (!entry.ok()) return core::Status(entry.error());

  const core::Status recorded = RecordTombstone(path);
  if (!recorded.ok()) return recorded;
  return RemoveTree(DiffPath(path));
}

core::Result<std::wstring> UnionView::CopyUp(std::wstring_view relative) const {
  auto normalized = Normalize(relative);
  if (!normalized.ok()) return normalized.error();
  const std::wstring path = normalized.Take();
  if (IsTombstoned(path)) return NotFound("hidden by tombstone");

  const std::wstring diff = DiffPath(path);
  if (Exists(diff)) return core::Ok(diff);

  const std::wstring base = BasePath(path);
  const DWORD attributes = GetFileAttributesW(base.c_str());
  if (attributes == INVALID_FILE_ATTRIBUTES) return NotFound();

  const core::Status parent = EnsureDiffParent(path);
  if (!parent.ok()) return parent.error();

  if ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
    if (!CreateDirectoryW(diff.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS) {
      return Win32Failure(core::ErrorCode::kBackendFailed, "create directory",
                          GetLastError());
    }
    return core::Ok(diff);
  }

  if (!CopyFileW(base.c_str(), diff.c_str(), TRUE)) {
    const DWORD error = GetLastError();
    if (error != ERROR_FILE_EXISTS) {
      return Win32Failure(core::ErrorCode::kBackendFailed, "copy up file", error);
    }
  }
  return core::Ok(diff);
}

core::Status UnionView::CopyUpTree(std::wstring_view relative) const {
  std::function<core::Status(std::wstring_view, int)> copy =
      [&](std::wstring_view path, int depth) -> core::Status {
    if (depth > kMaxCopyDepth) {
      return core::Status(
          core::Error(core::ErrorCode::kBackendFailed, "copy-up depth limit exceeded"));
    }
    const std::wstring base = BasePath(path);
    const DWORD attributes = GetFileAttributesW(base.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) {
      if (Exists(DiffPath(path))) return core::Status::Ok();
      return core::Status(NotFound());
    }

    if ((attributes & FILE_ATTRIBUTE_DIRECTORY) == 0) {
      const auto copied = CopyUp(path);
      return copied.ok() ? core::Status::Ok() : core::Status(copied.error());
    }

    const core::Status parent = EnsureDiffParent(path);
    if (!parent.ok()) return parent;
    if (!CreateDirectoryW(DiffPath(path).c_str(), nullptr) &&
        GetLastError() != ERROR_ALREADY_EXISTS) {
      return Win32Failure(core::ErrorCode::kBackendFailed, "create directory",
                          GetLastError());
    }

    WIN32_FIND_DATAW data{};
    const std::wstring pattern = base + L"\\*";
    HANDLE find = FindFirstFileW(pattern.c_str(), &data);
    if (find == INVALID_HANDLE_VALUE) {
      const DWORD error = GetLastError();
      if (error == ERROR_FILE_NOT_FOUND) return core::Status::Ok();
      return Win32Failure(core::ErrorCode::kBackendFailed, "enumerate directory", error);
    }
    core::Status result = core::Status::Ok();
    do {
      if (wcscmp(data.cFileName, L".") == 0 || wcscmp(data.cFileName, L"..") == 0) continue;
      UnionEntry child;
      ReadFindEntry(data, path, false, &child);
      if (IsTombstoned(child.relative)) continue;
      const core::Status copied = copy(child.relative, depth + 1);
      if (!copied.ok()) result = copied;
    } while (FindNextFileW(find, &data));
    FindClose(find);
    return result;
  };

  auto normalized = Normalize(relative);
  if (!normalized.ok()) return core::Status(normalized.error());
  const std::wstring path = normalized.Take();
  if (path.empty()) {
    return core::Status(
        core::Error(core::ErrorCode::kInvalidArgument, "cannot copy the workspace root"));
  }
  if (IsTombstoned(path)) return core::Status(NotFound("hidden by tombstone"));
  return copy(path, 0);
}

core::Status UnionView::Rename(std::wstring_view from, std::wstring_view to, bool replace) {
  auto source = Normalize(from);
  if (!source.ok()) return core::Status(source.error());
  auto destination = Normalize(to);
  if (!destination.ok()) return core::Status(destination.error());
  const std::wstring source_path = source.Take();
  const std::wstring destination_path = destination.Take();
  if (source_path.empty() || destination_path.empty()) {
    return core::Status(
        core::Error(core::ErrorCode::kInvalidArgument, "cannot rename the workspace root"));
  }
  if (ToUpper(source_path) == ToUpper(destination_path)) {
    const auto entry = Stat(source_path);
    return entry.ok() ? core::Status::Ok() : core::Status(entry.error());
  }

  const auto entry = Stat(source_path);
  if (!entry.ok()) return core::Status(entry.error());

  if (Exists(TombstonePath(destination_path))) {
    if (HasAncestorTombstone(destination_path)) {
      return core::Status(NotFound("parent directory was deleted"));
    }
    const core::Status cleared = ClearExactTombstone(destination_path);
    if (!cleared.ok()) return cleared;
  }

  const auto existing = Stat(destination_path);
  if (existing.ok()) {
    if (!replace) {
      return core::Status(
          core::Error(core::ErrorCode::kAlreadyExists, "destination already exists"));
    }
    const core::Status removed = RemoveTree(DiffPath(destination_path));
    if (!removed.ok()) return removed;
  }

  const core::Status copied =
      entry.value().is_directory ? CopyUpTree(source_path) : [&]() {
        const auto copy = CopyUp(source_path);
        return copy.ok() ? core::Status::Ok() : core::Status(copy.error());
      }();
  if (!copied.ok()) return copied;

  const core::Status parent = EnsureDiffParent(destination_path);
  if (!parent.ok()) return parent;

  if (!MoveFileExW(DiffPath(source_path).c_str(), DiffPath(destination_path).c_str(), 0)) {
    return Win32Failure(core::ErrorCode::kBackendFailed, "rename entry", GetLastError());
  }

  if (entry.value().is_directory) {
    const core::Status opaque = RecordOpaque(destination_path);
    if (!opaque.ok()) return opaque;
  }
  return RecordTombstone(source_path);
}

core::Result<std::wstring> UnionView::ResolvePath(std::wstring_view relative) const {
  auto normalized = Normalize(relative);
  if (!normalized.ok()) return normalized.error();
  const std::wstring path = normalized.Take();
  const auto entry = Stat(path);
  if (!entry.ok()) return entry.error();
  return core::Ok(entry.value().from_diff ? DiffPath(path) : BasePath(path));
}

core::Result<std::wstring> UnionView::DiffPathFor(std::wstring_view relative) const {
  auto normalized = Normalize(relative);
  if (!normalized.ok()) return normalized.error();
  if (IsTombstoned(normalized.value())) return NotFound("hidden by tombstone");
  return core::Ok(DiffPath(normalized.value()));
}

}  // namespace rvm::workspace