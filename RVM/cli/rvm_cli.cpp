#include "cli/rvm_cli.h"

#include <windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <map>
#include <memory>
#include <set>
#include <sstream>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "channel/frame_io.h"
#include "channel/protobuf_codec.h"
#include "rvm/v1/rpc.pb.h"

namespace rvm::cli {
namespace {

namespace fs = std::filesystem;

constexpr std::uint64_t kConnectAttempts = 300;
constexpr std::uint64_t kSeedMaxEntries = 200000;
constexpr std::uint64_t kSeedManifestMaxMillis = 15000;

std::string ToUtf8(const std::wstring& value) {
  if (value.empty()) return {};
  const int size = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                                       nullptr, 0, nullptr, nullptr);
  if (size <= 0) return {};
  std::string out(static_cast<std::size_t>(size), '\0');
  WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), out.data(), size,
                      nullptr, nullptr);
  return out;
}

std::wstring ToWide(const std::string& value) {
  if (value.empty()) return {};
  const int size = MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                                       nullptr, 0);
  if (size <= 0) return {};
  std::wstring out(static_cast<std::size_t>(size), L'\0');
  MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), out.data(), size);
  return out;
}

std::string Narrow(std::wstring_view value) { return ToUtf8(std::wstring(value)); }

void Print(const std::string& text) { std::cout << text; }

void PrintLine(const std::string& text) { std::cout << text << '\n'; }

void PrintError(const std::string& text) { std::cerr << text << '\n'; }

std::string LowerAscii(std::string value) {
  for (char& ch : value) {
    if (ch >= 'A' && ch <= 'Z') ch = static_cast<char>(ch - 'A' + 'a');
  }
  return value;
}

std::wstring LowerWide(std::wstring value) {
  for (wchar_t& ch : value) {
    if (ch >= L'A' && ch <= L'Z') ch = static_cast<wchar_t>(ch - L'A' + L'a');
  }
  return value;
}

std::string Trim(const std::string& value) {
  std::size_t begin = 0;
  while (begin < value.size() && (value[begin] == ' ' || value[begin] == '\t')) ++begin;
  std::size_t end = value.size();
  while (end > begin && (value[end - 1] == ' ' || value[end - 1] == '\t')) --end;
  return value.substr(begin, end - begin);
}

std::wstring ExePath() {
  std::vector<wchar_t> buffer(MAX_PATH);
  for (;;) {
    const DWORD copied = GetModuleFileNameW(nullptr, buffer.data(),
                                            static_cast<DWORD>(buffer.size()));
    if (copied == 0) return {};
    if (copied < buffer.size() - 1) return std::wstring(buffer.data(), copied);
    buffer.resize(buffer.size() * 2);
  }
}

std::wstring EnvironmentPath(const wchar_t* name) {
  const DWORD size = GetEnvironmentVariableW(name, nullptr, 0);
  if (size == 0) return {};
  std::wstring value(size, L'\0');
  const DWORD copied = GetEnvironmentVariableW(name, value.data(), size);
  if (copied == 0 || copied >= size) return {};
  value.resize(copied);
  return value;
}

std::wstring StateRoot() {
  const std::wstring local = EnvironmentPath(L"LOCALAPPDATA");
  const std::wstring base = local.empty() ? fs::temp_directory_path().wstring() : local;
  return (fs::path(base) / L"Responsible" / L"RVM" / L"cli").wstring();
}

std::wstring ArchiveRootFor(const std::string& storage_key) {
  const std::wstring local = EnvironmentPath(L"LOCALAPPDATA");
  const std::wstring base = local.empty() ? fs::temp_directory_path().wstring() : local;
  return (fs::path(base) / L"Responsible" / L"RVM" / ToWide(storage_key)).wstring();
}

std::wstring EngineRecordPath() { return (fs::path(StateRoot()) / L"engine.ini").wstring(); }

std::wstring SessionRecordPath(const std::string& id) {
  return (fs::path(StateRoot()) / L"sessions" / (ToWide(id) + L".ini")).wstring();
}

std::wstring EngineLogPath() { return (fs::path(StateRoot()) / L"engine.log").wstring(); }

std::string NowIso() {
  SYSTEMTIME now{};
  GetSystemTime(&now);
  char buffer[40] = {};
  std::snprintf(buffer, sizeof(buffer), "%04u-%02u-%02uT%02u:%02u:%02uZ", now.wYear, now.wMonth,
                now.wDay, now.wHour, now.wMinute, now.wSecond);
  return buffer;
}

std::string SnapshotStamp() {
  SYSTEMTIME now{};
  GetSystemTime(&now);
  char buffer[40] = {};
  std::snprintf(buffer, sizeof(buffer), "%04u%02u%02u-%02u%02u%02uZ", now.wYear, now.wMonth,
                now.wDay, now.wHour, now.wMinute, now.wSecond);
  return buffer;
}

bool ReadTextFile(const std::wstring& path, std::string* content) {
  std::ifstream input(fs::path(path), std::ios::binary);
  if (!input) return false;
  std::ostringstream buffer;
  buffer << input.rdbuf();
  *content = buffer.str();
  return true;
}

bool WriteTextFileAtomic(const std::wstring& path, const std::string& content) {
  std::error_code error;
  const fs::path target(path);
  fs::create_directories(target.parent_path(), error);
  if (error) return false;
  const fs::path temp = target.wstring() + L".tmp";
  {
    std::ofstream output(temp, std::ios::binary | std::ios::trunc);
    if (!output) return false;
    output.write(content.data(), static_cast<std::streamsize>(content.size()));
    output.flush();
    if (!output) return false;
  }
  if (!MoveFileExW(temp.c_str(), target.c_str(), MOVEFILE_REPLACE_EXISTING)) {
    std::error_code ignored;
    fs::remove(temp, ignored);
    return false;
  }
  return true;
}

bool ReadKeyValueFile(const std::wstring& path, std::map<std::string, std::string>* values) {
  std::string content;
  if (!ReadTextFile(path, &content)) return false;
  std::istringstream lines(content);
  std::string line;
  while (std::getline(lines, line)) {
    if (!line.empty() && line.back() == '\r') line.pop_back();
    if (line.empty() || line[0] == '#') continue;
    const std::size_t split = line.find('=');
    if (split == std::string::npos) continue;
    (*values)[line.substr(0, split)] = line.substr(split + 1);
  }
  return true;
}

bool WriteKeyValueFile(const std::wstring& path,
                       const std::vector<std::pair<std::string, std::string>>& values) {
  std::string content;
  for (const auto& [key, value] : values) {
    content += key;
    content += '=';
    for (const char ch : value) {
      content += ch == '\r' || ch == '\n' ? ' ' : ch;
    }
    content += '\n';
  }
  return WriteTextFileAtomic(path, content);
}

std::string Value(const std::map<std::string, std::string>& values, const char* key,
                  const std::string& fallback = {}) {
  const auto found = values.find(key);
  return found == values.end() ? fallback : found->second;
}

bool BoolValue(const std::map<std::string, std::string>& values, const char* key) {
  const std::string text = Value(values, key);
  return text == "1" || text == "true" || text == "True";
}

std::vector<std::string> SplitList(const std::string& value, char separator) {
  std::vector<std::string> parts;
  std::string current;
  for (const char ch : value) {
    if (ch == separator) {
      if (!current.empty()) parts.push_back(current);
      current.clear();
    } else {
      current.push_back(ch);
    }
  }
  if (!current.empty()) parts.push_back(current);
  return parts;
}

std::string JoinList(const std::vector<std::string>& values, char separator) {
  std::string out;
  for (const std::string& value : values) {
    if (!out.empty()) out.push_back(separator);
    out += value;
  }
  return out;
}

bool ValidId(const std::string& id) {
  if (id.empty() || id.size() > 40) return false;
  for (const char ch : id) {
    const bool allowed = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
                         (ch >= '0' && ch <= '9') || ch == '.' || ch == '_' || ch == '-';
    if (!allowed) return false;
  }
  return true;
}

