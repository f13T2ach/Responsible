#include "sandbox/backend.h"

#include <windows.h>

#include <aclapi.h>
#include <sddl.h>

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <map>
#include <set>
#include <sstream>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "core/log.h"
#include "environment/system_probe.h"
#include "sandbox/appcontainer.h"

namespace rvm::sandbox {
namespace {

namespace fs = std::filesystem;

constexpr std::uint32_t kDefaultMemoryMb = 2048;
constexpr std::uint32_t kDefaultMaxProcesses = 256;
constexpr std::uint32_t kMaxAppContainerName = 64;
constexpr wchar_t kFirstSessionDrive = L'R';

// A keyed workspace keeps one stable AppContainer identity: the session disk
// is granted to that SID once and the grant survives restarts, so reopening a
// workspace does not re-propagate ACLs across the disk. Unkeyed diagnostic
// sessions get a throwaway identity per process.
std::wstring ProfileNameFor(const runtime::EnvironmentSpec& spec) {
  std::wstring name = L"ResponsibleRvm";
  if (!spec.storage_key.empty()) {
    std::uint32_t hash = 2166136261u;
    for (const wchar_t ch : spec.storage_key) {
      hash ^= static_cast<std::uint32_t>(ch);
      hash *= 16777619u;
    }
    for (const wchar_t ch : spec.storage_key) {
      if (name.size() >= 40) break;
      if ((ch >= L'a' && ch <= L'z') || (ch >= L'A' && ch <= L'Z') || (ch >= L'0' && ch <= L'9')) {
        name.push_back(ch);
      }
    }
    char suffix[16] = {};
    std::snprintf(suffix, sizeof(suffix), "%08x", hash);
    name += L"W";
    for (int i = 0; suffix[i] != 0; ++i) name.push_back(static_cast<wchar_t>(suffix[i]));
  } else {
    name += std::to_wstring(GetCurrentProcessId());
    for (const wchar_t ch : spec.session_id) {
      if (name.size() >= kMaxAppContainerName) break;
      const bool allowed = (ch >= L'a' && ch <= L'z') || (ch >= L'A' && ch <= L'Z') ||
                           (ch >= L'0' && ch <= L'9');
      if (allowed) name.push_back(ch);
    }
  }
  if (name.size() > kMaxAppContainerName) name.resize(kMaxAppContainerName);
  return name;
}

std::wstring PackageFolderName(const std::wstring& profile_name) {
  std::wstring folder = profile_name;
  for (wchar_t& ch : folder) {
    if (ch >= L'A' && ch <= L'Z') ch = static_cast<wchar_t>(ch - L'A' + L'a');
  }
  return folder;
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

// A drive letter is free when neither the volume list nor the DOS device
// namespace already defines it; a leftover DefineDosDevice link from a crashed
// engine must not be reused for a different workspace.
bool DriveLetterFree(wchar_t letter) {
  const DWORD mask = GetLogicalDrives();
  if ((mask & (1u << (letter - L'A'))) != 0) return false;
  const std::wstring device = std::wstring(1, letter) + L":";
  wchar_t target[1024] = {};
  if (QueryDosDeviceW(device.c_str(), target, 1024) != 0) return false;
  return GetLastError() == ERROR_FILE_NOT_FOUND;
}

std::wstring PickSessionDrive(const std::wstring& preferred) {
  const std::wstring normalized = NormalizeDrive(preferred);
  if (!normalized.empty() && DriveLetterFree(normalized[0])) return normalized;
  for (wchar_t letter = kFirstSessionDrive; letter <= L'Z'; ++letter) {
    if (DriveLetterFree(letter)) return std::wstring(1, letter) + L":";
  }
  for (wchar_t letter = L'D'; letter < kFirstSessionDrive; ++letter) {
    if (DriveLetterFree(letter)) return std::wstring(1, letter) + L":";
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

// The session temp lives beside the workspace, not under %LOCALAPPDATA%: an
// AppContainer has no traverse right through the host user profile, and adding
// one makes Windows re-propagate the profile's inheritable DACL (minutes on a
// populated AppData\Local). Keeping it under the workspace lets the existing
// workspace grant cover it and keeps temp off the system drive.
std::wstring WorkspaceTempRoot(const runtime::EnvironmentSpec& spec) {
  return (fs::path(spec.workspace_root) / L".rvm_temp").wstring();
}

std::string ToUtf8(const std::wstring& text) {
  if (text.empty()) return {};
  const int size = WideCharToMultiByte(CP_UTF8, 0, text.c_str(),
                                       static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
  if (size <= 0) return {};
  std::string utf8(static_cast<std::size_t>(size), '\0');
  WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), utf8.data(), size,
                      nullptr, nullptr);
  return utf8;
}

std::string UtcStamp() {
  SYSTEMTIME now{};
  GetSystemTime(&now);
  char buffer[32] = {};
  std::snprintf(buffer, sizeof(buffer), "%04u-%02u-%02uT%02u:%02u:%02uZ", now.wYear, now.wMonth,
                now.wDay, now.wHour, now.wMinute, now.wSecond);
  return buffer;
}

// Snapshot archive index: one line per session start/stop beside the per
// workspace disk. The clone itself keeps the state; the journal records when
// the sandbox was opened and against which host directory it was seeded.
void AppendSessionJournal(const std::wstring& storage_root, const std::string& entry) {
  std::error_code error;
  fs::create_directories(storage_root, error);
  if (error) return;
  std::ofstream journal(fs::path(storage_root) / L"sessions.log",
                        std::ios::app | std::ios::binary);
  if (journal) journal << entry << '\n';
}

// The session disk is a selective, incremental snapshot, not a full copy of
// the host tree. The file list comes from the workspace's git index (tracked
// files, submodules included), so ignored trees such as node_modules, package
// stores and build output are never walked or duplicated. A manifest beside
// the disk records the host and clone state of every seeded file: later
// sessions copy only host changes and leave files the sandbox edited alone,
// so the archive keeps behaving like a VM disk.
constexpr std::size_t kSeedMaxFiles = 40000;
constexpr std::uint64_t kSeedMaxBytes = 512ull * 1024 * 1024;
constexpr ULONGLONG kSeedMaxMillis = 60000;

std::wstring WideFromUtf8(const std::string& text) {
  if (text.empty()) return {};
  int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(),
                                 static_cast<int>(text.size()), nullptr, 0);
  if (size <= 0) {
    size = MultiByteToWideChar(CP_ACP, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
    if (size <= 0) return {};
    std::wstring wide(size, L'\0');
    MultiByteToWideChar(CP_ACP, 0, text.data(), static_cast<int>(text.size()), wide.data(), size);
    return wide;
  }
  std::wstring wide(size, L'\0');
  MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()),
                      wide.data(), size);
  return wide;
}

std::wstring FindGitExecutable() {
  wchar_t buffer[MAX_PATH] = {};
  const DWORD length = SearchPathW(nullptr, L"git.exe", nullptr, MAX_PATH, buffer, nullptr);
  return length > 0 && length < MAX_PATH ? std::wstring(buffer, length) : std::wstring();
}

bool RunToFile(const std::wstring& executable, const std::wstring& arguments,
               const std::wstring& working_directory, const std::wstring& output,
               DWORD timeout_ms) {
  SECURITY_ATTRIBUTES attributes{};
  attributes.nLength = sizeof(attributes);
  attributes.bInheritHandle = TRUE;
  const HANDLE file = CreateFileW(output.c_str(), GENERIC_WRITE, FILE_SHARE_READ, &attributes,
                                  CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY, nullptr);
  if (file == INVALID_HANDLE_VALUE) return false;
  const HANDLE nul = CreateFileW(L"NUL", GENERIC_READ | GENERIC_WRITE,
                                 FILE_SHARE_READ | FILE_SHARE_WRITE, &attributes, OPEN_EXISTING,
                                 0, nullptr);
  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  startup.dwFlags = STARTF_USESTDHANDLES;
  startup.hStdOutput = file;
  startup.hStdError = nul == INVALID_HANDLE_VALUE ? file : nul;
  startup.hStdInput = nul == INVALID_HANDLE_VALUE ? file : nul;
  PROCESS_INFORMATION process{};
  std::wstring command = L"\"" + executable + L"\" " + arguments;
  const BOOL created =
      CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW,
                     nullptr, working_directory.c_str(), &startup, &process);
  CloseHandle(file);
  bool success = false;
  if (created) {
    const DWORD waited = WaitForSingleObject(process.hProcess, timeout_ms);
    DWORD code = 1;
    if (waited == WAIT_OBJECT_0) {
      GetExitCodeProcess(process.hProcess, &code);
    } else {
      TerminateProcess(process.hProcess, 1);
      WaitForSingleObject(process.hProcess, 5000);
    }
    success = waited == WAIT_OBJECT_0 && code == 0;
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
  }
  if (nul != INVALID_HANDLE_VALUE) CloseHandle(nul);
  return success;
}

bool StatRegularFile(const fs::path& path, std::uint64_t* size, std::int64_t* mtime) {
  const DWORD attributes = GetFileAttributesW(path.c_str());
  if (attributes == INVALID_FILE_ATTRIBUTES ||
      (attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) != 0) {
    return false;
  }
  std::error_code error;
  const auto file_size = fs::file_size(path, error);
  if (error) return false;
  const auto write_time = fs::last_write_time(path, error);
  if (error) return false;
  *size = file_size;
  *mtime = static_cast<std::int64_t>(write_time.time_since_epoch().count());
  return true;
}

struct SeedRecord {
  std::uint64_t host_size = 0;
  std::int64_t host_time = 0;
  std::uint64_t clone_size = 0;
  std::int64_t clone_time = 0;
};

using SeedManifest = std::map<std::wstring, SeedRecord>;

SeedManifest ReadSeedManifest(const fs::path& path) {
  SeedManifest manifest;
  std::ifstream input(path, std::ios::binary);
  std::string line;
  while (std::getline(input, line)) {
    if (!line.empty() && line.back() == '\r') line.pop_back();
    const std::size_t first = line.find('\t');
    if (first == std::string::npos) continue;
    const std::size_t second = line.find('\t', first + 1);
    if (second == std::string::npos) continue;
    const std::size_t third = line.find('\t', second + 1);
    if (third == std::string::npos) continue;
    const std::size_t fourth = line.find('\t', third + 1);
    if (fourth == std::string::npos) continue;
    try {
      SeedRecord record;
      record.host_size = std::stoull(line.substr(0, first));
      record.host_time = std::stoll(line.substr(first + 1, second - first - 1));
      record.clone_size = std::stoull(line.substr(second + 1, third - second - 1));
      record.clone_time = std::stoll(line.substr(third + 1, fourth - third - 1));
      manifest[WideFromUtf8(line.substr(fourth + 1))] = record;
    } catch (const std::exception&) {
      continue;
    }
  }
  return manifest;
}

void WriteSeedManifest(const fs::path& path, const SeedManifest& manifest) {
  const fs::path temp = path.wstring() + L".tmp";
  std::ofstream output(temp, std::ios::trunc | std::ios::binary);
  if (!output) return;
  for (const auto& [rel, record] : manifest) {
    const std::string encoded = ToUtf8(rel);
    if (encoded.find('\t') != std::string::npos || encoded.find('\n') != std::string::npos ||
        encoded.find('\r') != std::string::npos) {
      continue;
    }
    output << record.host_size << '\t' << record.host_time << '\t' << record.clone_size << '\t'
           << record.clone_time << '\t' << encoded << '\n';
  }
  output.close();
  std::error_code error;
  fs::remove(path, error);
  fs::rename(temp, path, error);
}

std::string JsonString(const std::string& value) {
  std::string out;
  out.reserve(value.size() + 2);
  out.push_back('"');
  for (const unsigned char ch : value) {
    switch (ch) {
      case '"':
        out += "\\\"";
        break;
      case '\\':
        out += "\\\\";
        break;
      case '\b':
        out += "\\b";
        break;
      case '\f':
        out += "\\f";
        break;
      case '\n':
        out += "\\n";
        break;
      case '\r':
        out += "\\r";
        break;
      case '\t':
        out += "\\t";
        break;
      default:
        if (ch < 0x20) {
          char escaped[8] = {};
          std::snprintf(escaped, sizeof(escaped), "\\u%04x", ch);
          out += escaped;
        } else {
          out.push_back(static_cast<char>(ch));
        }
    }
  }
  out.push_back('"');
  return out;
}

std::string JsonString(const std::wstring& value) { return JsonString(ToUtf8(value)); }

bool IsWithinRoot(const std::wstring& root, const std::wstring& path) {
  if (root.empty() || path.size() < root.size()) return false;
  for (std::size_t i = 0; i < root.size(); ++i) {
    wchar_t left = root[i];
    wchar_t right = path[i];
    if (left >= L'A' && left <= L'Z') left = static_cast<wchar_t>(left - L'A' + L'a');
    if (right >= L'A' && right <= L'Z') right = static_cast<wchar_t>(right - L'A' + L'a');
    if (left != right) return false;
  }
  return path.size() == root.size() || path[root.size()] == L'\\' || path[root.size()] == L'/';
}

bool SafeRelativePath(const std::wstring& rel);

// Resolve a workspace-relative path without letting a junction in the host
// tree redirect a sync or rollback outside the workspace.
bool ResolveUnderRoot(const std::wstring& root, const std::wstring& rel, std::wstring* target) {
  if (!SafeRelativePath(rel)) return false;
  const fs::path candidate = fs::path(root) / fs::path(rel);
  std::error_code error;
  fs::path canonical_root = fs::weakly_canonical(fs::path(root), error);
  if (error) {
    error.clear();
    canonical_root = fs::path(root);
  }
  fs::path canonical_target = fs::weakly_canonical(candidate, error);
  if (error) {
    error.clear();
    canonical_target = candidate;
  }
  if (!IsWithinRoot(canonical_root.wstring(), canonical_target.wstring())) return false;
  *target = candidate.wstring();
  return true;
}

struct CloneFileInfo {
  std::uint64_t size = 0;
  std::int64_t time = 0;
};

constexpr std::size_t kWorkspaceWalkMaxFiles = 250000;
constexpr ULONGLONG kWorkspaceWalkMaxMillis = 30000;

// Enumerate the session disk, not the host workspace: only the archive is
// walked so diffing never scans the user's tree.
bool EnumerateCloneFiles(const std::wstring& root, std::map<std::wstring, CloneFileInfo>* files,
                         bool* truncated) {
  *truncated = false;
  const fs::path root_path(root);
  const ULONGLONG started = GetTickCount64();
  std::error_code error;
  fs::recursive_directory_iterator iterator(root_path,
                                           fs::directory_options::skip_permission_denied, error);
  const fs::recursive_directory_iterator end;
  for (; !error && iterator != end; iterator.increment(error)) {
    if (GetTickCount64() - started > kWorkspaceWalkMaxMillis ||
        files->size() >= kWorkspaceWalkMaxFiles) {
      *truncated = true;
      break;
    }
    const fs::path& path = iterator->path();
    const DWORD attributes = GetFileAttributesW(path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) continue;
    const bool directory = (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
    const bool reparse = (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    if (reparse) {
      if (directory) iterator.disable_recursion_pending();
      continue;
    }
    const std::wstring name = path.filename().wstring();
    if (directory) {
      if (name == L".rvm_temp" || name == L".git") iterator.disable_recursion_pending();
      continue;
    }
    const fs::path relative = path.lexically_relative(root_path);
    if (relative.empty()) continue;
    CloneFileInfo info;
    const auto size = iterator->file_size(error);
    if (error) {
      error.clear();
      continue;
    }
    const auto time = iterator->last_write_time(error);
    if (error) {
      error.clear();
      continue;
    }
    info.size = size;
    info.time = static_cast<std::int64_t>(time.time_since_epoch().count());
    (*files)[relative.wstring()] = info;
  }
  return !error;
}

bool ReplaceFileFrom(const std::wstring& source, const std::wstring& target,
                     std::string* detail) {
  std::error_code error;
  fs::create_directories(fs::path(target).parent_path(), error);
  if (error) {
    *detail = error.message();
    return false;
  }
  const std::wstring temp = target + L".rvm-sync-" + std::to_wstring(GetCurrentProcessId()) +
                            L"-" + std::to_wstring(GetTickCount64()) + L".tmp";
  if (!CopyFileW(source.c_str(), temp.c_str(), FALSE)) {
    *detail = "CopyFile failed: " + std::to_string(GetLastError());
    DeleteFileW(temp.c_str());
    return false;
  }
  if (!MoveFileExW(temp.c_str(), target.c_str(),
                   MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
    *detail = "MoveFileEx failed: " + std::to_string(GetLastError());
    DeleteFileW(temp.c_str());
    return false;
  }
  return true;
}

void AppendAuditLine(const std::wstring& storage_root, const std::string& event,
                     const std::string& session, const std::string& path,
                     const std::string& detail) {
  if (storage_root.empty()) return;
  std::error_code error;
  fs::create_directories(storage_root, error);
  if (error) return;
  std::ofstream audit(fs::path(storage_root) / L"audit.log",
                      std::ios::app | std::ios::binary);
  if (!audit) return;
  audit << "{\"at\":" << JsonString(UtcStamp()) << ",\"event\":" << JsonString(event)
        << ",\"session\":" << JsonString(session) << ",\"path\":" << JsonString(path)
        << ",\"detail\":" << JsonString(detail) << "}\n";
}

std::wstring ProfileStorePath(const std::wstring& profile_name) {
  wchar_t local_app_data[MAX_PATH] = {};
  const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local_app_data, MAX_PATH);
  if (length == 0 || length >= MAX_PATH) return {};
  return (fs::path(std::wstring(local_app_data, length)) / L"Packages" /
          PackageFolderName(profile_name))
      .wstring();
}

std::vector<std::wstring> ListTopLevelNames(const std::wstring& directory) {
  std::vector<std::wstring> names;
  if (directory.empty()) return names;
  std::error_code error;
  fs::directory_iterator iterator(fs::path(directory),
                                  fs::directory_options::skip_permission_denied, error);
  for (; !error && iterator != fs::directory_iterator() && names.size() < 128;
       iterator.increment(error)) {
    names.push_back(iterator->path().filename().wstring());
  }
  std::sort(names.begin(), names.end());
  return names;
}

// A trailing backslash before the closing quote would escape the quote in the
// command line, so drive roots keep a forward slash instead.
std::wstring GitPathArgument(std::wstring value) {
  while (value.size() > 3 && (value.back() == L'\\' || value.back() == L'/')) value.pop_back();
  if (value.size() == 3 && value.back() == L'\\') value.back() = L'/';
  return value;
}

struct WorkspaceListing {
  bool from_git = false;
  std::vector<std::wstring> files;
  std::vector<std::wstring> empty_dirs;
};

// git ls-files reads the index instead of walking the tree, which is the only
// enumeration that respects ignore rules without scanning node_modules or the
// package store. Without git the listing degrades to the top level only.
WorkspaceListing ListWorkspaceFiles(const std::wstring& base) {
  WorkspaceListing listing;
  const std::wstring git = FindGitExecutable();
  if (!git.empty()) {
    std::error_code error;
    const fs::path output =
        fs::temp_directory_path(error) /
        (L"rvm-seed-" + std::to_wstring(GetCurrentProcessId()) + L".txt");
    if (!error) {
      std::set<std::wstring> seen;
      const auto append = [&](const std::wstring& arguments) {
        if (!RunToFile(git, arguments, base, output.wstring(), 20000)) return false;
        std::ifstream input(output, std::ios::binary);
        std::string data((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
        std::size_t begin = 0;
        while (begin < data.size()) {
          const std::size_t end = data.find('\0', begin);
          const std::string item =
              data.substr(begin, end == std::string::npos ? std::string::npos : end - begin);
          begin = end == std::string::npos ? data.size() : end + 1;
          std::wstring rel = WideFromUtf8(item);
          for (wchar_t& ch : rel) {
            if (ch == L'/') ch = L'\\';
          }
          if (!rel.empty() && seen.insert(rel).second) listing.files.push_back(std::move(rel));
        }
        return true;
      };
      // Tracked files (submodules included) plus untracked files that the
      // ignore rules keep, so files the user created before the session are
      // visible while node_modules and build output stay out.
      const std::wstring git_base = GitPathArgument(base);
      listing.from_git =
          append(L"-C \"" + git_base + L"\" ls-files -z --cached --recurse-submodules");
      if (listing.from_git)
        append(L"-C \"" + git_base + L"\" ls-files -z --others --exclude-standard");
      fs::remove(output, error);
    }
  }
  if (listing.from_git) return listing;

  std::error_code error;
  fs::directory_iterator iterator(fs::path(base),
                                  fs::directory_options::skip_permission_denied, error);
  for (; !error && iterator != fs::directory_iterator(); iterator.increment(error)) {
    const DWORD attributes = GetFileAttributesW(iterator->path().c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES ||
        (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) {
      continue;
    }
    if ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
      listing.empty_dirs.push_back(iterator->path().filename().wstring());
    } else {
      listing.files.push_back(iterator->path().filename().wstring());
    }
  }
  return listing;
}

bool SafeRelativePath(const std::wstring& rel) {
  if (rel.empty()) return false;
  const fs::path path(rel);
  if (path.is_absolute() || path.has_root_name() || path.has_root_directory()) return false;
  for (const fs::path& part : path) {
    if (part == L"." || part == L"..") return false;
  }
  return true;
}

struct SeedStats {
  bool from_git = false;
  std::size_t copied = 0;
  std::size_t skipped = 0;
  std::size_t removed = 0;
  std::size_t conflicts = 0;
  std::size_t unreadable = 0;
};

core::Status SeedOrSyncWorkspace(const std::wstring& base, const std::wstring& clone,
                                 const std::wstring& storage_root, bool first_seed,
                                 SeedStats* stats) {
  std::error_code error;
  fs::create_directories(clone, error);
  if (error) {
    return core::Status::Failure(core::Error(
        core::ErrorCode::kBackendFailed, "failed to create the session disk directory", false,
        error.message()));
  }

  const fs::path base_path(base);
  const fs::path clone_path(clone);
  const fs::path manifest_path = fs::path(storage_root) / L"seed.manifest";
  const SeedManifest previous = first_seed ? SeedManifest{} : ReadSeedManifest(manifest_path);
  const WorkspaceListing listing = ListWorkspaceFiles(base);
  stats->from_git = listing.from_git;
  if (!listing.from_git) {
    // The session disk is defined by the workspace's git index so ignored
    // trees are never walked. Refuse to guess a scope from the filesystem.
    return core::Status::Failure(core::Error(
        core::ErrorCode::kInvalidState,
        "RVM 需要工作区的 Git 索引才能建立会话盘快照；请先在 Git 中为工作区建立索引",
        false, "git-index-missing: " + ToUtf8(base)));
  }

  struct PlannedEntry {
    std::wstring rel;
    std::uint64_t host_size = 0;
    std::int64_t host_time = 0;
    bool copy = false;
    // The archive and the host both changed since the baseline. Keep the old
    // host state in the manifest so the next diff still reports a conflict
    // instead of silently treating the new host bytes as the baseline.
    bool preserve_host_state = false;
  };
  std::vector<PlannedEntry> plan;
  plan.reserve(listing.files.size());
  std::set<std::wstring> present;
  std::size_t planned_files = 0;
  std::uint64_t planned_bytes = 0;

  for (const std::wstring& rel : listing.files) {
    if (!SafeRelativePath(rel)) continue;
    std::uint64_t size = 0;
    std::int64_t mtime = 0;
    if (!StatRegularFile(base_path / fs::path(rel), &size, &mtime)) {
      ++stats->unreadable;
      continue;
    }
    PlannedEntry entry;
    entry.rel = rel;
    entry.host_size = size;
    entry.host_time = mtime;
    const auto known = previous.find(rel);
    if (first_seed || known == previous.end()) {
      entry.copy = true;
    } else if (known->second.host_size == size && known->second.host_time == mtime) {
      entry.copy = false;
    } else {
      std::uint64_t clone_size = 0;
      std::int64_t clone_time = 0;
      const bool untouched =
          StatRegularFile(clone_path / fs::path(rel), &clone_size, &clone_time) &&
          clone_size == known->second.clone_size && clone_time == known->second.clone_time;
      entry.copy = untouched;
      if (!untouched) {
        ++stats->conflicts;
        entry.preserve_host_state = true;
      }
    }
    if (entry.copy) {
      ++planned_files;
      planned_bytes += size;
    }
    present.insert(rel);
    plan.push_back(std::move(entry));
  }

  if (planned_files > kSeedMaxFiles || planned_bytes > kSeedMaxBytes) {
    return core::Status::Failure(core::Error(
        core::ErrorCode::kBackendFailed, "the workspace exceeds the session disk seed budget", false,
        "files=" + std::to_string(planned_files) + " bytes=" + std::to_string(planned_bytes) +
            " limits=" + std::to_string(kSeedMaxFiles) + "/" +
            std::to_string(kSeedMaxBytes)));
  }

  SeedManifest next = previous;
  const ULONGLONG started = GetTickCount64();
  for (const PlannedEntry& entry : plan) {
    const fs::path source = base_path / fs::path(entry.rel);
    const fs::path target = clone_path / fs::path(entry.rel);
    if (entry.copy) {
      if (GetTickCount64() - started > kSeedMaxMillis) {
        return core::Status::Failure(core::Error(
            core::ErrorCode::kBackendFailed,
            "the session disk seed took longer than the time budget", false,
            "files=" + std::to_string(stats->copied)));
      }
      fs::create_directories(target.parent_path(), error);
      error.clear();
      fs::copy_file(source, target, fs::copy_options::overwrite_existing, error);
      if (error) {
        ++stats->unreadable;
        error.clear();
        continue;
      }
      SeedRecord record;
      record.host_size = entry.host_size;
      record.host_time = entry.host_time;
      StatRegularFile(target, &record.clone_size, &record.clone_time);
      next[entry.rel] = record;
      ++stats->copied;
    } else {
      ++stats->skipped;
      // Keep the host state current when the sandbox version was preserved
      // without a conflict; a conflict keeps the previous host state so the
      // diff can still refuse to overwrite work the user did on the host.
      if (!entry.preserve_host_state) {
        const auto known = next.find(entry.rel);
        if (known != next.end()) {
          known->second.host_size = entry.host_size;
          known->second.host_time = entry.host_time;
        }
      }
    }
  }

  for (const auto& [rel, record] : previous) {
    if (present.count(rel) != 0) continue;
    const fs::path target = clone_path / fs::path(rel);
    std::uint64_t clone_size = 0;
    std::int64_t clone_time = 0;
    if (StatRegularFile(target, &clone_size, &clone_time) && clone_size == record.clone_size &&
        clone_time == record.clone_time) {
      fs::remove(target, error);
      if (!error) {
        next.erase(rel);
        ++stats->removed;
      }
      error.clear();
    }
  }

  for (const std::wstring& dir : listing.empty_dirs) {
    if (!SafeRelativePath(dir)) continue;
    fs::create_directories(clone_path / fs::path(dir), error);
    error.clear();
  }

  // The change tracker snapshots turns with git, so the archive needs a repo
  // of its own; initializing one is cheap and avoids copying the host .git.
  if (listing.from_git && GetFileAttributesW((clone_path / L".git").c_str()) ==
                              INVALID_FILE_ATTRIBUTES) {
    const std::wstring git = FindGitExecutable();
    if (!git.empty()) {
      RunToFile(git, L"-C \"" + GitPathArgument(clone) + L"\" init -q", clone, L"NUL",
                20000);
    }
  }

  WriteSeedManifest(manifest_path, next);
  // The baseline is what the host contained at this start, expressed with the
  // clone state of the last clean copy. Anything the sandbox changes from here
  // on (including changes carried over from earlier sessions) shows up in the
  // workspace diff.
  const fs::path baseline_path = fs::path(storage_root) / L"baseline.manifest";
  std::error_code baseline_error;
  fs::copy_file(manifest_path, baseline_path, fs::copy_options::overwrite_existing,
                baseline_error);
  if (baseline_error) {
    return core::Status::Failure(core::Error(
        core::ErrorCode::kBackendFailed, "failed to record the workspace baseline", false,
        baseline_error.message()));
  }
  std::error_code ignored;
  fs::remove_all(clone_path / L".rvm_temp", ignored);
  return core::Status::Ok();
}

// Granting an inheritable ACE on a populated tree makes Windows re-propagate
// the whole DACL (minutes on a workspace), so a stable workspace identity
// grants once and later sessions reuse the existing entry.
bool DirectoryHasGrant(const std::wstring& sid, const std::wstring& path, bool write) {
  PSID target = nullptr;
  if (!ConvertStringSidToSidW(sid.c_str(), &target)) return false;
  PSECURITY_DESCRIPTOR descriptor = nullptr;
  PACL acl = nullptr;
  const DWORD status =
      GetNamedSecurityInfoW(const_cast<LPWSTR>(path.c_str()), SE_FILE_OBJECT,
                            DACL_SECURITY_INFORMATION, nullptr, nullptr, &acl, nullptr,
                            &descriptor);
  bool found = false;
  if (status == ERROR_SUCCESS && acl != nullptr) {
    ACL_SIZE_INFORMATION info{};
    if (GetAclInformation(acl, &info, sizeof(info), AclSizeInformation)) {
      const DWORD needed = write ? (GENERIC_READ | GENERIC_WRITE | DELETE | FILE_TRAVERSE)
                                 : (GENERIC_READ | FILE_TRAVERSE);
      for (DWORD index = 0; index < info.AceCount && !found; ++index) {
        void* raw = nullptr;
        if (!GetAce(acl, index, &raw)) continue;
        const auto* header = static_cast<const ACE_HEADER*>(raw);
        if (header->AceType != ACCESS_ALLOWED_ACE_TYPE) continue;
        const auto* allowed = static_cast<const ACCESS_ALLOWED_ACE*>(raw);
        const PSID ace_sid = reinterpret_cast<PSID>(
            const_cast<DWORD*>(&allowed->SidStart));
        if (!EqualSid(ace_sid, target)) continue;
        if ((allowed->Mask & needed) == needed) found = true;
      }
    }
  }
  if (descriptor != nullptr) LocalFree(descriptor);
  LocalFree(target);
  return found;
}

// System directories (System32, Program Files) already grant read/execute to
// every app package, but their DACLs are TrustedInstaller-owned, so rewriting
// them fails for a normal user. Detect the existing grant instead of warning.
bool DirectoryAllowsReadExecute(const std::wstring& sid, const std::wstring& path) {
  PSID target = nullptr;
  PSID all_packages = nullptr;
  if (!ConvertStringSidToSidW(sid.c_str(), &target)) return false;
  if (!ConvertStringSidToSidW(L"S-1-15-2-1", &all_packages)) {
    LocalFree(target);
    return false;
  }
  PSECURITY_DESCRIPTOR descriptor = nullptr;
  PACL acl = nullptr;
  const DWORD status =
      GetNamedSecurityInfoW(const_cast<LPWSTR>(path.c_str()), SE_FILE_OBJECT,
                            DACL_SECURITY_INFORMATION, nullptr, nullptr, &acl, nullptr,
                            &descriptor);
  bool found = false;
  if (status == ERROR_SUCCESS && acl != nullptr) {
    ACL_SIZE_INFORMATION info{};
    if (GetAclInformation(acl, &info, sizeof(info), AclSizeInformation)) {
      const DWORD needed = FILE_GENERIC_READ | FILE_GENERIC_EXECUTE | FILE_TRAVERSE;
      for (DWORD index = 0; index < info.AceCount && !found; ++index) {
        void* raw = nullptr;
        if (!GetAce(acl, index, &raw)) continue;
        const auto* header = static_cast<const ACE_HEADER*>(raw);
        if (header->AceType != ACCESS_ALLOWED_ACE_TYPE) continue;
        if ((header->AceFlags & INHERIT_ONLY_ACE) != 0) continue;
        const auto* allowed = static_cast<const ACCESS_ALLOWED_ACE*>(raw);
        const PSID ace_sid = reinterpret_cast<PSID>(
            const_cast<DWORD*>(&allowed->SidStart));
        if (!EqualSid(ace_sid, target) && !EqualSid(ace_sid, all_packages)) continue;
        if ((allowed->Mask & needed) == needed) found = true;
      }
    }
  }
  if (descriptor != nullptr) LocalFree(descriptor);
  LocalFree(all_packages);
  LocalFree(target);
  return found;
}

// The drive must resolve for every process in the logon session (the DSH
// node process hosts the file tools and is not a child of the engine), so the
// mapping is created through subst instead of the caller-local DOS device
// table. subst is the system tool for exactly this and needs no elevation.
core::Status RunSubst(const std::wstring& command_line, std::string* detail) {
  wchar_t system_root[MAX_PATH] = {};
  const UINT length = GetSystemDirectoryW(system_root, MAX_PATH);
  if (length == 0 || length >= MAX_PATH) {
    if (detail) *detail = "win32=" + std::to_string(GetLastError());
    return core::Status::Failure(core::Error(core::ErrorCode::kBackendFailed,
                                             "the system directory is not available", false,
                                             detail ? *detail : ""));
  }
  const std::wstring subst = (fs::path(system_root) / L"subst.exe").wstring();
  std::wstring mutable_command = L"\"" + subst + L"\" " + command_line;
  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  PROCESS_INFORMATION process{};
  const std::wstring working = fs::temp_directory_path().wstring();
  if (!CreateProcessW(subst.c_str(), mutable_command.data(), nullptr, nullptr, FALSE,
                      CREATE_NO_WINDOW, nullptr, working.c_str(), &startup, &process)) {
    if (detail) *detail = "win32=" + std::to_string(GetLastError());
    return core::Status::Failure(core::Error(core::ErrorCode::kBackendFailed,
                                             "failed to start subst.exe", false,
                                             detail ? *detail : ""));
  }
  const DWORD waited = WaitForSingleObject(process.hProcess, 30000);
  DWORD code = 1;
  if (waited == WAIT_OBJECT_0) GetExitCodeProcess(process.hProcess, &code);
  CloseHandle(process.hThread);
  CloseHandle(process.hProcess);
  if (waited != WAIT_OBJECT_0 || code != 0) {
    if (detail) *detail = "subst exit=" + std::to_string(code);
    return core::Status::Failure(core::Error(core::ErrorCode::kBackendFailed,
                                             "subst.exe did not map the drive", false,
                                             detail ? *detail : ""));
  }
  return core::Status::Ok();
}

bool DriveResolvesTo(const std::wstring& drive, const std::wstring& clone_root) {
  wchar_t target[1024] = {};
  if (QueryDosDeviceW(drive.c_str(), target, 1024) == 0) return false;
  std::wstring lower_target(target);
  std::wstring lower_root = clone_root;
  for (wchar_t& ch : lower_target) ch = static_cast<wchar_t>(std::towlower(ch));
  for (wchar_t& ch : lower_root) ch = static_cast<wchar_t>(std::towlower(ch));
  return lower_target.find(lower_root) != std::wstring::npos;
}

core::Status MapSessionDrive(const std::wstring& drive, const std::wstring& clone_root) {
  if (DriveResolvesTo(drive, clone_root)) return core::Status::Ok();
  std::string detail;
  const core::Status mapped = RunSubst(drive + L" \"" + clone_root + L"\"", &detail);
  if (!mapped.ok()) {
    return core::Status::Failure(core::Error(
        core::ErrorCode::kBackendFailed, "failed to map the session disk onto a drive", false,
        "drive=" + ToUtf8(drive) + " " + detail));
  }
  if (!DriveResolvesTo(drive, clone_root)) {
    return core::Status::Failure(core::Error(
        core::ErrorCode::kBackendFailed, "the drive does not resolve to the session disk", false,
        "drive=" + ToUtf8(drive)));
  }
  return core::Status::Ok();
}

void UnmapSessionDrive(const std::wstring& drive, const std::wstring& clone_root) {
  if (drive.empty() || clone_root.empty()) return;
  if (!DriveResolvesTo(drive, clone_root)) return;
  std::string ignored;
  RunSubst(drive + L" /D", &ignored);
}

std::wstring EnvironmentValue(const wchar_t* name) {
  const DWORD size = GetEnvironmentVariableW(name, nullptr, 0);
  if (size == 0) return {};
  std::wstring value(size, L'\0');
  const DWORD copied = GetEnvironmentVariableW(name, value.data(), size);
  if (copied == 0 || copied >= size) return {};
  value.resize(copied);
  return value;
}

// The shell tool needs its host executable to be readable/executable by the
// AppContainer; cmd.exe in System32 is covered by ALL APPLICATION PACKAGES,
// but PowerShell 7 and the legacy engine may not be.
std::vector<std::wstring> ShellHostDirectories() {
  std::vector<std::wstring> dirs;
  const std::wstring program_files = EnvironmentValue(L"ProgramFiles");
  if (!program_files.empty()) {
    const std::wstring pwsh = (fs::path(program_files) / L"PowerShell" / L"7").wstring();
    std::error_code error;
    if (fs::is_directory(pwsh, error) && !error) dirs.push_back(pwsh);
  }
  const std::wstring system_root = EnvironmentValue(L"SystemRoot");
  if (!system_root.empty()) {
    const std::wstring legacy =
        (fs::path(system_root) / L"System32" / L"WindowsPowerShell" / L"v1.0").wstring();
    std::error_code error;
    if (fs::is_directory(legacy, error) && !error) dirs.push_back(legacy);
  }
  return dirs;
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
    const bool write = spec_.policy.mode != core::SandboxMode::kReadOnly;
    phase_ = runtime::EnvironmentPhase::kCreating;

    auto profile = CreateProfile(ProfileNameFor(spec_));
    if (!profile.ok()) {
      phase_ = runtime::EnvironmentPhase::kFailed;
      return profile.error();
    }
    profile_ = profile.Take();
    core::LogDebug("sandbox", "AppContainer profile ready");

    // Outbound-only network: the internetClient capability lets the child open
    // outbound connections. Loopback and inbound stay masked because the token
    // has neither privateNetworkClientServer nor a loopback exemption.
    if (spec_.policy.network_access) {
      auto network_sid = DeriveCapabilitySid(L"internetClient");
      if (!network_sid.ok()) {
        StopProfile();
        phase_ = runtime::EnvironmentPhase::kFailed;
        return network_sid.error();
      }
      network_capability_sid_ = network_sid.Take();
      core::LogDebug("sandbox", "outbound network capability ready: " +
                                    ToUtf8(network_capability_sid_));
    }

    // A protected workspace runs on its own session disk mapped onto a drive,
    // never in the host directory: the base tree is only read to seed the disk
    // and is not granted to the AppContainer at all.
    if (spec_.overlay_workspace) {
      const core::Status prepared = PrepareSessionDisk(profile_.sid, write);
      if (!prepared.ok()) {
        StopProfile();
        phase_ = runtime::EnvironmentPhase::kFailed;
        return prepared.error();
      }
      temp_root_ = (fs::path(clone_root_) / L".rvm_temp").wstring();
    } else {
      temp_root_ = WorkspaceTempRoot(spec_);
    }

    std::error_code temp_error;
    fs::create_directories(temp_root_, temp_error);
    const fs::path home = fs::path(temp_root_) / L"home";
    if (!temp_error) fs::create_directories(home / L"AppData" / L"Local", temp_error);
    if (!temp_error) fs::create_directories(home / L"AppData" / L"Roaming", temp_error);
    // The AppContainer identity rewrites TEMP/TMP/LOCALAPPDATA to its package
    // folder under LOCALAPPDATA. That folder must exist and be inside the
    // granted temp tree, otherwise $env:TEMP points at a missing path.
    const fs::path package_temp =
        home / L"AppData" / L"Local" / L"Packages" / PackageFolderName(profile_.name) / L"AC" /
        L"Temp";
    if (!temp_error) fs::create_directories(package_temp, temp_error);
    if (temp_error) {
      StopProfile();
      phase_ = runtime::EnvironmentPhase::kFailed;
      return core::Error(core::ErrorCode::kBackendFailed,
                         "failed to create the session temp directory", false,
                         temp_error.message());
    }
    core::LogDebug("sandbox", "session temp directories created");

    // Every sandboxed process gets its own temp tree. It sits under the
    // workspace (or the session disk), so the workspace grant already covers
    // it; no host profile traversal is involved.
    core::LogDebug("sandbox", "session temp ready");

    if (spec_.overlay_workspace) {
      const core::Status reachable = VerifySessionDiskAccess(write);
      if (!reachable.ok()) {
        Stop();
        phase_ = runtime::EnvironmentPhase::kFailed;
        return reachable.error();
      }
      core::LogDebug("sandbox", "mounted workspace access verified");
    } else {
      core::Status granted = GrantDirectory(profile_.sid, spec_.workspace_root, write, true);
      if (!granted.ok()) {
        Stop();
        phase_ = runtime::EnvironmentPhase::kFailed;
        return granted.error();
      }
      granted_directories_.push_back(spec_.workspace_root);

      const core::Status temp = GrantDirectory(profile_.sid, temp_root_, write, true);
      if (!temp.ok()) {
        Stop();
        phase_ = runtime::EnvironmentPhase::kFailed;
        return temp.error();
      }
      granted_directories_.push_back(temp_root_);

      const core::Status reachable = VerifyWorkspaceAccess(write);
      if (!reachable.ok()) {
        Stop();
        phase_ = runtime::EnvironmentPhase::kFailed;
        return reachable.error();
      }
      core::LogDebug("sandbox", "direct workspace access verified");
    }

    // The user explicitly exposed these tool compilers/interpreters. They get
    // read/execute only; writes stay confined to the workspace and temp.
    for (const std::wstring& tool : spec_.tool_paths) {
      std::error_code tool_error;
      if (!IsAbsolutePath(tool) || !fs::is_directory(tool, tool_error) || tool_error) {
        Stop();
        phase_ = runtime::EnvironmentPhase::kFailed;
        return core::Error(core::ErrorCode::kInvalidArgument,
                           "an exposed tool directory is not an absolute directory", false,
                           std::string(tool.begin(), tool.end()));
      }
      if (std::find(granted_directories_.begin(), granted_directories_.end(), tool) !=
          granted_directories_.end()) {
        continue;
      }
      const core::Status granted = GrantDirectoryReadExecute(profile_.sid, tool, true);
      if (!granted.ok()) {
        Stop();
        phase_ = runtime::EnvironmentPhase::kFailed;
        return granted.error();
      }
      granted_directories_.push_back(tool);
    }
    if (!spec_.tool_paths.empty()) core::LogDebug("sandbox", "exposed tool directories granted");

    // The shell host executable (pwsh) is provided by the host toolchain, not
    // by the user's tool list; it still needs an explicit read/execute grant.
    for (const std::wstring& dir : ShellHostDirectories()) {
      if (std::find(granted_directories_.begin(), granted_directories_.end(), dir) !=
          granted_directories_.end()) {
        continue;
      }
      if (DirectoryAllowsReadExecute(profile_.sid, dir)) {
        core::LogDebug("sandbox", "shell host directory already readable by app packages");
        continue;
      }
      const core::Status granted = GrantDirectoryReadExecute(profile_.sid, dir, true);
      if (!granted.ok()) {
        core::LogWarn("sandbox", "could not grant read access to a shell host directory: " +
                                     granted.error().message);
        continue;
      }
      granted_directories_.push_back(dir);
    }
    core::LogDebug("sandbox", "shell host directories granted");

    // Device penetration is per-device and revocable. A device the engine
    // cannot open for WRITE_DAC fails the start instead of silently staying
    // masked while the UI reports it as exposed.
    for (const std::wstring& device : spec_.devices) {
      if (DeviceAllowsAccess(profile_.sid, device)) {
        core::LogDebug("sandbox", "device already readable by app packages");
        continue;
      }
      const core::Status granted = GrantDeviceAccess(profile_.sid, device);
      if (!granted.ok()) {
        Stop();
        phase_ = runtime::EnvironmentPhase::kFailed;
        return granted.error();
      }
      granted_devices_.push_back(device);
    }
    if (!granted_devices_.empty()) core::LogDebug("sandbox", "exposed devices granted");

    // Fail closed unless the containment really denies the host profile.
    const core::Status isolated = VerifyHostIsolation();
    if (!isolated.ok()) {
      Stop();
      phase_ = runtime::EnvironmentPhase::kFailed;
      return isolated.error();
    }
    core::LogDebug("sandbox", "host profile isolation verified");

    phase_ = runtime::EnvironmentPhase::kReady;
    runtime::EnvironmentFacts facts;
    facts.workspace_path =
        session_workspace_.empty() ? spec_.workspace_root : session_workspace_;
    facts.temp_path = temp_root_;
    const std::string build = environment::CurrentWindowsBuild();
    windows_build_ = std::wstring(build.begin(), build.end());
    facts.windows_build = windows_build_;
    facts.memory_mb = spec_.memory_mb == 0 ? kDefaultMemoryMb : spec_.memory_mb;
    facts.vcpu_count = spec_.vcpu_count == 0
                           ? std::max(1u, std::thread::hardware_concurrency())
                           : spec_.vcpu_count;
    core::LogDebug("sandbox", "probing virtualization facts");
    vbs_enabled_ = environment::ProbeVbs().ok();
    hvci_enabled_ = environment::ProbeHvci().ok();
    facts.vbs_enabled = vbs_enabled_;
    facts.hvci_enabled = hvci_enabled_;
    facts.network_enabled = spec_.policy.network_access;
    facts.network_capability_sid = network_capability_sid_;
    facts.archive_root = storage_root_;
    facts.device_paths = spec_.devices;
    facts.appcontainer_sid = profile_.sid;
    facts.restricted_token = false;
    facts.exposed_environment = spec_.exposed_environment;
    facts.tool_paths = spec_.tool_paths;
    for (const runtime::ToolAccess& tool : spec_.tools) {
      const bool listed =
          std::any_of(facts.tool_paths.begin(), facts.tool_paths.end(),
                      [&](const std::wstring& path) { return path == tool.path; });
      if (!listed) facts.tool_paths.push_back(tool.path);
    }
    facts.tools = spec_.tools;
    profile_entries_at_start_ = ListTopLevelNames(ProfileStorePath(profile_.name));
    RecordSystemSnapshot("start");
    core::LogInfo("sandbox", "sandbox environment ready");
    return facts;
  }

  core::Status Stop() override {
    if (profile_.name.empty() && clone_drive_.empty() && temp_root_.empty()) {
      phase_ = runtime::EnvironmentPhase::kStopped;
      return core::Status::Ok();
    }
    phase_ = runtime::EnvironmentPhase::kStopping;
    RecordSystemSnapshot("stop");
    core::Status result = core::Status::Ok();
    for (const std::wstring& device : granted_devices_) {
      const core::Status revoked = RevokeDeviceAccess(profile_.sid, device);
      if (!revoked.ok()) {
        core::LogWarn("sandbox", revoked.error().message);
        result = revoked;
      }
    }
    granted_devices_.clear();
    // Retire the drive first: the sandbox must not keep a usable letter while
    // grants are revoked.
    const std::wstring released_drive = clone_drive_;
    const std::wstring released_root = clone_root_;
    UnmapSessionDrive(released_drive, released_root);
    for (auto it = granted_directories_.rbegin(); it != granted_directories_.rend(); ++it) {
      const core::Status revoked = RevokeDirectory(profile_.sid, *it, true);
      if (!revoked.ok()) {
        core::LogWarn("sandbox", revoked.error().message);
        result = revoked;
      }
    }
    granted_directories_.clear();
    RemoveTempRoot();
    // The journal and, for a keyed workspace, the session disk stay on disk:
    // that archive is what the next session boots from. Reusing the identity
    // and its disk grant keeps the next open instant.
    RemoveCloneRoot();
    clone_drive_.clear();
    session_workspace_.clear();
    core::Status deleted = core::Status::Ok();
    if (!clone_persistent_ && !profile_.name.empty()) deleted = DeleteProfile(profile_.name);
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

  core::Result<runtime::WorkspaceDiff> DiffWorkspace() override {
    if (phase_ != runtime::EnvironmentPhase::kReady || clone_root_.empty()) {
      return core::Error(core::ErrorCode::kInvalidState,
                         "the session has no active workspace archive");
    }
    return BuildDiff();
  }

  core::Result<runtime::WorkspaceApplyResult> ApplyWorkspace(
      const std::vector<std::wstring>& paths, bool all) override {
    if (phase_ != runtime::EnvironmentPhase::kReady || clone_root_.empty()) {
      return core::Error(core::ErrorCode::kInvalidState,
                         "the session has no active workspace archive");
    }
    auto diff = BuildDiff();
    if (!diff.ok()) return diff.error();

    runtime::WorkspaceApplyResult result;
    std::map<std::wstring, const runtime::WorkspaceChange*> changes;
    for (const auto& change : diff.value().changes) changes[change.path] = &change;

    std::vector<std::wstring> selected;
    if (all) {
      for (const auto& change : diff.value().changes) selected.push_back(change.path);
    } else {
      for (const std::wstring& raw : paths) {
        std::wstring rel = raw;
        for (wchar_t& ch : rel) {
          if (ch == L'/') ch = L'\\';
        }
        while (!rel.empty() && rel.front() == L'\\') rel.erase(rel.begin());
        if (!SafeRelativePath(rel)) {
          result.conflicts.emplace_back(raw, "路径不在工作区内");
          continue;
        }
        if (changes.find(rel) == changes.end()) {
          result.conflicts.emplace_back(rel, "路径没有变更");
          continue;
        }
        selected.push_back(std::move(rel));
      }
    }

    auto baseline = ReadSeedManifest(fs::path(storage_root_) / L"baseline.manifest");
    auto seed = ReadSeedManifest(fs::path(storage_root_) / L"seed.manifest");
    bool touched = false;
    for (const std::wstring& rel : selected) {
      const auto found = changes.find(rel);
      if (found == changes.end()) continue;
      const runtime::WorkspaceChange& change = *found->second;
      if (change.conflict) {
        result.conflicts.emplace_back(
            rel, change.conflict_reason.empty() ? "宿主文件在快照后被修改" : change.conflict_reason);
        continue;
      }
      std::wstring host_target;
      if (!ResolveUnderRoot(spec_.workspace_root, rel, &host_target)) {
        result.conflicts.emplace_back(rel, "路径解析到工作区之外");
        continue;
      }
      std::string failure;
      if (change.kind == runtime::WorkspaceChangeKind::kDeleted) {
        if (GetFileAttributesW(host_target.c_str()) != INVALID_FILE_ATTRIBUTES &&
            !DeleteFileW(host_target.c_str())) {
          result.conflicts.emplace_back(rel, "无法删除宿主文件：错误 " +
                                                 std::to_string(GetLastError()));
          continue;
        }
      } else {
        const std::wstring source = (fs::path(clone_root_) / fs::path(rel)).wstring();
        if (!ReplaceFileFrom(source, host_target, &failure)) {
          result.conflicts.emplace_back(rel, "无法写入宿主文件：" + failure);
          continue;
        }
      }
      UpdateManifestsAfterApply(rel, host_target, &baseline, &seed);
      result.applied.push_back(change);
      touched = true;
    }
    if (touched) {
      WriteSeedManifest(fs::path(storage_root_) / L"baseline.manifest", baseline);
      WriteSeedManifest(fs::path(storage_root_) / L"seed.manifest", seed);
      AppendAuditLine(storage_root_, "sync_applied", ToUtf8(spec_.session_id), "",
                      "applied=" + std::to_string(result.applied.size()) +
                          " conflicts=" + std::to_string(result.conflicts.size()));
      AppendSessionJournal(storage_root_, "sync " + UtcStamp() + " session=" +
                                              ToUtf8(spec_.session_id) + " applied=" +
                                              std::to_string(result.applied.size()) +
                                              " conflicts=" +
                                              std::to_string(result.conflicts.size()));
    }
    return core::Ok(std::move(result));
  }

  core::Status RollbackWorkspace() override {
    if (phase_ != runtime::EnvironmentPhase::kReady || clone_root_.empty()) {
      return core::Status::Failure(core::Error(
          core::ErrorCode::kInvalidState, "the session has no active workspace archive"));
    }
    auto diff = BuildDiff();
    if (!diff.ok()) return core::Status::Failure(diff.error());

    auto baseline = ReadSeedManifest(fs::path(storage_root_) / L"baseline.manifest");
    auto seed = ReadSeedManifest(fs::path(storage_root_) / L"seed.manifest");
    std::size_t rolled_back = 0;
    for (const runtime::WorkspaceChange& change : diff.value().changes) {
      std::wstring host_target;
      if (!ResolveUnderRoot(spec_.workspace_root, change.path, &host_target)) continue;
      const std::wstring clone_target =
          (fs::path(clone_root_) / fs::path(change.path)).wstring();
      std::uint64_t host_size = 0;
      std::int64_t host_time = 0;
      const bool host_exists = StatRegularFile(host_target, &host_size, &host_time);
      std::string failure;
      if (host_exists) {
        if (!ReplaceFileFrom(host_target, clone_target, &failure)) {
          core::LogWarn("sandbox", "rollback could not restore " + ToUtf8(change.path) + ": " +
                                       failure);
          continue;
        }
        SeedRecord record;
        record.host_size = host_size;
        record.host_time = host_time;
        StatRegularFile(clone_target, &record.clone_size, &record.clone_time);
        baseline[change.path] = record;
        seed[change.path] = record;
      } else {
        std::error_code error;
        fs::remove(clone_target, error);
        baseline.erase(change.path);
        seed.erase(change.path);
      }
      rolled_back++;
    }
    WriteSeedManifest(fs::path(storage_root_) / L"baseline.manifest", baseline);
    WriteSeedManifest(fs::path(storage_root_) / L"seed.manifest", seed);
    AppendAuditLine(storage_root_, "rollback", ToUtf8(spec_.session_id), "",
                    "rolled_back=" + std::to_string(rolled_back));
    AppendSessionJournal(storage_root_, "rollback " + UtcStamp() + " session=" +
                                            ToUtf8(spec_.session_id) +
                                            " changes=" + std::to_string(rolled_back));
    return core::Status::Ok();
  }

 private:
  core::Result<runtime::WorkspaceDiff> BuildDiff() {
    const fs::path baseline_path = fs::path(storage_root_) / L"baseline.manifest";
    std::error_code baseline_error;
    if (!fs::is_regular_file(baseline_path, baseline_error) || baseline_error) {
      return core::Error(core::ErrorCode::kInvalidState,
                         "the workspace baseline is missing from the session archive");
    }
    const SeedManifest baseline = ReadSeedManifest(baseline_path);
    std::map<std::wstring, CloneFileInfo> clone_files;
    bool truncated = false;
    if (!EnumerateCloneFiles(clone_root_, &clone_files, &truncated)) {
      return core::Error(core::ErrorCode::kBackendFailed,
                         "failed to enumerate the session disk for the diff");
    }

    runtime::WorkspaceDiff diff;
    diff.truncated = truncated;
    std::set<std::wstring> paths;
    for (const auto& [rel, record] : baseline) {
      if (SafeRelativePath(rel)) paths.insert(rel);
    }
    for (const auto& [rel, info] : clone_files) {
      if (SafeRelativePath(rel)) paths.insert(rel);
    }

    for (const std::wstring& rel : paths) {
      const auto before = baseline.find(rel);
      const auto after = clone_files.find(rel);
      runtime::WorkspaceChange change;
      change.path = rel;
      if (before == baseline.end() && after != clone_files.end()) {
        change.kind = runtime::WorkspaceChangeKind::kAdded;
        change.after_bytes = after->second.size;
      } else if (before != baseline.end() && after == clone_files.end()) {
        change.kind = runtime::WorkspaceChangeKind::kDeleted;
        change.before_bytes = before->second.clone_size;
      } else if (before != baseline.end() && after != clone_files.end()) {
        if (before->second.clone_size == after->second.size &&
            before->second.clone_time == after->second.time) {
          continue;
        }
        change.kind = runtime::WorkspaceChangeKind::kModified;
        change.before_bytes = before->second.clone_size;
        change.after_bytes = after->second.size;
      } else {
        continue;
      }

      std::wstring host_target;
      if (!ResolveUnderRoot(spec_.workspace_root, rel, &host_target)) {
        change.conflict = true;
        change.conflict_reason = "路径解析到工作区之外";
        diff.changes.push_back(std::move(change));
        continue;
      }
      std::uint64_t host_size = 0;
      std::int64_t host_time = 0;
      const bool host_exists = StatRegularFile(host_target, &host_size, &host_time);
      if (change.kind == runtime::WorkspaceChangeKind::kAdded) {
        if (host_exists) {
          change.conflict = true;
          change.conflict_reason = "宿主工作区已存在同名文件";
        }
      } else if (before != baseline.end()) {
        if (!host_exists) {
          change.conflict = true;
          change.conflict_reason = "宿主文件在快照后被删除";
        } else if (host_size != before->second.host_size ||
                   host_time != before->second.host_time) {
          change.conflict = true;
          change.conflict_reason = "宿主文件在快照后被修改";
        }
      }
      diff.changes.push_back(std::move(change));
    }
    return core::Ok(std::move(diff));
  }

  void UpdateManifestsAfterApply(const std::wstring& rel, const std::wstring& host_target,
                                 SeedManifest* baseline, SeedManifest* seed) {
    const std::wstring clone_target = (fs::path(clone_root_) / fs::path(rel)).wstring();
    std::uint64_t host_size = 0;
    std::int64_t host_time = 0;
    std::uint64_t clone_size = 0;
    std::int64_t clone_time = 0;
    if (!StatRegularFile(host_target, &host_size, &host_time) ||
        !StatRegularFile(clone_target, &clone_size, &clone_time)) {
      baseline->erase(rel);
      seed->erase(rel);
      return;
    }
    SeedRecord record;
    record.host_size = host_size;
    record.host_time = host_time;
    record.clone_size = clone_size;
    record.clone_time = clone_time;
    (*baseline)[rel] = record;
    (*seed)[rel] = record;
  }
  // The system layer is read-only by construction: the AppContainer has no
  // write grant outside the session disk and its own package store. Record the
  // system configuration and any package-store entries it gained, so the
  // archive has an auditable system snapshot even when the answer is "none".
  void RecordSystemSnapshot(const char* event) {
    if (storage_root_.empty()) return;
    std::error_code error;
    fs::create_directories(storage_root_, error);
    if (error) return;

    const std::wstring profile_store = ProfileStorePath(profile_.name);
    const std::vector<std::wstring> current = ListTopLevelNames(profile_store);
    std::set<std::wstring> before(profile_entries_at_start_.begin(),
                                  profile_entries_at_start_.end());
    std::set<std::wstring> now(current.begin(), current.end());

    std::ostringstream json;
    json << "{\"schema\":1,\"event\":" << JsonString(std::string(event))
         << ",\"at\":" << JsonString(UtcStamp())
         << ",\"session\":" << JsonString(ToUtf8(spec_.session_id))
         << ",\"storage_key\":" << JsonString(ToUtf8(spec_.storage_key))
         << ",\"source\":" << JsonString(spec_.workspace_root)
         << ",\"archive\":" << JsonString(storage_root_)
         << ",\"session_disk\":" << JsonString(clone_root_)
         << ",\"drive\":" << JsonString(clone_drive_)
         << ",\"archive_kept\":" << (clone_persistent_ ? "true" : "false")
         << ",\"os_build\":" << JsonString(windows_build_)
         << ",\"vbs_enabled\":" << (vbs_enabled_ ? "true" : "false")
         << ",\"hvci_enabled\":" << (hvci_enabled_ ? "true" : "false")
         << ",\"network_enabled\":" << (spec_.policy.network_access ? "true" : "false")
         << ",\"network_mask\":\"outbound-only;host-loopback-and-inbound-masked\""
         << ",\"appcontainer\":{\"name\":" << JsonString(profile_.name)
         << ",\"sid\":" << JsonString(profile_.sid) << "}"
         << ",\"policy_mode\":";
    switch (spec_.policy.mode) {
      case core::SandboxMode::kReadOnly:
        json << "\"read-only\"";
        break;
      case core::SandboxMode::kWorkspaceWrite:
        json << "\"workspace-write\"";
        break;
      case core::SandboxMode::kDangerFullAccess:
        json << "\"danger-full-access\"";
        break;
    }
    json << ",\"grants\":[";
    for (std::size_t i = 0; i < granted_directories_.size(); ++i) {
      if (i != 0) json << ',';
      json << JsonString(granted_directories_[i]);
    }
    json << "],\"environment_names\":[";
    bool first = true;
    for (const auto& [name, value] : spec_.exposed_environment) {
      (void)value;
      if (!first) json << ',';
      first = false;
      json << JsonString(name);
    }
    json << "],\"devices\":[";
    for (std::size_t i = 0; i < spec_.devices.size(); ++i) {
      if (i != 0) json << ',';
      json << JsonString(ToUtf8(spec_.devices[i]));
    }
    json << "],\"tools\":[";
    for (std::size_t i = 0; i < spec_.tools.size(); ++i) {
      if (i != 0) json << ',';
      json << "{\"path\":" << JsonString(spec_.tools[i].path)
           << ",\"capability_sid\":" << JsonString(spec_.tools[i].capability_sid) << '}';
    }
    json << "],\"profile_store\":" << JsonString(profile_store) << ",\"profile_entries\":[";
    for (std::size_t i = 0; i < current.size(); ++i) {
      if (i != 0) json << ',';
      json << JsonString(current[i]);
    }
    json << "],\"profile_changes\":{\"added\":[";
    first = true;
    for (const std::wstring& entry : now) {
      if (before.count(entry) != 0) continue;
      if (!first) json << ',';
      first = false;
      json << JsonString(entry);
    }
    json << "],\"removed\":[";
    first = true;
    for (const std::wstring& entry : before) {
      if (now.count(entry) != 0) continue;
      if (!first) json << ',';
      first = false;
      json << JsonString(entry);
    }
    json << "]},\"system_changes\":[],\"system_writes\":\"denied-by-containment\""
         << ",\"note\":\"AppContainer 没有系统写入通道；系统层快照只记录配置与包存储条目，Rollback 只还原工作区\"}";

    const fs::path target = fs::path(storage_root_) / L"system.json";
    const fs::path temp = fs::path(storage_root_) / L"system.json.tmp";
    {
      std::ofstream output(temp, std::ios::trunc | std::ios::binary);
      if (!output) return;
      output << json.str();
    }
    std::error_code rename_error;
    fs::remove(target, rename_error);
    rename_error.clear();
    fs::rename(temp, target, rename_error);
    std::ofstream log(fs::path(storage_root_) / L"system.log",
                      std::ios::app | std::ios::binary);
    if (log) log << json.str() << '\n';
  }

  core::Status PrepareSessionDisk(const std::wstring& sid, bool write) {
    const std::wstring drive = PickSessionDrive(spec_.overlay_drive);
    if (drive.empty()) {
      return core::Status::Failure(core::Error(
          core::ErrorCode::kBackendFailed, "no free drive letter is available for the workspace",
          false, "checked D: through Z:"));
    }

    storage_root_ = WorkspaceStorageRoot(spec_);
    clone_persistent_ = !spec_.storage_key.empty();
    clone_root_ = (fs::path(storage_root_) / L"workspace").wstring();

    const fs::path marker = fs::path(storage_root_) / L"clone.ready";
    std::error_code error;
    const bool seeded = fs::is_regular_file(marker, error) && !error &&
                        fs::is_directory(clone_root_, error) && !error;
    if (!seeded) {
      std::error_code ignored;
      fs::remove_all(clone_root_, ignored);
      fs::create_directories(clone_root_, ignored);
      // Grant before the first file lands so every copied entry inherits the
      // ACE at creation time; granting afterwards would walk the whole tree.
      if (!DirectoryHasGrant(sid, clone_root_, write)) {
        const core::Status granted = GrantDirectory(sid, clone_root_, write, true);
        if (!granted.ok()) {
          RemoveCloneRoot();
          return granted;
        }
      }
      SeedStats first{};
      const core::Status copied =
          SeedOrSyncWorkspace(spec_.workspace_root, clone_root_, storage_root_, true, &first);
      if (!copied.ok()) {
        RemoveCloneRoot();
        return copied;
      }
      std::ofstream ready(marker, std::ios::trunc | std::ios::binary);
      ready << "seeded " << UtcStamp() << " source=" << ToUtf8(spec_.workspace_root)
            << " git=" << (first.from_git ? 1 : 0) << "\n";
      ready.close();
      core::LogInfo("sandbox", "session disk seeded from " + ToUtf8(spec_.workspace_root) +
                                   " (git=" + (first.from_git ? "1" : "0") +
                                   ", copied=" + std::to_string(first.copied) +
                                   ", unreadable=" + std::to_string(first.unreadable) + ")");
    } else {
      // Reuse the archived disk, but drop a temp tree a crashed session left
      // behind so it never replaces the session temp.
      std::error_code ignored;
      fs::remove_all(fs::path(clone_root_) / L".rvm_temp", ignored);
      if (!DirectoryHasGrant(sid, clone_root_, write)) {
        const core::Status granted = GrantDirectory(sid, clone_root_, write, true);
        if (!granted.ok()) {
          RemoveCloneRoot();
          return granted;
        }
      }
      const fs::path manifest = fs::path(storage_root_) / L"seed.manifest";
      std::error_code manifest_error;
      if (fs::is_regular_file(manifest, manifest_error) && !manifest_error) {
        SeedStats stats{};
        const core::Status synced =
            SeedOrSyncWorkspace(spec_.workspace_root, clone_root_, storage_root_, false, &stats);
        if (!synced.ok()) return synced;
        core::LogInfo("sandbox", "session disk synced with the host workspace (copied=" +
                                     std::to_string(stats.copied) +
                                     ", removed=" + std::to_string(stats.removed) +
                                     ", conflicts=" + std::to_string(stats.conflicts) + ")");
      } else {
        core::LogInfo("sandbox", "session disk restored from the workspace archive");
      }
    }

    const core::Status mapped = MapSessionDrive(drive, clone_root_);
    if (!mapped.ok()) {
      RemoveCloneRoot();
      return mapped;
    }
    clone_drive_ = drive;
    session_workspace_ = drive + L"\\";
    if (clone_persistent_) {
      AppendSessionJournal(storage_root_, "start " + UtcStamp() + " session=" +
                                              ToUtf8(spec_.session_id) + " source=" +
                                              ToUtf8(spec_.workspace_root) +
                                              " drive=" + ToUtf8(drive));
    }
    core::LogInfo("sandbox", "workspace drive ready on " + ToUtf8(drive));
    return core::Status::Ok();
  }

  // Writes must land on the session disk and nowhere near the host tree: the
  // probe checks both sides, so a broken mapping can never pass silently.
  core::Status VerifySessionDiskAccess(bool write) {
    constexpr wchar_t kProbe[] = L".rvm-clone-probe";
    ProcessOptions probe;
    probe.command_line = write ? L"cmd.exe /c \"echo rvm> .rvm-clone-probe\""
                               : L"cmd.exe /c \"dir /b > nul\"";
    probe.working_directory = session_workspace_;
    probe.appcontainer_sid = profile_.sid;
    probe.timeout_ms = 20000;
    probe.memory_limit_bytes = 512ull * 1024 * 1024;
    probe.max_processes = 4;

    auto result = RunProcess(probe);
    if (!result.ok()) {
      return core::Status::Failure(core::Error(
          core::ErrorCode::kBackendFailed, "failed to verify the workspace drive from the sandbox",
          false, result.error().detail));
    }
    if (result.value().timed_out || result.value().exit_code != 0) {
      std::string detail = "exit=" + std::to_string(result.value().exit_code);
      if (!result.value().error.empty()) detail += " " + result.value().error;
      return core::Status::Failure(core::Error(
          core::ErrorCode::kPolicyDenied, "the sandbox cannot access the workspace drive", false,
          detail));
    }
    if (!write) return core::Status::Ok();

    std::error_code error;
    const fs::path on_disk = fs::path(clone_root_) / kProbe;
    const bool landed = fs::exists(on_disk, error) && !error;
    fs::remove(on_disk, error);
    const fs::path in_base = fs::path(spec_.workspace_root) / kProbe;
    error.clear();
    const bool leaked = fs::exists(in_base, error) && !error;
    if (leaked) fs::remove(in_base, error);
    if (leaked) {
      return core::Status::Failure(core::Error(
          core::ErrorCode::kPolicyDenied,
          "the sandbox wrote into the protected workspace instead of the session disk",
          false, "probe file appeared under the host workspace root"));
    }
    if (!landed) {
      return core::Status::Failure(core::Error(
          core::ErrorCode::kBackendFailed,
          "the sandbox write did not reach the session disk", false,
          "drive resolved to an unexpected target"));
    }
    return core::Status::Ok();
  }

  void RemoveSessionDrive() {
    UnmapSessionDrive(clone_drive_, clone_root_);
    if (!clone_drive_.empty()) {
      core::LogInfo("sandbox", "workspace drive released");
    }
    clone_drive_.clear();
    session_workspace_.clear();
  }

  void RemoveCloneRoot() {
    if (session_workspace_.empty() && clone_drive_.empty() && clone_root_.empty()) return;
    RemoveSessionDrive();
    if (!storage_root_.empty() && !clone_root_.empty()) {
      if (clone_persistent_) {
        AppendSessionJournal(storage_root_, "stop " + UtcStamp() + " session=" +
                                                ToUtf8(spec_.session_id));
      } else {
        std::error_code ignored;
        fs::remove_all(storage_root_, ignored);
      }
    }
    clone_root_.clear();
  }

  void RemoveTempRoot() {
    if (temp_root_.empty()) return;
    std::error_code ignored;
    fs::remove_all(temp_root_, ignored);
    temp_root_.clear();
  }

  // Both containments must deny the host profile; anything else is an escape.
  // A restriction that cannot run the probe (loader failure, timeout) is also
  // treated as a failure so a broken setup never silently starts unprotected.
  core::Status VerifyHostIsolation() {
    ProcessOptions probe;
    probe.command_line =
        L"cmd.exe /c \"dir /b \\\"%USERPROFILE%\\\" >nul 2>&1 & if errorlevel 1 "
        L"(echo RVM_PROFILE_DENIED) else (echo RVM_PROFILE_READABLE)\"";
    probe.working_directory =
        session_workspace_.empty() ? spec_.workspace_root : session_workspace_;
    probe.appcontainer_sid = profile_.sid;
    probe.timeout_ms = 20000;
    probe.memory_limit_bytes = 512ull * 1024 * 1024;
    probe.max_processes = 4;

    auto result = RunProcess(probe);
    if (!result.ok()) {
      return core::Status::Failure(core::Error(
          core::ErrorCode::kBackendFailed, "failed to verify host profile isolation", false,
          result.error().detail));
    }
    const bool denied = result.value().output.find("RVM_PROFILE_DENIED") != std::string::npos;
    if (denied && !result.value().timed_out) return core::Status::Ok();

    std::string detail = "exit=" + std::to_string(result.value().exit_code);
    if (result.value().timed_out) detail += " timed_out";
    if (!result.value().output.empty()) detail += " output=" + result.value().output;
    if (!result.value().error.empty()) detail += " error=" + result.value().error;
    return core::Status::Failure(core::Error(
        core::ErrorCode::kPolicyDenied, "the sandbox can still read the host user profile", false,
        detail));
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
  std::wstring storage_root_;
  std::wstring clone_root_;
  std::wstring clone_drive_;
  bool clone_persistent_ = false;
  std::wstring temp_root_;
  std::wstring session_workspace_;
  std::wstring windows_build_;
  std::wstring network_capability_sid_;
  std::vector<std::wstring> granted_devices_;
  bool vbs_enabled_ = false;
  bool hvci_enabled_ = false;
  std::vector<std::wstring> profile_entries_at_start_;
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