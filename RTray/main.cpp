// RTray：宿主的看护进程。
//
// 目的只有两个：
//   1. Responsible.exe 被强杀（任务管理器结束、崩溃、kill -9 等价物）时，替它收尾：
//      清掉指向 RVM 存储的 subst 盘符、结束遗留的引擎/内核子进程；
//   2. 把这次异常退出记录成一份 JSON 报告 + 一行摘要日志，方便事后定位。
//
// 它必须比宿主活得久，所以宿主正常退出时会先 SetEvent 告别（--bye-event），
// RTray 收到告别就直接退出，什么都不清理——清理只在“没有告别”的情况下发生。
//
// 构建与 RVM 相同：CMake + Ninja + MinGW (C++20)。
#include <windows.h>
#include <shellapi.h>
#include <tlhelp32.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <ctime>
#include <cwchar>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <string_view>
#include <vector>

namespace {

namespace fs = std::filesystem;

constexpr const wchar_t* kVersion = L"RTray 0.1.0-p0";

// 只杀这些名字的遗留子进程：宿主自己被强杀时，引擎和 DSH 内核会变成孤儿。
// 不按 PID 泛杀，避免父 PID 被复用后误伤无关进程。
constexpr const wchar_t* kOrphanWhitelist[] = {L"RvmEngine.exe", L"node.exe"};

struct Options {
  DWORD host_pid = 0;
  std::wstring bye_event;
  std::wstring heartbeat;
  std::wstring cleanup_subst_root;
  bool cleanup_children = true;
  std::wstring report_dir;
  DWORD grace_ms = 1500;
  bool show_help = false;
  bool show_version = false;
};

std::string WideToUtf8(std::wstring_view text) {
  if (text.empty()) return {};
  const int needed = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()),
                                         nullptr, 0, nullptr, nullptr);
  if (needed <= 0) return {};
  std::string result(static_cast<std::size_t>(needed), '\0');
  WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), result.data(), needed,
                      nullptr, nullptr);
  return result;
}

std::string JsonEscape(std::string_view text) {
  std::string out;
  out.reserve(text.size() + 8);
  for (const char ch : text) {
    switch (ch) {
      case '"': out += "\\\""; break;
      case '\\': out += "\\\\"; break;
      case '\n': out += "\\n"; break;
      case '\r': out += "\\r"; break;
      case '\t': out += "\\t"; break;
      default:
        if (static_cast<unsigned char>(ch) < 0x20) {
          char buffer[8];
          std::snprintf(buffer, sizeof(buffer), "\\u%04x", static_cast<unsigned char>(ch));
          out += buffer;
        } else {
          out += ch;
        }
    }
  }
  return out;
}

std::string UtcNowIso() {
  const std::time_t now = std::time(nullptr);
  const std::tm* utc = std::gmtime(&now);
  if (utc == nullptr) return {};
  char buffer[32];
  std::strftime(buffer, sizeof(buffer), "%Y-%m-%dT%H:%M:%SZ", utc);
  return buffer;
}

std::wstring FileStampNow() {
  const std::time_t now = std::time(nullptr);
  const std::tm* local = std::localtime(&now);
  if (local == nullptr) return L"unknown";
  wchar_t buffer[32];
  std::wcsftime(buffer, sizeof(buffer) / sizeof(buffer[0]), L"%Y%m%d-%H%M%S", local);
  return buffer;
}

std::wstring DefaultReportDir() {
  wchar_t local_app_data[MAX_PATH]{};
  const DWORD length =
      GetEnvironmentVariableW(L"LOCALAPPDATA", local_app_data, static_cast<DWORD>(std::size(local_app_data)));
  if (length == 0 || length >= std::size(local_app_data)) return L".";
  return fs::path(local_app_data).append(L"Responsible").append(L"logs").wstring();
}