bool IsAbsoluteDrivePath(const std::wstring& path) {
  if (path.size() < 3) return false;
  const wchar_t letter = path[0];
  const bool alpha = (letter >= L'a' && letter <= L'z') || (letter >= L'A' && letter <= L'Z');
  return alpha && path[1] == L':' && (path[2] == L'\\' || path[2] == L'/');
}

std::wstring NormalizeLocalPath(const std::wstring& path) {
  const DWORD size = GetFullPathNameW(path.c_str(), 0, nullptr, nullptr);
  if (size == 0) return path;
  std::wstring buffer(size, L'\0');
  const DWORD copied = GetFullPathNameW(path.c_str(), size, buffer.data(), nullptr);
  if (copied == 0 || copied >= size) return path;
  buffer.resize(copied);
  while (buffer.size() > 3 && (buffer.back() == L'\\' || buffer.back() == L'/')) buffer.pop_back();
  return buffer;
}

bool PathUnder(const std::wstring& root, const std::wstring& candidate) {
  std::wstring lowered_root = LowerWide(root);
  while (lowered_root.size() > 2 &&
         (lowered_root.back() == L'\\' || lowered_root.back() == L'/')) {
    lowered_root.pop_back();
  }
  const std::wstring lowered_candidate = LowerWide(candidate);
  if (lowered_candidate == lowered_root) return true;
  if (lowered_candidate.size() <= lowered_root.size()) return false;
  if (lowered_candidate.compare(0, lowered_root.size(), lowered_root) != 0) return false;
  const wchar_t boundary = lowered_candidate[lowered_root.size()];
  return boundary == L'\\';
}

std::wstring QuoteArgument(const std::wstring& argument) {
  if (!argument.empty() && argument.find_first_of(L" \t\n\v\"") == std::wstring::npos) {
    return argument;
  }
  std::wstring quoted = L"\"";
  for (std::size_t i = 0;; ++i) {
    std::size_t backslashes = 0;
    while (i < argument.size() && argument[i] == L'\\') {
      ++i;
      ++backslashes;
    }
    if (i == argument.size()) {
      quoted.append(backslashes * 2, L'\\');
      break;
    }
    if (argument[i] == L'"') {
      quoted.append(backslashes * 2 + 1, L'\\');
      quoted.push_back(L'"');
    } else {
      quoted.append(backslashes, L'\\');
      quoted.push_back(argument[i]);
    }
  }
  quoted.push_back(L'"');
  return quoted;
}

std::wstring FindGitExecutable() {
  wchar_t buffer[MAX_PATH] = {};
  const DWORD length =
      SearchPathW(nullptr, L"git.exe", nullptr, MAX_PATH, buffer, nullptr);
  if (length > 0 && length < MAX_PATH) return std::wstring(buffer, length);
  return {};
}

bool RunProcessAndWait(const std::wstring& command_line, const std::wstring& workdir,
                       DWORD* exit_code, std::string* error) {
  SECURITY_ATTRIBUTES attributes{};
  attributes.nLength = sizeof(attributes);
  attributes.bInheritHandle = TRUE;
  const HANDLE nul = CreateFileW(L"NUL", GENERIC_READ | GENERIC_WRITE,
                                 FILE_SHARE_READ | FILE_SHARE_WRITE, &attributes,
                                 OPEN_EXISTING, 0, nullptr);
  if (nul == INVALID_HANDLE_VALUE) {
    if (error != nullptr) *error = "无法打开 NUL 设备";
    return false;
  }
  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  startup.dwFlags = STARTF_USESTDHANDLES;
  startup.hStdInput = nul;
  startup.hStdOutput = nul;
  startup.hStdError = nul;
  PROCESS_INFORMATION process{};
  std::wstring mutable_command = command_line;
  const BOOL created =
      CreateProcessW(nullptr, mutable_command.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW,
                     nullptr, workdir.empty() ? nullptr : workdir.c_str(), &startup, &process);
  if (!created) {
    const DWORD code = GetLastError();
    CloseHandle(nul);
    if (error != nullptr) *error = "CreateProcess 失败：错误 " + std::to_string(code);
    return false;
  }
  CloseHandle(process.hThread);
  WaitForSingleObject(process.hProcess, INFINITE);
  DWORD code = 1;
  GetExitCodeProcess(process.hProcess, &code);
  CloseHandle(process.hProcess);
  CloseHandle(nul);
  if (exit_code != nullptr) *exit_code = code;
  return true;
}

bool RunProcessCapture(const std::wstring& command_line, const std::wstring& workdir,
                       std::string* output, DWORD* exit_code, std::string* error) {
  std::error_code fs_error;
  fs::create_directories(StateRoot(), fs_error);
  const std::wstring temp_path =
      (fs::path(StateRoot()) /
       (L"capture-" + std::to_wstring(GetCurrentProcessId()) + L"-" +
        std::to_wstring(GetTickCount64()) + L".tmp"))
          .wstring();
  SECURITY_ATTRIBUTES attributes{};
  attributes.nLength = sizeof(attributes);
  attributes.bInheritHandle = TRUE;
  const HANDLE nul = CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                 &attributes, OPEN_EXISTING, 0, nullptr);
  const HANDLE output_file =
      CreateFileW(temp_path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                  &attributes, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (nul == INVALID_HANDLE_VALUE || output_file == INVALID_HANDLE_VALUE) {
    if (nul != INVALID_HANDLE_VALUE) CloseHandle(nul);
    if (output_file != INVALID_HANDLE_VALUE) CloseHandle(output_file);
    if (error != nullptr) *error = "无法创建命令输出文件";
    return false;
  }
  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  startup.dwFlags = STARTF_USESTDHANDLES;
  startup.hStdInput = nul;
  startup.hStdOutput = output_file;
  startup.hStdError = nul;
  PROCESS_INFORMATION process{};
  std::wstring mutable_command = command_line;
  const BOOL created =
      CreateProcessW(nullptr, mutable_command.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW,
                     nullptr, workdir.empty() ? nullptr : workdir.c_str(), &startup, &process);
  CloseHandle(output_file);
  CloseHandle(nul);
  if (!created) {
    const DWORD code = GetLastError();
    DeleteFileW(temp_path.c_str());
    if (error != nullptr) *error = "CreateProcess 失败：错误 " + std::to_string(code);
    return false;
  }
  CloseHandle(process.hThread);
  WaitForSingleObject(process.hProcess, INFINITE);
  DWORD code = 1;
  GetExitCodeProcess(process.hProcess, &code);
  CloseHandle(process.hProcess);
  if (output != nullptr) ReadTextFile(temp_path, output);
  DeleteFileW(temp_path.c_str());
  if (exit_code != nullptr) *exit_code = code;
  return true;
}

bool EnsureGitIndex(const std::wstring& workspace, std::string* error) {
  const std::wstring git = FindGitExecutable();
  if (git.empty()) {
    if (error != nullptr) *error = "未找到 git.exe；RVM 需要 Git 索引才能建立会话盘快照";
    return false;
  }
  DWORD code = 1;
  std::string toplevel;
  std::string failure;
  const std::wstring command = QuoteArgument(git) + L" -C " + QuoteArgument(workspace) +
                               L" rev-parse --show-toplevel";
  if (!RunProcessCapture(command, workspace, &toplevel, &code, &failure) || code != 0) {
    if (error != nullptr) {
      *error =
          "工作区没有 Git 索引。请先在工作区执行 `git init` 并建立索引；办公用户可以直接全面索引"
          "（`git add -A`），RVM 只会按索引复制工作区，不会扫描整块磁盘。";
    }
    return false;
  }
  while (!toplevel.empty() && (toplevel.back() == '\r' || toplevel.back() == '\n')) {
    toplevel.pop_back();
  }
  const std::wstring actual =
      NormalizeLocalPath(fs::path(ToWide(Trim(toplevel))).lexically_normal().wstring());
  if (LowerWide(actual) != LowerWide(NormalizeLocalPath(workspace))) {
    if (error != nullptr) {
      *error =
          "工作区索引属于上层仓库：" + Narrow(actual) +
          "。请在工作区根目录单独执行 `git init`（办公用户可直接 `git add -A` 建立全面索引），"
          "否则 RVM 无法按工作区界定会话盘。";
    }
    return false;
  }
  return true;
}

