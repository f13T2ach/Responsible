#include "server/shell_runner.h"

#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cstddef>

#include "channel/convert.h"
#include "core/error.h"

namespace rvm::server {
namespace {

constexpr std::uint32_t kDefaultTimeoutMs = 120000;
constexpr std::uint32_t kMaxTimeoutMs = 1800000;
constexpr std::uint32_t kMinTimeoutMs = 100;
constexpr std::uint32_t kDefaultOutputCap = 4u * 1024u * 1024u;
constexpr std::uint32_t kMaxOutputCap = 16u * 1024u * 1024u;
constexpr std::size_t kMaxEnvironmentEntries = 512;
constexpr std::size_t kMaxEnvironmentChars = 1024 * 1024;

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
  if (path.size() < root.size()) return false;
  if (ToLower(path.substr(0, root.size())) != ToLower(root)) return false;
  return path.size() == root.size() || path[root.size()] == L'\\' || path[root.size()] == L'/';
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

core::Result<std::vector<std::pair<std::wstring, std::wstring>>> TranslateEnvironment(
    const ShellContext& context, const v1::ShellExecRequest& request) {
  std::vector<std::pair<std::wstring, std::wstring>> environment;
  const auto& entries = request.env();
  if (entries.size() > kMaxEnvironmentEntries) {
    return core::Error(core::ErrorCode::kInvalidArgument,
                       "environment has too many entries");
  }
  std::size_t total = 0;
  environment.reserve(entries.size());
  for (const auto& entry : entries) {
    if (entry.first.empty() || entry.first.find('=') != std::string::npos ||
        entry.first.find('\0') != std::string::npos) {
      return core::Error(core::ErrorCode::kInvalidArgument,
                         "environment variable names must be non-empty without '='");
    }
    auto name = channel::FromUtf8(entry.first);
    if (!name.ok()) return name.error();
    auto value = channel::FromUtf8(entry.second);
    if (!value.ok()) return value.error();
    total += name.value().size() + value.value().size() + 2;
    if (total > kMaxEnvironmentChars) {
      return core::Error(core::ErrorCode::kInvalidArgument,
                         "environment exceeds the size limit");
    }
    environment.emplace_back(std::move(name.value()), std::move(value.value()));
  }
  if (!context.temp_path.empty()) {
    // Session-scoped temp replaces any caller-provided TEMP/TMP/TMPDIR so the
    // child can never fall back to the host %TEMP%.
    std::erase_if(environment, [](const auto& entry) {
      const std::wstring name = ToLower(entry.first);
      return name == L"temp" || name == L"tmp" || name == L"tmpdir";
    });
    environment.emplace_back(L"TEMP", context.temp_path);
    environment.emplace_back(L"TMP", context.temp_path);
    environment.emplace_back(L"TMPDIR", context.temp_path);
  }
  return core::Ok(std::move(environment));
}

}  // namespace

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

  auto environment = TranslateEnvironment(context, request);
  if (!environment.ok()) return environment.error();

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
  options.environment = environment.Take();
  if (!request.stdin_data().empty()) {
    options.stdin_data.assign(request.stdin_data().begin(), request.stdin_data().end());
  }

  // Mounted (overlay) workspaces always run under the restricted token; direct
  // workspaces keep full AppContainer containment.
  options.containment = context.restricted_token ? sandbox::Containment::kRestrictedToken
                                                 : sandbox::Containment::kAppContainer;
  options.appcontainer_sid = context.appcontainer_sid;

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