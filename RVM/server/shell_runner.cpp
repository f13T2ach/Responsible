#include "server/shell_runner.h"

#include <filesystem>

#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cstddef>
#include <utility>
#include <vector>

#include "channel/convert.h"
#include "core/error.h"
#include "server/network_audit.h"

namespace rvm::server {
namespace {

constexpr std::uint32_t kDefaultTimeoutMs = 120000;
constexpr std::uint32_t kMaxTimeoutMs = 1800000;
constexpr std::uint32_t kMinTimeoutMs = 100;
constexpr std::uint32_t kDefaultOutputCap = 4u * 1024u * 1024u;
constexpr std::uint32_t kMaxOutputCap = 16u * 1024u * 1024u;

std::wstring ToLower(std::wstring value) {
  for (wchar_t& ch : value) {
    if (ch >= L'A' && ch <= L'Z') ch = static_cast<wchar_t>(ch - L'A' + L'a');
  }
  return value;
}

std::wstring TrimTrailingSeparators(std::wstring path) {
  while (path.size() > 1 && (path.back() == L'\\' || path.back() == L'/')) {
    path.pop_back();
  }
  return path;
}

// Host paths arrive both from Win32 (backslashes) and from UTF-8 callers that
// may use forward slashes; compare and join on one canonical form.
std::wstring NormalizeSeparators(std::wstring path) {
  for (wchar_t& ch : path) {
    if (ch == L'/') ch = L'\\';
  }
  return TrimTrailingSeparators(std::move(path));
}

bool IsUnder(const std::wstring& root, const std::wstring& path) {
  if (root.empty() || path.size() < root.size()) return false;
  if (ToLower(path.substr(0, root.size())) != ToLower(root)) return false;
  return path.size() == root.size() || path[root.size()] == L'\\' || path[root.size()] == L'/';
}

std::wstring TrimPathEntry(const std::wstring& value) {
  std::size_t begin = 0;
  std::size_t end = value.size();
  while (begin < end && (value[begin] == L' ' || value[begin] == L'\t')) ++begin;
  while (end > begin && (value[end - 1] == L' ' || value[end - 1] == L'\t')) --end;
  std::wstring entry = value.substr(begin, end - begin);
  if (entry.size() >= 2 && entry.front() == L'"' && entry.back() == L'"') {
    entry = entry.substr(1, entry.size() - 2);
  }
  return entry;
}

// Only drive-rooted local directories may be appended from the host PATH.
// Relative or UNC entries would change binary resolution, and the workspace or
// session temp must never shadow host tools.
bool IsAppendableHostPath(const std::wstring& entry) {
  if (entry.size() < 3) return false;
  const wchar_t drive = entry[0];
  if (!((drive >= L'A' && drive <= L'Z') || (drive >= L'a' && drive <= L'z'))) return false;
  if (entry[1] != L':' || (entry[2] != L'\\' && entry[2] != L'/')) return false;
  if (entry.find(L'"') != std::wstring::npos) return false;
  const DWORD attributes = GetFileAttributesW(entry.c_str());
  return attributes != INVALID_FILE_ATTRIBUTES &&
         (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

void AppendHostPath(const std::wstring& host_path, const std::wstring& workspace_path,
                    const std::wstring& host_workspace_root, const std::wstring& temp_path,
                    std::vector<std::wstring>& entries) {
  std::size_t start = 0;
  while (start <= host_path.size()) {
    const std::size_t end = host_path.find(L';', start);
    const std::wstring raw =
        host_path.substr(start, end == std::wstring::npos ? std::wstring::npos : end - start);
    start = end == std::wstring::npos ? host_path.size() + 1 : end + 1;

    std::wstring entry = NormalizeSeparators(TrimPathEntry(raw));
    if (!IsAppendableHostPath(entry)) continue;
    if (IsUnder(workspace_path, entry) || IsUnder(host_workspace_root, entry) ||
        IsUnder(temp_path, entry)) {
      continue;
    }
    const bool duplicate = std::any_of(entries.begin(), entries.end(),
                                       [&](const std::wstring& existing) {
                                         return ToLower(existing) == ToLower(entry);
                                       });
    if (!duplicate) entries.push_back(std::move(entry));
  }
}

// Quote one argv element per the CommandLineToArgvW / CRT rules so the child
// sees exactly the arguments the caller intended.
std::wstring QuoteArgument(const std::wstring& arg) {
  if (!arg.empty() && arg.find_first_of(L" \t\n\v\"") == std::wstring::npos) return arg;
  std::wstring quoted = L"\"";
  for (std::size_t i = 0;; ++i) {
    std::size_t backslashes = 0;
    while (i < arg.size() && arg[i] == L'\\') {
      ++i;
      ++backslashes;
    }
    if (i == arg.size()) {
      quoted.append(backslashes * 2, L'\\');
      break;
    }
    if (arg[i] == L'"') {
      quoted.append(backslashes * 2 + 1, L'\\');
      quoted.push_back(L'"');
    } else {
      quoted.append(backslashes, L'\\');
      quoted.push_back(arg[i]);
    }
  }
  quoted.push_back(L'"');
  return quoted;
}

core::Result<std::wstring> TranslateWorkdir(const ShellContext& context,
                                            const std::wstring& workdir) {
  const std::wstring virtual_root = NormalizeSeparators(context.workspace_path);
  if (workdir.empty()) return core::Ok(virtual_root);
  if (workdir.size() > 1 && workdir[1] != L':') {
    return core::Error(core::ErrorCode::kPathOutOfScope,
                       "shell workdir must be an absolute local path");
  }
  const std::wstring host_root = NormalizeSeparators(context.host_workspace_root);
  const std::wstring normalized = NormalizeSeparators(workdir);
  if (!host_root.empty() && IsUnder(host_root, normalized)) {
    std::wstring suffix = normalized.substr(host_root.size());
    while (!suffix.empty() && suffix.front() == L'\\') suffix.erase(suffix.begin());
    return core::Ok(suffix.empty() ? virtual_root : virtual_root + L"\\" + suffix);
  }
  if (IsUnder(virtual_root, normalized)) return core::Ok(normalized);
  return core::Error(core::ErrorCode::kPathOutOfScope,
                     "shell workdir is outside the session workspace", false,
                     std::string(workdir.begin(), workdir.end()));
}

std::wstring HostEnvironmentValue(const wchar_t* name) {
  const DWORD size = GetEnvironmentVariableW(name, nullptr, 0);
  if (size == 0) return {};
  std::wstring value(size, L'\0');
  const DWORD copied = GetEnvironmentVariableW(name, value.data(), size);
  if (copied == 0 || copied >= size) return {};
  value.resize(copied);
  return value;
}

// Names whose value must always come from the sandbox, never from the user's
// exposed set: they decide where processes write or which binaries load.
bool IsEngineEnvironmentName(const std::wstring& lower) {
  static const wchar_t* const kReserved[] = {
      L"path",       L"pathext",      L"temp",        L"tmp",
      L"tmpdir",     L"systemroot",   L"windir",      L"systemdrive",
      L"comspec",    L"userprofile",  L"home",        L"homedrive",
      L"homepath",   L"appdata",      L"localappdata", L"username",
      L"os",         L"number_of_processors", L"processor_architecture",
      L"psmodulepath"};
  for (const wchar_t* name : kReserved) {
    if (lower == name) return true;
  }
  return lower.rfind(L"responsible_rvm_", 0) == 0;
}

// Build the complete child environment. Nothing from the host is forwarded:
// only the variables the engine needs, the session temp/home tree, and the
// variables/tools the user explicitly exposed. TEMP therefore always points
// at the sandbox temp even if the caller passes its own TEMP.
std::vector<std::pair<std::wstring, std::wstring>> BuildEnvironment(
    const ShellContext& context) {
  std::vector<std::pair<std::wstring, std::wstring>> environment;
  std::wstring system_root = HostEnvironmentValue(L"SystemRoot");
  if (system_root.empty()) system_root = L"C:\\Windows";
  std::wstring system_drive = system_root.size() >= 2 ? system_root.substr(0, 2) : L"C:";
  const std::wstring home = context.temp_path + L"\\home";

  std::vector<std::wstring> path_entries = {
      system_root + L"\\System32",
      system_root,
      system_root + L"\\System32\\Wbem",
      system_root + L"\\System32\\WindowsPowerShell\\v1.0",
  };
  for (const std::wstring& tool : context.tool_paths) {
    if (!tool.empty()) path_entries.push_back(tool);
  }
  for (const auto& [name, value] : context.exposed_environment) {
    if (ToLower(name) == L"path") {
      AppendHostPath(value, context.workspace_path, context.host_workspace_root,
                     context.temp_path, path_entries);
      break;
    }
  }
  std::wstring path;
  for (const std::wstring& entry : path_entries) {
    if (!path.empty()) path.push_back(L';');
    path.append(entry);
  }

  environment.emplace_back(L"SystemRoot", system_root);
  environment.emplace_back(L"windir", system_root);
  environment.emplace_back(L"SystemDrive", system_drive);
  environment.emplace_back(L"ComSpec", system_root + L"\\System32\\cmd.exe");
  environment.emplace_back(
      L"PATHEXT", L".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC");
  environment.emplace_back(L"PATH", path);
  environment.emplace_back(L"PSModulePath",
                           system_root + L"\\System32\\WindowsPowerShell\\v1.0\\Modules");
  environment.emplace_back(L"OS", L"Windows_NT");
  environment.emplace_back(L"PROCESSOR_ARCHITECTURE", L"AMD64");
  environment.emplace_back(L"USERNAME", L"rvm");
  const std::wstring processors = HostEnvironmentValue(L"NUMBER_OF_PROCESSORS");
  if (!processors.empty()) environment.emplace_back(L"NUMBER_OF_PROCESSORS", processors);

  environment.emplace_back(L"TEMP", context.temp_path);
  environment.emplace_back(L"TMP", context.temp_path);
  environment.emplace_back(L"TMPDIR", context.temp_path);
  environment.emplace_back(L"HOME", home);
  environment.emplace_back(L"USERPROFILE", home);
  environment.emplace_back(L"HOMEDRIVE", system_drive);
  environment.emplace_back(L"HOMEPATH", home.size() > 2 ? home.substr(2) : L"\\");
  environment.emplace_back(L"APPDATA", home + L"\\AppData\\Roaming");
  environment.emplace_back(L"LOCALAPPDATA", home + L"\\AppData\\Local");

  for (const auto& [name, value] : context.exposed_environment) {
    if (name.empty() || name.find(L'=') != std::wstring::npos) continue;
    if (IsEngineEnvironmentName(ToLower(name))) continue;
    environment.emplace_back(name, value);
  }
  return environment;
}

}  // namespace

std::vector<std::wstring> FilterHostPathEntries(const std::wstring& host_path,
                                                const std::wstring& workspace_path,
                                                const std::wstring& host_workspace_root,
                                                const std::wstring& temp_path) {
  std::vector<std::wstring> entries;
  AppendHostPath(host_path, workspace_path, host_workspace_root, temp_path, entries);
  return entries;
}

core::Result<ShellOutcome> RunShell(const ShellContext& context,
                                    const v1::ShellExecRequest& request,
                                    const std::function<bool()>& should_cancel) {
  if (context.workspace_path.empty()) {
    return core::Error(core::ErrorCode::kInvalidState,
                       "the session has no workspace path to run commands in");
  }

  auto workdir = channel::FromUtf8(request.workdir());
  if (!workdir.ok()) return workdir.error();
  auto translated = TranslateWorkdir(context, workdir.value());
  if (!translated.ok()) return translated.error();

  std::wstring command_line;
  if (request.argv_size() > 0) {
    for (const std::string& arg : request.argv()) {
      auto decoded = channel::FromUtf8(arg);
      if (!decoded.ok()) return decoded.error();
      if (!command_line.empty()) command_line.push_back(L' ');
      command_line.append(QuoteArgument(decoded.value()));
    }
  } else {
    auto decoded = channel::FromUtf8(request.command());
    if (!decoded.ok()) return decoded.error();
    command_line = std::move(decoded.value());
  }
  if (command_line.empty()) {
    return core::Error(core::ErrorCode::kInvalidArgument,
                       "shell request needs argv or a command");
  }

  auto environment = BuildEnvironment(context);

  std::uint32_t timeout_ms = request.timeout_ms();
  if (timeout_ms == 0) timeout_ms = kDefaultTimeoutMs;
  timeout_ms = std::clamp(timeout_ms, kMinTimeoutMs, kMaxTimeoutMs);

  std::uint32_t output_cap = request.stdout_max_bytes();
  if (output_cap == 0) output_cap = kDefaultOutputCap;
  output_cap = std::min(output_cap, kMaxOutputCap);

  sandbox::ProcessOptions options;
  options.command_line = std::move(command_line);
  options.working_directory = translated.value();
  options.timeout_ms = timeout_ms;
  options.memory_limit_bytes = 1024ull * 1024ull * 1024ull;
  options.max_processes = 64;
  options.max_output_bytes = output_cap;
  options.should_cancel = should_cancel;
  options.environment = std::move(environment);
  for (const runtime::ToolAccess& tool : context.tools) {
    if (!tool.capability_sid.empty()) options.capabilities.push_back(tool.capability_sid);
  }
  if (context.network_enabled && !context.network_capability_sid.empty()) {
    options.capabilities.push_back(context.network_capability_sid);
  }
  if (!request.stdin_data().empty()) {
    options.stdin_data.assign(request.stdin_data().begin(), request.stdin_data().end());
  }

  options.containment = context.restricted_token ? sandbox::Containment::kRestrictedToken
                                                 : sandbox::Containment::kAppContainer;
  options.appcontainer_sid = context.appcontainer_sid;

  // Network audit is real table sampling scoped to this command's process tree.
  // It only runs when the session actually has outbound capability; a disabled
  // session leaves no listener or connection to observe.
  NetworkAudit audit(context.network_enabled && !context.archive_root.empty()
                         ? (std::filesystem::path(context.archive_root) / L"network.log").wstring()
                         : std::wstring());
  options.on_started = [&audit, &context](std::uint32_t pid) {
    audit.Start(context.session_id, pid);
  };
  options.on_finished = [&audit] { audit.Stop(); };

  const auto started = std::chrono::steady_clock::now();
  auto result = sandbox::RunProcess(options);
  if (!result.ok()) return result.error();
  const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
                           std::chrono::steady_clock::now() - started)
                           .count();

  ShellOutcome outcome;
  outcome.process_id = result.value().process_id;
  outcome.duration_ms = static_cast<std::uint64_t>(std::max<std::int64_t>(elapsed, 0));
  outcome.output_truncated = result.value().output_truncated;
  outcome.error_truncated = result.value().error_truncated;
  outcome.result = std::move(result.value());
  return core::Ok(std::move(outcome));
}

}  // namespace rvm::server