bool InitEmptyGitIndex(const std::wstring& workspace, std::string* error) {
  const std::wstring git = FindGitExecutable();
  if (git.empty()) {
    if (error != nullptr) *error = "未找到 git.exe；空头工作区也需要一个空索引";
    return false;
  }
  std::error_code fs_error;
  fs::create_directories(workspace, fs_error);
  if (fs_error) {
    if (error != nullptr) *error = "无法创建空头工作区目录：" + fs_error.message();
    return false;
  }
  DWORD code = 1;
  std::string failure;
  const std::wstring command =
      QuoteArgument(git) + L" -C " + QuoteArgument(workspace) + L" init -q";
  if (!RunProcessAndWait(command, workspace, &code, &failure) || code != 0) {
    if (error != nullptr) {
      *error = "无法为空头工作区建立 Git 索引" +
               (failure.empty() ? std::string() : "：" + failure);
    }
    return false;
  }
  return true;
}

// The engine's penetration policy is stored in the WPF settings file. The CLI
// reads the same file so a session started from either surface behaves alike.
struct PenetrationPolicy {
  bool network = false;
  bool devices = false;
  std::vector<std::string> device_list;
};

std::wstring PolicyPath() {
  const std::wstring appdata = EnvironmentPath(L"APPDATA");
  const std::wstring base = appdata.empty() ? StateRoot() : appdata;
  return (fs::path(base) / L"Responsible" / L"rvm-policy.json").wstring();
}

std::size_t SkipJsonWhitespace(const std::string& text, std::size_t position) {
  while (position < text.size() &&
         (text[position] == ' ' || text[position] == '\t' || text[position] == '\r' ||
          text[position] == '\n')) {
    ++position;
  }
  return position;
}

bool JsonBoolField(const std::string& text, const std::string& key, bool* value) {
  const std::string token = "\"" + key + "\"";
  const std::size_t found = text.find(token);
  if (found == std::string::npos) return false;
  std::size_t position = SkipJsonWhitespace(text, found + token.size());
  if (position >= text.size() || text[position] != ':') return false;
  position = SkipJsonWhitespace(text, position + 1);
  if (text.compare(position, 4, "true") == 0) {
    *value = true;
    return true;
  }
  if (text.compare(position, 5, "false") == 0) {
    *value = false;
    return true;
  }
  return false;
}

void AppendUtf8(std::string* out, unsigned int code_point) {
  if (code_point <= 0x7F) {
    out->push_back(static_cast<char>(code_point));
  } else if (code_point <= 0x7FF) {
    out->push_back(static_cast<char>(0xC0 | (code_point >> 6)));
    out->push_back(static_cast<char>(0x80 | (code_point & 0x3F)));
  } else {
    out->push_back(static_cast<char>(0xE0 | (code_point >> 12)));
    out->push_back(static_cast<char>(0x80 | ((code_point >> 6) & 0x3F)));
    out->push_back(static_cast<char>(0x80 | (code_point & 0x3F)));
  }
}

std::vector<std::string> JsonStringArrayField(const std::string& text, const std::string& key) {
  std::vector<std::string> values;
  const std::string token = "\"" + key + "\"";
  std::size_t found = text.find(token);
  if (found == std::string::npos) return values;
  std::size_t position = SkipJsonWhitespace(text, found + token.size());
  if (position >= text.size() || text[position] != ':') return values;
  position = SkipJsonWhitespace(text, position + 1);
  if (position >= text.size() || text[position] != '[') return values;
  ++position;
  while (position < text.size()) {
    position = SkipJsonWhitespace(text, position);
    if (position >= text.size() || text[position] == ']') break;
    if (text[position] != '"') break;
    ++position;
    std::string value;
    bool closed = false;
    while (position < text.size()) {
      const char ch = text[position++];
      if (ch == '"') {
        closed = true;
        break;
      }
      if (ch == '\\' && position < text.size()) {
        const char escape = text[position++];
        switch (escape) {
          case '"': value.push_back('"'); break;
          case '\\': value.push_back('\\'); break;
          case '/': value.push_back('/'); break;
          case 'b': value.push_back('\b'); break;
          case 'f': value.push_back('\f'); break;
          case 'n': value.push_back('\n'); break;
          case 'r': value.push_back('\r'); break;
          case 't': value.push_back('\t'); break;
          case 'u': {
            if (position + 4 <= text.size()) {
              const std::string hex = text.substr(position, 4);
              const unsigned int code = static_cast<unsigned int>(std::strtoul(hex.c_str(), nullptr, 16));
              AppendUtf8(&value, code);
              position += 4;
            }
            break;
          }
          default: value.push_back(escape); break;
        }
      } else {
        value.push_back(ch);
      }
    }
    if (!closed) break;
    if (!value.empty()) values.push_back(value);
    position = SkipJsonWhitespace(text, position);
    if (position < text.size() && text[position] == ',') ++position;
  }
  return values;
}

PenetrationPolicy LoadPolicy(std::string* warning) {
  PenetrationPolicy policy;
  const std::wstring path = PolicyPath();
  std::error_code error;
  if (!fs::is_regular_file(path, error) || error) return policy;
  std::string text;
  if (!ReadTextFile(path, &text)) {
    if (warning != nullptr) *warning = "无法读取 RVM 穿透策略，按关闭处理";
    return policy;
  }
  if (!JsonBoolField(text, "networkEnabled", &policy.network) &&
      !JsonBoolField(text, "network_enabled", &policy.network)) {
    if (warning != nullptr) *warning = "RVM 穿透策略缺少 networkEnabled，按关闭处理";
  }
  JsonBoolField(text, "devicesEnabled", &policy.devices);
  JsonBoolField(text, "devices_enabled", &policy.devices);
  if (policy.devices) policy.device_list = JsonStringArrayField(text, "devices");
  return policy;
}

struct SessionRecord {
  std::string id;
  std::string alias;
  std::string host_workspace;
  std::string storage_key;
  std::string archive_root;
  std::string virtual_root;
  std::string created;
  bool synthetic = false;
  bool network_enabled = false;
  std::vector<std::string> devices;
  std::uint64_t engine_session = 0;
};

bool LoadSessionRecord(const std::string& id, SessionRecord* record) {
  std::map<std::string, std::string> values;
  if (!ReadKeyValueFile(SessionRecordPath(id), &values)) return false;
  record->id = Value(values, "id", id);
  record->alias = Value(values, "alias", id);
  record->host_workspace = Value(values, "host_workspace");
  record->storage_key = Value(values, "storage_key", "cli_" + id);
  record->archive_root = Value(values, "archive_root");
  if (record->archive_root.empty()) record->archive_root = Narrow(ArchiveRootFor(record->storage_key));
  record->virtual_root = Value(values, "virtual_root");
  record->created = Value(values, "created");
  record->synthetic = BoolValue(values, "synthetic");
  record->network_enabled = BoolValue(values, "network_enabled");
  record->devices = SplitList(Value(values, "devices"), ';');
  record->engine_session =
      std::strtoull(Value(values, "engine_session", "0").c_str(), nullptr, 10);
  return true;
}

bool SaveSessionRecord(const SessionRecord& record) {
  return WriteKeyValueFile(
      SessionRecordPath(record.id),
      {{"id", record.id},
       {"alias", record.alias},
       {"host_workspace", record.host_workspace},
       {"storage_key", record.storage_key},
       {"archive_root", record.archive_root},
       {"virtual_root", record.virtual_root},
       {"created", record.created},
       {"synthetic", record.synthetic ? "1" : "0"},
       {"network_enabled", record.network_enabled ? "1" : "0"},
       {"devices", JoinList(record.devices, ';')},
       {"engine_session", std::to_string(record.engine_session)}});
}