void PrintUsage() {
  // 统一走 UTF-8 单字节输出：重定向到文件时 std::wcout 会按控制台代码页丢中文
  const auto line = [](const wchar_t* text) { std::cout << WideToUtf8(text) << "\n"; };
  line(kVersion);
  line(L"");
  line(L"用法: RTray --watch <pid> [选项]");
  line(L"");
  line(L"  --watch <pid>              要监视的宿主进程（必填）");
  line(L"  --bye-event <name>         宿主正常退出时置位的命名事件；收到就只记录、不清理");
  line(L"  --heartbeat <file>         宿主的心跳文件，报告里会带上最后一次心跳距今多久");
  line(L"  --cleanup-subst <root>     清理所有映射到 <root> 之下的 subst 盘符");
  line(L"  --no-cleanup-children      不结束遗留的 RvmEngine.exe / node.exe");
  line(L"  --report-dir <dir>         报告目录，默认 %LOCALAPPDATA%\\Responsible\\logs");
  line(L"  --grace <ms>               宿主退出后等子进程自己收尾的时间，默认 1500");
  line(L"  --version                  版本");
  line(L"  --help                     本帮助");
  line(L"");
  line(L"报告：<report-dir>\\crash-<时间>-<pid>.json，另追加一行到 crash.log");
}

bool ParseOptions(int argc, wchar_t** argv, Options* options, std::wstring* error) {
  options->report_dir = DefaultReportDir();
  for (int index = 1; index < argc; ++index) {
    const std::wstring_view arg = argv[index];
    const auto next = [&](std::wstring* value) {
      if (index + 1 >= argc) {
        *error = L"缺少参数: " + std::wstring(arg);
        return false;
      }
      *value = argv[++index];
      return true;
    };

    if (arg == L"--watch") {
      std::wstring value;
      if (!next(&value)) return false;
      try {
        options->host_pid = static_cast<DWORD>(std::stoul(value));
      } catch (const std::exception&) {
        *error = L"--watch 需要进程号";
        return false;
      }
    } else if (arg == L"--bye-event") {
      if (!next(&options->bye_event)) return false;
    } else if (arg == L"--heartbeat") {
      if (!next(&options->heartbeat)) return false;
    } else if (arg == L"--cleanup-subst") {
      if (!next(&options->cleanup_subst_root)) return false;
    } else if (arg == L"--report-dir") {
      if (!next(&options->report_dir)) return false;
    } else if (arg == L"--grace") {
      std::wstring value;
      if (!next(&value)) return false;
      try {
        options->grace_ms = static_cast<DWORD>(std::stoul(value));
      } catch (const std::exception&) {
        *error = L"--grace 需要毫秒数";
        return false;
      }
    } else if (arg == L"--no-cleanup-children") {
      options->cleanup_children = false;
    } else if (arg == L"--version") {
      options->show_version = true;
    } else if (arg == L"--help" || arg == L"-h") {
      options->show_help = true;
    } else {
      *error = L"未知参数: " + std::wstring(arg);
      return false;
    }
  }
  if (options->show_help || options->show_version) return true;
  if (options->host_pid == 0) {
    *error = L"必须指定 --watch <pid>";
    return false;
  }
  return true;
}

std::wstring QueryDriveTarget(const std::wstring& drive) {
  wchar_t buffer[2048]{};
  const DWORD length =
      QueryDosDeviceW(drive.c_str(), buffer, static_cast<DWORD>(std::size(buffer)));
  if (length == 0) return {};
  // QueryDosDevice 返回 NT 路径（\??\C:\...），字符串里可能带 MULTI_SZ 的终止符
  std::wstring value(buffer, length);
  const auto zero = value.find(L'\0');
  if (zero != std::wstring::npos) value.resize(zero);
  return value;
}