std::string EngineErrorText(const v1::Error& error) {
  std::string text = "引擎错误 " + std::to_string(static_cast<int>(error.code())) + "：" +
                     error.message();
  if (!error.detail().empty()) text += "（" + error.detail() + "）";
  return text;
}

class RpcClient {
 public:
  RpcClient() = default;
  ~RpcClient() { Close(); }

  RpcClient(const RpcClient&) = delete;
  RpcClient& operator=(const RpcClient&) = delete;

  bool Connect(const std::wstring& pipe_name, std::uint64_t attempts, std::string* error) {
    const std::wstring path = L"\\\\.\\pipe\\" + pipe_name;
    for (std::uint64_t attempt = 0; attempt < attempts; ++attempt) {
      pipe_ = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                          OPEN_EXISTING, 0, nullptr);
      if (pipe_ != INVALID_HANDLE_VALUE) return true;
      const DWORD code = GetLastError();
      if (code == ERROR_PIPE_BUSY) {
        WaitNamedPipeW(path.c_str(), 200);
      } else if (code == ERROR_FILE_NOT_FOUND) {
        Sleep(100);
      } else {
        if (error != nullptr) {
          *error = "无法连接 RVM 引擎管道：错误 " + std::to_string(code);
        }
        return false;
      }
    }
    if (error != nullptr) *error = "等待 RVM 引擎管道超时";
    return false;
  }

  bool Handshake(const std::string& token, std::string* error) {
    v1::RpcRequest request;
    request.set_protocol(v1::PROTOCOL_VERSION_1);
    request.set_request_id(next_id_++);
    v1::HelloRequest* hello = request.mutable_hello();
    hello->set_requested_protocol(v1::PROTOCOL_VERSION_1);
    hello->set_client_name("responsible-rvm-cli");
    hello->set_client_pid(GetCurrentProcessId());
    hello->set_token(token);
    if (!WriteRequest(request, error)) return false;
    v1::RpcResponse response;
    if (!ReadResponse(&response, error)) return false;
    if (response.has_error()) {
      if (error != nullptr) *error = EngineErrorText(response.error());
      return false;
    }
    if (!response.has_hello()) {
      if (error != nullptr) *error = "引擎握手响应缺少 HelloResponse";
      return false;
    }
    if (!response.hello().rvm_available()) {
      if (error != nullptr) {
        const std::string reason = response.hello().unavailable_reason();
        *error = "RVM 不可用：" + (reason.empty() ? std::string("可用性探测未通过") : reason);
      }
      return false;
    }
    return true;
  }

  bool Call(v1::RpcRequest request, v1::RpcResponse* response, std::string* error) {
    request.set_protocol(v1::PROTOCOL_VERSION_1);
    const std::uint64_t id = next_id_++;
    request.set_request_id(id);
    if (!WriteRequest(request, error)) return false;
    if (!ReadResponse(response, error)) return false;
    if (response->request_id() != id) {
      if (error != nullptr) *error = "引擎响应 request id 不匹配";
      return false;
    }
    if (response->has_error()) {
      if (error != nullptr) *error = EngineErrorText(response->error());
      return false;
    }
    return true;
  }

  bool SendRequest(v1::RpcRequest request, std::string* error) {
    request.set_protocol(v1::PROTOCOL_VERSION_1);
    request.set_request_id(next_id_++);
    return WriteRequest(request, error);
  }

  bool ReadStreamFrame(v1::StreamFrame* frame, std::string* error) {
    channel::FrameHeader header;
    std::vector<std::uint8_t> payload;
    const core::Status status = channel::ReadFrame(pipe_, header, payload);
    if (!status.ok()) {
      if (error != nullptr) *error = "引擎连接已断开";
      return false;
    }
    if (header.type == channel::FrameType::kRpcResponse) {
      v1::RpcResponse response;
      if (!channel::ParseMessage(payload, response, channel::kMaxPayloadBytes).ok()) {
        if (error != nullptr) *error = "引擎返回了无法解析的响应";
        return false;
      }
      if (error != nullptr) {
        *error = response.has_error() ? EngineErrorText(response.error())
                                      : "引擎在命令执行期间返回了非预期响应";
      }
      return false;
    }
    if (header.type != channel::FrameType::kStream) {
      if (error != nullptr) *error = "引擎返回了非预期的帧类型";
      return false;
    }
    if (!channel::ParseMessage(payload, *frame, channel::kMaxPayloadBytes).ok()) {
      if (error != nullptr) *error = "引擎返回了无法解析的流帧";
      return false;
    }
    return true;
  }

  void Close() {
    if (pipe_ != INVALID_HANDLE_VALUE) {
      CloseHandle(pipe_);
      pipe_ = INVALID_HANDLE_VALUE;
    }
  }

 private:
  bool WriteRequest(const v1::RpcRequest& request, std::string* error) {
    const core::Result<std::string> payload = channel::SerializeMessage(request);
    if (!payload.ok()) {
      if (error != nullptr) *error = "请求序列化失败";
      return false;
    }
    const std::vector<std::uint8_t> bytes(payload.value().begin(), payload.value().end());
    const core::Status status = channel::WriteFrame(pipe_, channel::FrameType::kRpcRequest,
                                                    request.request_id(), bytes);
    if (!status.ok()) {
      if (error != nullptr) *error = "写入引擎请求失败";
      return false;
    }
    return true;
  }

  bool ReadResponse(v1::RpcResponse* response, std::string* error) {
    channel::FrameHeader header;
    std::vector<std::uint8_t> payload;
    const core::Status status = channel::ReadFrame(pipe_, header, payload);
    if (!status.ok()) {
      if (error != nullptr) *error = "引擎连接已断开";
      return false;
    }
    if (header.type != channel::FrameType::kRpcResponse) {
      if (error != nullptr) *error = "引擎返回了非预期的帧类型";
      return false;
    }
    if (!channel::ParseMessage(payload, *response, channel::kMaxPayloadBytes).ok()) {
      if (error != nullptr) *error = "引擎响应无法解析";
      return false;
    }
    return true;
  }

  HANDLE pipe_ = INVALID_HANDLE_VALUE;
  std::uint64_t next_id_ = 1;
};

class MutexGuard {
 public:
  MutexGuard() {
    handle_ = CreateMutexW(nullptr, FALSE, L"Local\\ResponsibleRvmCliDaemon");
    if (handle_ != nullptr) WaitForSingleObject(handle_, 30000);
  }
  ~MutexGuard() {
    if (handle_ != nullptr) {
      ReleaseMutex(handle_);
      CloseHandle(handle_);
    }
  }
  MutexGuard(const MutexGuard&) = delete;
  MutexGuard& operator=(const MutexGuard&) = delete;

 private:
  HANDLE handle_ = nullptr;
};

std::string RandomToken() {
  unsigned char bytes[32] = {};
  const NTSTATUS status = BCryptGenRandom(nullptr, bytes, sizeof(bytes),
                                          BCRYPT_USE_SYSTEM_PREFERRED_RNG);
  if (status < 0) return {};
  static const char* digits = "0123456789ABCDEF";
  std::string token;
  token.reserve(sizeof(bytes) * 2);
  for (const unsigned char byte : bytes) {
    token.push_back(digits[byte >> 4]);
    token.push_back(digits[byte & 0x0F]);
  }
  return token;
}

std::string TailOfFile(const std::wstring& path, std::size_t limit) {
  std::string content;
  if (!ReadTextFile(path, &content)) return {};
  if (content.size() <= limit) return content;
  return content.substr(content.size() - limit);
}

bool StartDaemon(const std::wstring& pipe_name, const std::string& token, DWORD* daemon_pid,
                 std::string* error) {
  const std::wstring exe = ExePath();
  if (exe.empty()) {
    if (error != nullptr) *error = "无法确定 RvmEngine.exe 路径";
    return false;
  }
  std::error_code fs_error;
  fs::create_directories(StateRoot(), fs_error);
  const std::wstring log_path = EngineLogPath();

  SECURITY_ATTRIBUTES inherit{};
  inherit.nLength = sizeof(inherit);
  inherit.bInheritHandle = TRUE;
  const HANDLE log_handle =
      CreateFileW(log_path.c_str(), FILE_APPEND_DATA,
                  FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, &inherit, OPEN_ALWAYS,
                  FILE_ATTRIBUTE_NORMAL, nullptr);
  if (log_handle == INVALID_HANDLE_VALUE) {
    if (error != nullptr) *error = "无法打开 RVM 引擎日志";
    return false;
  }

  HANDLE token_read = nullptr;
  HANDLE token_write = nullptr;
  if (!CreatePipe(&token_read, &token_write, &inherit, 0)) {
    CloseHandle(log_handle);
    if (error != nullptr) *error = "无法创建引擎令牌管道";
    return false;
  }
  SetHandleInformation(token_write, HANDLE_FLAG_INHERIT, 0);

  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  startup.dwFlags = STARTF_USESTDHANDLES;
  startup.hStdInput = token_read;
  startup.hStdOutput = log_handle;
  startup.hStdError = log_handle;

  std::wstring command = QuoteArgument(exe) + L" --pipe " + QuoteArgument(pipe_name) +
                         L" --token-stdin --log-level info --daemon";
  PROCESS_INFORMATION process{};
  const BOOL created = CreateProcessW(nullptr, command.data(), nullptr, nullptr, TRUE,
                                      CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP, nullptr,
                                      nullptr, &startup, &process);
  CloseHandle(token_read);
  CloseHandle(log_handle);
  if (!created) {
    const DWORD code = GetLastError();
    CloseHandle(token_write);
    if (error != nullptr) *error = "启动 RVM 引擎失败：错误 " + std::to_string(code);
    return false;
  }
  CloseHandle(process.hThread);
  CloseHandle(process.hProcess);
  if (daemon_pid != nullptr) *daemon_pid = process.dwProcessId;

  const std::string line = token + "\n";
  DWORD written = 0;
  const BOOL sent = WriteFile(token_write, line.data(), static_cast<DWORD>(line.size()), &written,
                              nullptr);
  CloseHandle(token_write);
  if (!sent || written != line.size()) {
    if (error != nullptr) *error = "无法向 RVM 引擎发送令牌";
    return false;
  }
  return true;
}

bool OpenExistingClient(std::unique_ptr<RpcClient>* client, std::string* error) {
  (void)error;
  std::map<std::string, std::string> values;
  if (!ReadKeyValueFile(EngineRecordPath(), &values)) return false;
  const std::string pipe = Value(values, "pipe");
  const std::string token = Value(values, "token");
  if (pipe.empty() || token.empty()) return false;
  auto candidate = std::make_unique<RpcClient>();
  std::string failure;
  if (!candidate->Connect(ToWide(pipe), 3, &failure)) return false;
  if (!candidate->Handshake(token, &failure)) return false;
  *client = std::move(candidate);
  return true;
}

bool EnsureClient(std::unique_ptr<RpcClient>* client, std::string* error) {
  MutexGuard guard;
  if (OpenExistingClient(client, error)) return true;

  const std::string token = RandomToken();
  if (token.empty()) {
    if (error != nullptr) *error = "无法生成引擎令牌";
    return false;
  }
  char suffix[48] = {};
  std::snprintf(suffix, sizeof(suffix), "responsible-rvm-cli-%lu-%llx",
                static_cast<unsigned long>(GetCurrentProcessId()),
                static_cast<unsigned long long>(GetTickCount64()));
  const std::wstring pipe_name = ToWide(suffix);
  std::string failure;
  DWORD daemon_pid = 0;
  if (!StartDaemon(pipe_name, token, &daemon_pid, &failure)) {
    if (error != nullptr) *error = failure;
    return false;
  }

  auto candidate = std::make_unique<RpcClient>();
  if (!candidate->Connect(pipe_name, kConnectAttempts, &failure) ||
      !candidate->Handshake(token, &failure)) {
    const std::string tail = TailOfFile(EngineLogPath(), 800);
    if (error != nullptr) {
      *error = failure + (tail.empty() ? std::string() : "\n引擎日志：\n" + tail);
    }
    return false;
  }
  if (!WriteKeyValueFile(EngineRecordPath(),
                         {{"pipe", Narrow(pipe_name)},
                          {"token", token},
                          {"pid", std::to_string(daemon_pid)}})) {
    if (error != nullptr) *error = "无法写入 RVM 引擎状态档案";
    return false;
  }
  *client = std::move(candidate);
  return true;
}

bool CreateEngineSession(RpcClient& client, SessionRecord* record, std::string* error) {
  v1::RpcRequest request;
  v1::CreateSessionRequest* body = request.mutable_create_session();
  body->set_client_id("responsible-rvm-cli");
  body->set_workspace_root(record->host_workspace);
  v1::SandboxPolicy* policy = body->mutable_policy();
  policy->set_mode(v1::SANDBOX_MODE_WORKSPACE_WRITE);
  policy->set_approval(v1::APPROVAL_MODE_ASK);
  policy->set_network_access(record->network_enabled);
  body->set_network_access(record->network_enabled);
  body->set_overlay_workspace(true);
  body->set_storage_key(record->storage_key);
  for (const std::string& device : record->devices) body->add_device_paths(device);

  v1::RpcResponse response;
  if (!client.Call(std::move(request), &response, error)) return false;
  if (!response.has_create_session() || !response.create_session().has_session()) {
    if (error != nullptr) *error = "引擎返回了空的会话记录";
    return false;
  }
  const v1::Session& session = response.create_session().session();
  if (session.session_id() == 0 || session.virtual_workspace_root().empty()) {
    if (error != nullptr) *error = "引擎会话缺少 id 或工作区路径";
    return false;
  }
  record->engine_session = session.session_id();
  record->virtual_root = session.virtual_workspace_root();
  if (!SaveSessionRecord(*record)) {
    if (error != nullptr) *error = "无法写入会话档案";
    return false;
  }
  return true;
}

bool EnsureLiveSession(RpcClient& client, SessionRecord* record, bool start_if_missing,
                       std::string* error) {
  if (record->engine_session != 0) {
    v1::RpcRequest request;
    request.mutable_get_session()->set_session_id(record->engine_session);
    v1::RpcResponse response;
    std::string failure;
    if (client.Call(std::move(request), &response, &failure) &&
        response.has_get_session() && response.get_session().has_session()) {
      const v1::Session& session = response.get_session().session();
      if (session.state() == v1::SESSION_STATE_READY &&
          !session.virtual_workspace_root().empty()) {
        const std::string virtual_root = session.virtual_workspace_root();
        if (virtual_root != record->virtual_root) {
          record->virtual_root = virtual_root;
          SaveSessionRecord(*record);
        }
        return true;
      }
    }
    record->engine_session = 0;
    SaveSessionRecord(*record);
  }
  if (!start_if_missing) {
    if (error != nullptr) *error = "会话未在引擎中运行";
    return false;
  }
  return CreateEngineSession(client, record, error);
}

std::string ChangeKindName(v1::ChangeKind kind) {
  switch (kind) {
    case v1::CHANGE_KIND_ADDED: return "ADDED";
    case v1::CHANGE_KIND_MODIFIED: return "MODIFIED";
    case v1::CHANGE_KIND_DELETED: return "DELETED";
    case v1::CHANGE_KIND_RENAMED: return "RENAMED";
    default: return "UNSPECIFIED";
  }
}

void PrintDiff(const v1::DiffResponse& diff) {
  if (diff.changes().empty()) PrintLine("没有工作区变更");
  for (const v1::FileChange& change : diff.changes()) {
    PrintLine(ChangeKindName(change.kind()) + "\t" + change.path() + "\t+ " +
              std::to_string(change.added_bytes()) + "\t- " +
              std::to_string(change.removed_bytes()));
  }
  for (const v1::SyncConflict& conflict : diff.conflicts()) {
    PrintLine("CONFLICT\t" + conflict.path() + "\t" + conflict.reason());
  }
  if (diff.truncated()) PrintLine("注意：变更列表达到扫描预算，未列全");
}