// 判断盘符是否映射到 root 之下：剥掉 \??\ 前缀后做大小写无关的前缀比较。
bool DriveBelongsTo(const std::wstring& target, const std::wstring& root) {
  if (target.empty() || root.empty()) return false;
  std::wstring path = target;
  if (path.rfind(L"\\??\\", 0) == 0) path.erase(0, 4);
  if (path.size() < root.size()) return false;
  const int compared = CompareStringOrdinal(path.c_str(), static_cast<int>(root.size()), root.c_str(),
                                            static_cast<int>(root.size()), TRUE);
  return compared == CSTR_EQUAL;
}

bool RemoveDriveMapping(const std::wstring& drive, const std::wstring& target) {
  // 先试 DefineDosDevice：不需要起 subst.exe，失败再回退到命令行
  if (DefineDosDeviceW(DDD_REMOVE_DEFINITION | DDD_EXACT_MATCH_ON_REMOVE, drive.c_str(),
                       target.c_str())) {
    return true;
  }
  const std::wstring command = L"subst " + drive + L" /D";
  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  startup.dwFlags = STARTF_USESHOWWINDOW;
  startup.wShowWindow = SW_HIDE;
  PROCESS_INFORMATION process{};
  std::wstring mutable_command = command;
  if (!CreateProcessW(nullptr, mutable_command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
                      nullptr, nullptr, &startup, &process)) {
    return false;
  }
  WaitForSingleObject(process.hProcess, 5000);
  DWORD exit_code = 1;
  GetExitCodeProcess(process.hProcess, &exit_code);
  CloseHandle(process.hThread);
  CloseHandle(process.hProcess);
  return exit_code == 0;
}

std::vector<std::wstring> ClearDriveMappingsUnder(const std::wstring& root,
                                                  std::vector<std::wstring>* cleared) {
  std::vector<std::wstring> failed;
  for (wchar_t letter = L'A'; letter <= L'Z'; ++letter) {
    const std::wstring drive = std::wstring(1, letter) + L":";
    const std::wstring target = QueryDriveTarget(drive);
    if (target.empty()) continue;
    if (!DriveBelongsTo(target, root)) continue;
    if (RemoveDriveMapping(drive, target)) {
      cleared->push_back(drive);
    } else {
      failed.push_back(drive);
    }
  }
  return failed;
}

struct ProcessEntry {
  DWORD pid = 0;
  std::wstring name;
};

std::vector<ProcessEntry> ChildrenOf(DWORD parent_pid) {
  std::vector<ProcessEntry> result;
  const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
  if (snapshot == INVALID_HANDLE_VALUE) return result;
  PROCESSENTRY32W entry{};
  entry.dwSize = sizeof(entry);
  if (Process32FirstW(snapshot, &entry)) {
    do {
      if (entry.th32ParentProcessID != parent_pid) continue;
      if (entry.th32ProcessID == GetCurrentProcessId()) continue;
      const std::wstring name = entry.szExeFile;
      const bool whitelisted =
          std::any_of(std::begin(kOrphanWhitelist), std::end(kOrphanWhitelist),
                      [&](const wchar_t* candidate) {
                        return CompareStringOrdinal(name.c_str(), -1, candidate, -1, TRUE) ==
                               CSTR_EQUAL;
                      });
      if (!whitelisted) continue;
      result.push_back({entry.th32ProcessID, name});
    } while (Process32NextW(snapshot, &entry));
  }
  CloseHandle(snapshot);
  return result;
}

bool TerminateAndWait(DWORD pid) {
  const HANDLE process = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, pid);
  if (process == nullptr) return false;
  const bool terminated = TerminateProcess(process, 1) != FALSE;
  WaitForSingleObject(process, 5000);
  CloseHandle(process);
  return terminated;
}

// 心跳文件最后一次写入距今多少毫秒；拿不到返回 -1。
long long HeartbeatAgeMs(const std::wstring& path) {
  if (path.empty()) return -1;
  WIN32_FILE_ATTRIBUTE_DATA data{};
  if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &data)) return -1;
  ULARGE_INTEGER written{};
  written.LowPart = data.ftLastWriteTime.dwLowDateTime;
  written.HighPart = data.ftLastWriteTime.dwHighDateTime;
  FILETIME now{};
  GetSystemTimeAsFileTime(&now);
  ULARGE_INTEGER current{};
  current.LowPart = now.dwLowDateTime;
  current.HighPart = now.dwHighDateTime;
  if (current.QuadPart <= written.QuadPart) return 0;
  return static_cast<long long>((current.QuadPart - written.QuadPart) / 10000ull);
}

struct Outcome {
  bool graceful = false;
  DWORD exit_code = 0;
  std::vector<std::wstring> cleared_drives;
  std::vector<std::wstring> failed_drives;
  std::vector<ProcessEntry> terminated;
  long long heartbeat_age_ms = -1;
};

std::wstring BuildReportPath(const std::wstring& directory, DWORD host_pid) {
  std::wstring name = L"crash-" + FileStampNow() + L"-" + std::to_wstring(host_pid) + L".json";
  return (fs::path(directory) / name).wstring();
}

bool WriteReport(const Options& options, const Outcome& outcome, std::wstring* path_out,
                 std::wstring* error) {
  std::error_code directory_error;
  fs::create_directories(options.report_dir, directory_error);

  const std::wstring path = BuildReportPath(options.report_dir, options.host_pid);
  std::ofstream output(fs::path(path), std::ios::binary | std::ios::trunc);
  if (!output) {
    *error = L"无法写入报告: " + path;
    return false;
  }

  char hex_exit[16];
  std::snprintf(hex_exit, sizeof(hex_exit), "0x%08lX", static_cast<unsigned long>(outcome.exit_code));

  output << "{\n";
  output << "  \"tool\": \"" << WideToUtf8(kVersion) << "\",\n";
  output << "  \"at\": \"" << UtcNowIso() << "\",\n";
  output << "  \"host_pid\": " << options.host_pid << ",\n";
  output << "  \"graceful\": " << (outcome.graceful ? "true" : "false") << ",\n";
  output << "  \"exit_code\": " << static_cast<long>(outcome.exit_code) << ",\n";
  output << "  \"exit_code_hex\": \"" << hex_exit << "\",\n";
  output << "  \"heartbeat_age_ms\": " << outcome.heartbeat_age_ms << ",\n";
  output << "  \"subst_root\": \"" << JsonEscape(WideToUtf8(options.cleanup_subst_root)) << "\",\n";

  output << "  \"cleared_drives\": [";
  for (std::size_t index = 0; index < outcome.cleared_drives.size(); ++index) {
    if (index != 0) output << ", ";
    output << "\"" << JsonEscape(WideToUtf8(outcome.cleared_drives[index])) << "\"";
  }
  output << "],\n";

  output << "  \"failed_drives\": [";
  for (std::size_t index = 0; index < outcome.failed_drives.size(); ++index) {
    if (index != 0) output << ", ";
    output << "\"" << JsonEscape(WideToUtf8(outcome.failed_drives[index])) << "\"";
  }
  output << "],\n";

  output << "  \"terminated_processes\": [";
  for (std::size_t index = 0; index < outcome.terminated.size(); ++index) {
    if (index != 0) output << ", ";
    output << "{\"pid\": " << outcome.terminated[index].pid << ", \"name\": \""
           << JsonEscape(WideToUtf8(outcome.terminated[index].name)) << "\"}";
  }
  output << "]\n";
  output << "}\n";
  output.close();

  std::ofstream log(fs::path(options.report_dir) / L"crash.log", std::ios::binary | std::ios::app);
  if (log) {
    log << UtcNowIso() << " host=" << options.host_pid
        << (outcome.graceful ? " graceful" : " abnormal") << " exit=" << hex_exit
        << " cleared=" << outcome.cleared_drives.size()
        << " killed=" << outcome.terminated.size() << " report=" << WideToUtf8(path) << "\n";
  }
  *path_out = path;
  return true;
}

}  // namespace