bool FetchDiff(RpcClient& client, std::uint64_t session_id, v1::DiffResponse* diff,
               std::string* error) {
  v1::RpcRequest request;
  request.mutable_workspace_diff()->set_session_id(session_id);
  v1::RpcResponse response;
  if (!client.Call(std::move(request), &response, error)) return false;
  if (!response.has_workspace_diff()) {
    if (error != nullptr) *error = "引擎返回了空的变更列表";
    return false;
  }
  *diff = response.workspace_diff();
  return true;
}

bool ApplyAll(RpcClient& client, std::uint64_t session_id, v1::ApplyResponse* applied,
              std::string* error) {
  v1::RpcRequest request;
  v1::ApplyRequest* body = request.mutable_workspace_apply();
  body->set_session_id(session_id);
  body->set_all(true);
  v1::RpcResponse response;
  if (!client.Call(std::move(request), &response, error)) return false;
  if (!response.has_workspace_apply()) {
    if (error != nullptr) *error = "引擎返回了空的同步结果";
    return false;
  }
  *applied = response.workspace_apply();
  return true;
}

void WriteStdStream(HANDLE handle, const std::string& data) {
  std::size_t offset = 0;
  while (offset < data.size()) {
    const DWORD chunk =
        static_cast<DWORD>(std::min<std::size_t>(data.size() - offset, 64 * 1024));
    DWORD written = 0;
    if (!WriteFile(handle, data.data() + offset, chunk, &written, nullptr) || written == 0) return;
    offset += written;
  }
}

// Runs one shell string through cmd inside the session. The codepage is forced
// to UTF-8 so the CLI can print the captured bytes unchanged.
bool ExecShellLine(RpcClient& client, std::uint64_t session_id, const std::wstring& workdir,
                   const std::wstring& line, int* exit_code, bool* timed_out,
                   std::string* error) {
  v1::RpcRequest request;
  v1::ShellExecRequest* body = request.mutable_shell_exec();
  body->set_session_id(session_id);
  body->set_workdir(Narrow(workdir));
  body->set_timeout_ms(0);
  body->add_argv("cmd.exe");
  body->add_argv("/d");
  body->add_argv("/s");
  body->add_argv("/c");
  body->add_argv(Narrow(L"chcp 65001>nul & " + line));
  if (!client.SendRequest(std::move(request), error)) return false;

  for (;;) {
    v1::StreamFrame frame;
    if (!client.ReadStreamFrame(&frame, error)) return false;
    if (frame.has_output()) {
      const v1::ShellOutputChunk& chunk = frame.output();
      WriteStdStream(chunk.stream() == v1::SHELL_STREAM_STDERR ? GetStdHandle(STD_ERROR_HANDLE)
                                                               : GetStdHandle(STD_OUTPUT_HANDLE),
                     chunk.data());
    } else if (frame.has_exited()) {
      const v1::ShellExited& exited = frame.exited();
      *exit_code = exited.has_exit_code() ? static_cast<int>(exited.exit_code()) : 1;
      *timed_out = exited.timed_out();
      if (!exited.error().empty() && !exited.timed_out()) {
        if (error != nullptr) *error = exited.error();
      }
      return true;
    }
  }
}

// A `cd` candidate must be inside the session drive. The probe reuses the
// engine's own workdir validation, so a nonexistent directory is rejected by
// the sandbox rather than guessed at by the CLI.
bool ProbeWorkdir(RpcClient& client, std::uint64_t session_id, const std::wstring& workdir) {
  v1::RpcRequest request;
  v1::ShellExecRequest* body = request.mutable_shell_exec();
  body->set_session_id(session_id);
  body->set_workdir(Narrow(workdir));
  body->set_timeout_ms(30000);
  body->add_argv("cmd.exe");
  body->add_argv("/d");
  body->add_argv("/s");
  body->add_argv("/c");
  body->add_argv("exit 0");
  std::string error;
  if (!client.SendRequest(std::move(request), &error)) return false;
  for (;;) {
    v1::StreamFrame frame;
    if (!client.ReadStreamFrame(&frame, &error)) return false;
    if (frame.has_exited()) {
      return frame.exited().has_exit_code() && frame.exited().exit_code() == 0;
    }
  }
}

std::wstring TrimQuotes(const std::wstring& value) {
  if (value.size() >= 2 && value.front() == L'"' && value.back() == L'"') {
    return value.substr(1, value.size() - 2);
  }
  return value;
}

std::wstring ResolveJoinPath(const std::wstring& current, const std::wstring& argument) {
  std::wstring base;
  if (IsAbsoluteDrivePath(argument)) {
    base = argument;
  } else {
    base = current;
    if (!base.empty() && base.back() != L'\\') base.push_back(L'\\');
    base += argument;
  }
  std::wstring normalized = fs::path(base).lexically_normal().wstring();
  while (normalized.size() > 3 && (normalized.back() == L'\\' || normalized.back() == L'/')) {
    normalized.pop_back();
  }
  return normalized;
}

int ExitCodeForConflict(bool conflicts) { return conflicts ? 2 : 0; }

void PrintSessionHeader(const SessionRecord& record) {
  PrintLine("alias=" + record.alias);
  PrintLine("workspace=" + record.virtual_root);
  PrintLine("archive=" + record.archive_root);
  PrintLine(std::string("network=") + (record.network_enabled ? "outbound-only" : "masked"));
  PrintLine("port_mask=host-loopback,inbound");
  PrintLine("devices=" + (record.devices.empty() ? std::string("masked")
                                                 : JoinList(record.devices, ',')));
}

bool CopyIfPresent(const fs::path& source, const fs::path& target, std::vector<std::string>* copied,
                   std::vector<std::string>* warnings) {
  std::error_code error;
  if (!fs::is_regular_file(source, error) || error) return false;
  if (!CopyFileW(source.c_str(), target.c_str(), FALSE)) {
    if (warnings != nullptr) {
      warnings->push_back("无法复制 " + source.filename().string() + "：错误 " +
                          std::to_string(GetLastError()));
    }
    return false;
  }
  if (copied != nullptr) copied->push_back(ToUtf8(source.filename().wstring()));
  return true;
}

void WriteWorkspaceManifest(const fs::path& workspace, const fs::path& target,
                            std::size_t* entries, bool* truncated) {
  *entries = 0;
  *truncated = false;
  std::ostringstream output;
  std::error_code error;
  if (!fs::is_directory(workspace, error) || error) {
    *truncated = true;
    WriteTextFileAtomic(target.wstring(), output.str());
    return;
  }
  const ULONGLONG started = GetTickCount64();
  fs::recursive_directory_iterator iterator(workspace, fs::directory_options::skip_permission_denied,
                                            error);
  const fs::recursive_directory_iterator end;
  while (!error && iterator != end) {
    if (GetTickCount64() - started > kSeedManifestMaxMillis || *entries >= kSeedMaxEntries) {
      *truncated = true;
      break;
    }
    const fs::directory_entry entry = *iterator;
    std::error_code item_error;
    if (entry.is_directory(item_error) && !item_error) {
      const std::wstring name = entry.path().filename().wstring();
      if (name == L".git" || name == L".rvm_temp") iterator.disable_recursion_pending();
      iterator.increment(error);
      continue;
    }
    if (!entry.is_regular_file(item_error) || item_error) {
      iterator.increment(error);
      continue;
    }
    const fs::path relative = fs::relative(entry.path(), workspace, item_error);
    if (item_error || relative.empty() || relative.native().rfind(L"..", 0) == 0) {
      iterator.increment(error);
      continue;
    }
    const std::uintmax_t size = entry.file_size(item_error);
    const fs::file_time_type stamp = entry.last_write_time(item_error);
    const auto stamp_count = static_cast<long long>(stamp.time_since_epoch().count());
    output << ToUtf8(relative.generic_wstring()) << '\t' << (item_error ? 0 : size) << '\t'
           << stamp_count << '\n';
    ++*entries;
    iterator.increment(error);
  }
  WriteTextFileAtomic(target.wstring(), output.str());
}