int main() {
  SetConsoleOutputCP(CP_UTF8);
  SetConsoleCP(CP_UTF8);
  int argc = 0;
  wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
  if (argv == nullptr) {
    std::cerr << "RTray: 无法解析命令行\n";
    return 2;
  }

  Options options;
  std::wstring error;
  const bool parsed = ParseOptions(argc, argv, &options, &error);
  LocalFree(argv);
  if (!parsed) {
    std::cerr << WideToUtf8(L"RTray: " + error + L"\n\n");
    PrintUsage();
    return 2;
  }
  if (options.show_version) {
    std::cout << WideToUtf8(kVersion) << "\n";
    return 0;
  }
  if (options.show_help) {
    PrintUsage();
    return 0;
  }

  const HANDLE host = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE,
                                  options.host_pid);
  if (host == nullptr) {
    std::cerr << WideToUtf8(L"RTray: 无法打开宿主进程 " + std::to_wstring(options.host_pid) + L"（错误 " + std::to_wstring(GetLastError()) + L"）\n");
    return 3;
  }

  HANDLE bye = nullptr;
  if (!options.bye_event.empty()) {
    bye = OpenEventW(SYNCHRONIZE, FALSE, options.bye_event.c_str());
  }

  HANDLE handles[2] = {host, bye};
  const DWORD wait_count = bye != nullptr ? 2 : 1;
  const std::wstring heartbeat_before = options.heartbeat;
  const DWORD waited = WaitForMultipleObjects(wait_count, handles, FALSE, INFINITE);

  Outcome outcome;
  outcome.graceful = bye != nullptr && waited == WAIT_OBJECT_0 + 1;
  outcome.heartbeat_age_ms = HeartbeatAgeMs(heartbeat_before);
  if (waited == WAIT_OBJECT_0) {
    DWORD exit_code = 0;
    if (GetExitCodeProcess(host, &exit_code)) outcome.exit_code = exit_code;
  } else if (waited == WAIT_FAILED) {
    outcome.exit_code = GetLastError();
  }

  if (!outcome.graceful) {
    // 宿主没告别就没了：先让它自己的收尾跑一会儿（IDE 调试、正常 kill 也可能走到这里），
    // 再把孤儿进程清掉。
    if (options.grace_ms > 0) Sleep(options.grace_ms);

    if (options.cleanup_children) {
      for (const ProcessEntry& child : ChildrenOf(options.host_pid)) {
        if (TerminateAndWait(child.pid)) outcome.terminated.push_back(child);
      }
    }
    outcome.heartbeat_age_ms = HeartbeatAgeMs(options.heartbeat);
  }

  // 盘符清理两种情况都做：宿主正常退出时它自己会清，这里再走一遍是空操作；
  // 万一它清到一半就没了（子进程还在占着卷），这一步才是最后兜底。
  if (!options.cleanup_subst_root.empty()) {
    outcome.failed_drives = ClearDriveMappingsUnder(options.cleanup_subst_root,
                                                    &outcome.cleared_drives);
  }

  std::wstring report_path;
  std::wstring report_error;
  const bool reported = WriteReport(options, outcome, &report_path, &report_error);

  if (bye != nullptr) CloseHandle(bye);
  CloseHandle(host);

  if (!reported) {
    std::cerr << WideToUtf8(L"RTray: " + report_error + L"\n");
    return 4;
  }
  if (outcome.graceful) {
    std::cout << WideToUtf8(L"RTray: 宿主正常退出，已记录 " + report_path + L"\n");
  } else {
    std::cout << WideToUtf8(L"RTray: 宿主异常退出（" +
                            std::to_wstring(outcome.exit_code) + L"），已收尾：清理盘符 " +
                            std::to_wstring(outcome.cleared_drives.size()) + L" 个、结束进程 " +
                            std::to_wstring(outcome.terminated.size()) + L" 个；报告 " +
                            report_path + L"\n");
  }
  return 0;
}