int CreateSessionCommand(int argc, wchar_t** argv) {
  if (argc < 4) {
    PrintError("用法：RvmEngine --create-session <alias> <id> [<absolute_path>]");
    return 64;
  }
  const std::wstring alias = argv[2];
  const std::string id = Narrow(argv[3]);
  if (Trim(Narrow(alias)).empty()) {
    PrintError("别名不能为空");
    return 64;
  }
  if (!ValidId(id)) {
    PrintError("id 只能包含字母、数字、点、下划线和连字符，且不超过 40 个字符");
    return 64;
  }
  if (argc > 5) {
    PrintError("用法：RvmEngine --create-session <alias> <id> [<absolute_path>]");
    return 64;
  }

  SessionRecord record;
  record.id = id;
  record.alias = ToUtf8(alias);
  record.created = NowIso();
  record.storage_key = "cli_" + id;
  record.archive_root = Narrow(ArchiveRootFor(record.storage_key));

  std::string error;
  std::map<std::string, std::string> existing;
  if (ReadKeyValueFile(SessionRecordPath(id), &existing)) {
    PrintError("会话 id 已存在：" + id + "；如需重建请先 --terminate " + id);
    return 1;
  }

  bool synthetic = argc < 5;
  std::wstring host_workspace;
  if (synthetic) {
    host_workspace = (fs::path(StateRoot()) / L"workspaces" / ToWide(id)).wstring();
    if (!InitEmptyGitIndex(host_workspace, &error)) {
      PrintError(error);
      return 1;
    }
    record.synthetic = true;
  } else {
    const std::wstring raw_path = argv[4];
    if (!IsAbsoluteDrivePath(raw_path)) {
      PrintError("工作区必须是绝对的本地路径（例如 D:\\work\\project）");
      return 64;
    }
    host_workspace = NormalizeLocalPath(raw_path);
    std::error_code fs_error;
    if (!fs::is_directory(host_workspace, fs_error) || fs_error) {
      PrintError("工作区目录不存在：" + Narrow(host_workspace));
      return 1;
    }
    if (!EnsureGitIndex(host_workspace, &error)) {
      PrintError(error);
      return 1;
    }
    record.synthetic = false;
  }
  record.host_workspace = Narrow(host_workspace);

  std::string policy_warning;
  const PenetrationPolicy policy = LoadPolicy(&policy_warning);
  if (!policy_warning.empty()) PrintError("警告：" + policy_warning);
  record.network_enabled = policy.network;
  if (policy.devices) record.devices = policy.device_list;

  std::unique_ptr<RpcClient> client;
  if (!EnsureClient(&client, &error)) {
    if (synthetic) {
      std::error_code ignored;
      fs::remove_all(host_workspace, ignored);
    }
    PrintError("RVM 引擎不可用：" + error);
    return 1;
  }
  if (!CreateEngineSession(*client, &record, &error)) {
    PrintError("创建会话失败：" + error);
    return 1;
  }

  PrintLine("session_created=true");
  PrintLine("id=" + record.id);
  PrintLine("alias=" + record.alias);
  PrintLine("engine_session=" + std::to_string(record.engine_session));
  PrintSessionHeader(record);
  PrintLine(std::string("synthetic=") + (record.synthetic ? "true" : "false"));
  return 0;
}

int JoinCommand(int argc, wchar_t** argv) {
  if (argc != 3) {
    PrintError("用法：RvmEngine --join <id>");
    return 64;
  }
  const std::string id = Narrow(argv[2]);
  SessionRecord record;
  if (!ValidId(id) || !LoadSessionRecord(id, &record)) {
    PrintError("未知会话 id：" + id + "；请先 --create-session");
    return 1;
  }
  std::string error;
  std::unique_ptr<RpcClient> client;
  if (!EnsureClient(&client, &error)) {
    PrintError("RVM 引擎不可用：" + error);
    return 1;
  }
  if (!EnsureLiveSession(*client, &record, true, &error)) {
    PrintError("无法进入会话：" + error);
    return 1;
  }

  std::wstring current = ToWide(record.virtual_root);
  const std::wstring root = current;
  PrintSessionHeader(record);
  PrintLine("输入 exit 结束连接。");

  std::string line;
  for (;;) {
    Print("[" + record.alias + "@" + record.id + " " + Narrow(current) + "]> ");
    std::cout.flush();
    if (!std::getline(std::cin, line)) {
      PrintLine("");
      break;
    }
    while (!line.empty() && (line.back() == '\r' || line.back() == '\n')) line.pop_back();
    const std::string trimmed = Trim(line);
    if (trimmed.empty()) continue;
    const std::string lowered = LowerAscii(trimmed);
    if (lowered == "exit" || lowered == "quit") break;
    if (lowered == "help") {
      PrintLine("exit/quit  断开连接");
      PrintLine("pwd        显示会话盘上的当前目录");
      PrintLine("cd <路径>  切换目录（限制在会话盘内）");
      PrintLine("其他输入通过 cmd.exe 在会话内执行");
      continue;
    }
    if (lowered == "pwd") {
      PrintLine(Narrow(current));
      continue;
    }
    if (lowered == "cd" || lowered.rfind("cd ", 0) == 0) {
      const std::wstring argument =
          TrimQuotes(ToWide(trimmed.size() > 3 ? Trim(trimmed.substr(2)) : std::string()));
      const std::wstring candidate =
          argument.empty() ? root : ResolveJoinPath(current, argument);
      if (!PathUnder(root, candidate)) {
        PrintError("路径不在会话盘内：" + Narrow(candidate));
        continue;
      }
      if (ProbeWorkdir(*client, record.engine_session, candidate)) {
        current = candidate;
      } else {
        PrintError("目录不存在或不可进入：" + Narrow(candidate));
      }
      continue;
    }

    int exit_code = 1;
    bool timed_out = false;
    if (!ExecShellLine(*client, record.engine_session, current, ToWide(trimmed), &exit_code,
                       &timed_out, &error)) {
      PrintError("命令执行失败：" + error);
      return 1;
    }
    if (timed_out) PrintError("[命令超时]");
    if (exit_code != 0) PrintError("[exit " + std::to_string(exit_code) + "]");
  }
  return 0;
}

int TerminateCommand(int argc, wchar_t** argv) {
  if (argc != 3) {
    PrintError("用法：RvmEngine --terminate <id>");
    return 64;
  }
  const std::string id = Narrow(argv[2]);
  SessionRecord record;
  if (!ValidId(id) || !LoadSessionRecord(id, &record)) {
    PrintError("未知会话 id：" + id);
    return 1;
  }

  std::unique_ptr<RpcClient> client;
  std::string ignored_error;
  if (OpenExistingClient(&client, &ignored_error) && record.engine_session != 0) {
    v1::RpcRequest request;
    v1::CloseSessionRequest* body = request.mutable_close_session();
    body->set_session_id(record.engine_session);
    body->set_keep_diff(true);
    body->set_reason("cli terminate");
    v1::RpcResponse response;
    std::string close_error;
    if (!client->Call(std::move(request), &response, &close_error)) {
      // Deleting a live session disk while its drive is still mapped would
      // leave the sandbox writing into a removed directory.
      const bool already_closed = close_error.find("引擎错误 4") != std::string::npos ||
                                  close_error.find("引擎错误 18") != std::string::npos;
      if (!already_closed) {
        PrintError("无法关闭引擎会话，未删除档案：" + close_error);
        return 1;
      }
    }
  }

  std::error_code error;
  if (!record.archive_root.empty()) {
    fs::remove_all(fs::path(ToWide(record.archive_root)), error);
    if (error) {
      PrintError("无法删除会话档案：" + error.message() + "；档案：" + record.archive_root);
      return 1;
    }
  }
  if (record.synthetic && !record.host_workspace.empty()) {
    error.clear();
    fs::remove_all(fs::path(ToWide(record.host_workspace)), error);
    if (error) PrintError("警告：无法删除空头工作区目录：" + error.message());
  }
  if (!DeleteFileW(SessionRecordPath(id).c_str())) {
    PrintError("无法删除会话记录：错误 " + std::to_string(GetLastError()));
    return 1;
  }
  PrintLine("terminated=" + id);
  return 0;
}

int FlushbackCommand(int argc, wchar_t** argv) {
  if (argc != 3) {
    PrintError("用法：RvmEngine --flushback <id>");
    return 64;
  }
  const std::string id = Narrow(argv[2]);
  SessionRecord record;
  if (!ValidId(id) || !LoadSessionRecord(id, &record)) {
    PrintError("未知会话 id：" + id);
    return 1;
  }
  if (record.synthetic) {
    PrintError("空头工作区没有宿主角落，无法 flushback；请使用 --save 保存会话状态");
    return 1;
  }

  std::string error;
  std::unique_ptr<RpcClient> client;
  if (!EnsureClient(&client, &error)) {
    PrintError("RVM 引擎不可用：" + error);
    return 1;
  }
  if (!EnsureLiveSession(*client, &record, true, &error)) {
    PrintError("无法打开会话：" + error);
    return 1;
  }

  v1::DiffResponse diff;
  if (!FetchDiff(*client, record.engine_session, &diff, &error)) {
    PrintError("读取工作区变更失败：" + error);
    return 1;
  }
  PrintLine("== 审计 ==");
  PrintDiff(diff);

  v1::ApplyResponse applied;
  if (!ApplyAll(*client, record.engine_session, &applied, &error)) {
    PrintError("同步工作区失败：" + error);
    return 1;
  }
  PrintLine("== 同步 ==");
  PrintLine("applied=" + std::to_string(applied.applied_size()));
  for (const v1::FileChange& change : applied.applied()) {
    PrintLine(ChangeKindName(change.kind()) + "\t" + change.path());
  }
  for (const v1::SyncConflict& conflict : applied.conflicts()) {
    PrintLine("CONFLICT\t" + conflict.path() + "\t" + conflict.reason());
  }
  return ExitCodeForConflict(applied.conflicts_size() > 0);
}

int SaveCommand(int argc, wchar_t** argv) {
  if (argc != 3) {
    PrintError("用法：RvmEngine --save <id>");
    return 64;
  }
  const std::string id = Narrow(argv[2]);
  SessionRecord record;
  if (!ValidId(id) || !LoadSessionRecord(id, &record)) {
    PrintError("未知会话 id：" + id);
    return 1;
  }
  const fs::path archive(ToWide(record.archive_root));
  std::error_code error;
  if (!fs::is_directory(archive, error) || error) {
    PrintError("会话档案不存在：" + record.archive_root);
    return 1;
  }

  fs::path snapshot = archive / L"snapshots" / ToWide(SnapshotStamp());
  if (fs::exists(snapshot, error)) snapshot += ToWide("-" + std::to_string(GetTickCount64()));
  fs::create_directories(snapshot, error);
  if (error) {
    PrintError("无法创建快照目录：" + error.message());
    return 1;
  }

  std::vector<std::string> copied;
  std::vector<std::string> warnings;
  static const wchar_t* const kArtifacts[] = {L"system.json", L"system.log", L"sessions.log",
                                              L"audit.log",   L"network.log", L"seed.manifest",
                                              L"baseline.manifest", L"clone.ready"};
  for (const wchar_t* name : kArtifacts) {
    CopyIfPresent(archive / name, snapshot / name, &copied, &warnings);
  }
  for (const std::string& warning : warnings) PrintError("警告：" + warning);

  std::size_t workspace_entries = 0;
  bool workspace_truncated = false;
  WriteWorkspaceManifest(archive / L"workspace", snapshot / L"workspace.manifest",
                         &workspace_entries, &workspace_truncated);

  bool session_running = false;
  std::unique_ptr<RpcClient> client;
  std::string ignored_error;
  if (OpenExistingClient(&client, &ignored_error) && record.engine_session != 0) {
    if (EnsureLiveSession(*client, &record, false, &ignored_error)) {
      session_running = true;
      v1::DiffResponse diff;
      std::string diff_error;
      if (FetchDiff(*client, record.engine_session, &diff, &diff_error)) {
        std::ostringstream text;
        text << "# RVM workspace diff at snapshot time\n";
        for (const v1::FileChange& change : diff.changes()) {
          text << ChangeKindName(change.kind()) << '\t' << change.path() << '\t'
               << change.added_bytes() << '\t' << change.removed_bytes() << '\n';
        }
        for (const v1::SyncConflict& conflict : diff.conflicts()) {
          text << "CONFLICT\t" << conflict.path() << '\t' << conflict.reason() << '\n';
        }
        if (diff.truncated()) text << "# truncated\n";
        WriteTextFileAtomic((snapshot / L"workspace.diff").wstring(), text.str());
      }
    }
  }

  std::vector<std::pair<std::string, std::string>> metadata = {
      {"created", NowIso()},
      {"id", record.id},
      {"alias", record.alias},
      {"engine_session", std::to_string(record.engine_session)},
      {"host_workspace", record.host_workspace},
      {"workspace", record.virtual_root},
      {"archive", record.archive_root},
      {"synthetic", record.synthetic ? "1" : "0"},
      {"network_enabled", record.network_enabled ? "1" : "0"},
      {"devices", JoinList(record.devices, ';')},
      {"session_running", session_running ? "1" : "0"},
      {"workspace_entries", std::to_string(workspace_entries)},
      {"workspace_truncated", workspace_truncated ? "1" : "0"},
      {"artifacts", JoinList(copied, ',')},
  };
  if (!WriteKeyValueFile(snapshot / L"snapshot.ini", metadata)) {
    PrintError("无法写入快照元数据");
    return 1;
  }

  PrintLine("snapshot=" + ToUtf8(snapshot.wstring()));
  PrintLine("artifacts=" + std::to_string(copied.size()));
  PrintLine("workspace_entries=" + std::to_string(workspace_entries) +
            (workspace_truncated ? " (truncated)" : ""));
  PrintLine(std::string("session_running=") + (session_running ? "true" : "false"));
  if (!session_running) {
    PrintLine("注意：引擎会话当前未运行；快照取自持久会话档案（系统状态与工作区索引）。");
  }
  return 0;
}

void PrintUsage() {
  PrintError("用法：");
  PrintError("  RvmEngine --create-session <alias> <id> [<absolute_path>]");
  PrintError("  RvmEngine --join <id>");
  PrintError("  RvmEngine --terminate <id>");
  PrintError("  RvmEngine --flushback <id>");
  PrintError("  RvmEngine --save <id>");
}

}  // namespace

bool IsCommand(const wchar_t* argument) {
  if (argument == nullptr) return false;
  const std::wstring_view value(argument);
  return value == L"--create-session" || value == L"--join" || value == L"--terminate" ||
         value == L"--flushback" || value == L"--save";
}

int Run(int argc, wchar_t** argv) {
  SetConsoleOutputCP(CP_UTF8);
  SetConsoleCP(CP_UTF8);
  if (argc < 2) {
    PrintUsage();
    return 64;
  }
  const std::wstring_view command(argv[1]);
  if (command == L"--help" || command == L"-h") {
    PrintUsage();
    return 0;
  }
  if (command == L"--create-session") return CreateSessionCommand(argc, argv);
  if (command == L"--join") return JoinCommand(argc, argv);
  if (command == L"--terminate") return TerminateCommand(argc, argv);
  if (command == L"--flushback") return FlushbackCommand(argc, argv);
  if (command == L"--save") return SaveCommand(argc, argv);
  PrintUsage();
  return 64;
}

}  // namespace rvm::cli