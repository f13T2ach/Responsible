#include "server/jailbreak_smoke.h"

#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <filesystem>
#include <iostream>
#include <string>
#include <utility>
#include <vector>

#include "channel/convert.h"
#include "core/error.h"
#include "sandbox/backend.h"
#include "server/shell_runner.h"

namespace rvm::server {
namespace {

namespace fs = std::filesystem;

constexpr std::uint32_t kProbeTimeoutMs = 30000;

std::wstring HostValue(const wchar_t* name) {
  const DWORD size = GetEnvironmentVariableW(name, nullptr, 0);
  if (size == 0) return {};
  std::wstring value(size, L'\0');
  const DWORD copied = GetEnvironmentVariableW(name, value.data(), size);
  if (copied == 0 || copied >= size) return {};
  value.resize(copied);
  return value;
}

std::wstring HostTempDirectory() {
  wchar_t buffer[MAX_PATH + 2] = {};
  const DWORD length = GetTempPathW(MAX_PATH + 1, buffer);
  std::wstring dir(buffer, length);
  while (!dir.empty() && (dir.back() == L'\\' || dir.back() == L'/')) dir.pop_back();
  return dir;
}

std::wstring JoinPath(const std::wstring& directory, const std::wstring& name) {
  return (fs::path(directory) / name).wstring();
}

bool WriteHostFile(const std::wstring& path, const std::string& content) {
  const HANDLE file =
      CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                  FILE_ATTRIBUTE_NORMAL, nullptr);
  if (file == INVALID_HANDLE_VALUE) return false;
  DWORD written = 0;
  const BOOL ok =
      WriteFile(file, content.data(), static_cast<DWORD>(content.size()), &written, nullptr);
  CloseHandle(file);
  return ok != 0 && written == content.size();
}

bool HostFileExists(const std::wstring& path) {
  const DWORD attributes = GetFileAttributesW(path.c_str());
  return attributes != INVALID_FILE_ATTRIBUTES &&
         (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

void DeleteHostFile(const std::wstring& path) {
  if (!path.empty()) DeleteFileW(path.c_str());
}

std::string ToUtf8OrEmpty(const std::wstring& value) {
  auto utf8 = channel::ToUtf8(value);
  return utf8.ok() ? utf8.value() : std::string();
}

// Console output can contain non UTF-8 bytes from cmd.exe; keep the log line
// printable and bounded so a denial message never breaks the check format.
std::string Snippet(const std::string& text, std::size_t limit = 220) {
  std::string out;
  out.reserve(std::min(limit, text.size()));
  for (const char raw : text) {
    if (out.size() >= limit) {
      out += "...";
      break;
    }
    const unsigned char ch = static_cast<unsigned char>(raw);
    out.push_back(ch >= 0x20 && ch < 0x7f ? static_cast<char>(ch) : '.');
  }
  return out;
}

std::string TrimAscii(std::string value) {
  while (!value.empty() && (value.back() == '\r' || value.back() == '\n' || value.back() == ' ')) {
    value.pop_back();
  }
  std::size_t start = 0;
  while (start < value.size() && value[start] == ' ') ++start;
  return value.substr(start);
}

// cmd.exe rejects forward slashes in several path arguments (type, del), so a
// workspace passed as D:/x must reach the probes as D:\x.
std::wstring NativePath(std::wstring path) {
  for (wchar_t& ch : path) {
    if (ch == L'/') ch = L'\\';
  }
  return path;
}

std::wstring ToLower(std::wstring value) {
  for (wchar_t& ch : value) {
    if (ch >= L'A' && ch <= L'Z') ch = static_cast<wchar_t>(ch - L'A' + L'a');
  }
  while (!value.empty() && (value.back() == L'\\' || value.back() == L'/')) value.pop_back();
  return value;
}

std::wstring PyLiteral(const std::wstring& path) {
  std::wstring literal;
  for (const wchar_t ch : path) {
    if (ch == L'\\' || ch == L'\'') literal.push_back(L'\\');
    literal.push_back(ch);
  }
  return L"'" + literal + L"'";
}

struct Probe {
  bool ran = false;
  std::uint32_t exit_code = 0;
  std::string output;
  std::string error;
  std::string failure;
};

Probe Run(const ShellContext& context, const std::vector<std::wstring>& argv) {
  Probe probe;
  v1::ShellExecRequest request;
  request.set_session_id(1);
  request.set_timeout_ms(kProbeTimeoutMs);
  for (const std::wstring& argument : argv) {
    auto utf8 = channel::ToUtf8(argument);
    if (!utf8.ok()) {
      probe.failure = "argument is not valid UTF-8";
      return probe;
    }
    request.add_argv(utf8.value());
  }
  auto outcome = RunShell(context, request, [] { return false; });
  if (!outcome.ok()) {
    probe.failure = outcome.error().message;
    return probe;
  }
  probe.ran = true;
  probe.exit_code = outcome.value().result.exit_code;
  probe.output = outcome.value().result.output;
  probe.error = outcome.value().result.error;
  return probe;
}

std::string Brief(const Probe& probe) {
  if (!probe.failure.empty()) return "engine=" + Snippet(probe.failure);
  std::string brief = "exit=" + std::to_string(probe.exit_code);
  if (!probe.output.empty()) brief += " out=" + Snippet(probe.output);
  if (!probe.error.empty()) brief += " err=" + Snippet(probe.error);
  return brief;
}

std::string Combined(const Probe& probe) { return probe.output + probe.error; }

}  // namespace

int RunJailbreakSmoke(const JailbreakOptions& options) {
  std::cout << std::unitbuf;
  const std::wstring workspace_root = NativePath(options.workspace_root);
  std::error_code path_error;
  if (workspace_root.empty() || !fs::is_directory(workspace_root, path_error)) {
    std::cout << "setup=FAIL detail=workspace directory does not exist\n";
    return 2;
  }

  runtime::EnvironmentSpec spec;
  spec.session_id = L"jailbreak-smoke";
  spec.workspace_root = workspace_root;
  spec.policy.mode = core::SandboxMode::kWorkspaceWrite;
  spec.policy.approval = core::ApprovalMode::kAsk;
  spec.policy.network_access = false;
  if (!options.overlay_drive.empty()) {
    spec.overlay_workspace = true;
    spec.overlay_drive = options.overlay_drive;
  }
  for (const std::wstring& tool : options.tool_paths) spec.tool_paths.push_back(NativePath(tool));

  std::wstring python = NativePath(options.python_exe);
  if (!python.empty()) {
    const std::wstring parent = fs::path(python).parent_path().wstring();
    if (!parent.empty() &&
        std::find(spec.tool_paths.begin(), spec.tool_paths.end(), parent) ==
            spec.tool_paths.end()) {
      spec.tool_paths.push_back(parent);
    }
  }

  auto backend = sandbox::CreateSandboxBackend();
  auto environment = backend->Create(spec);
  if (!environment.ok()) {
    std::cout << "setup=FAIL detail=" << Snippet(environment.error().message) << '\n';
    return 2;
  }
  auto facts = environment.value()->Start();
  if (!facts.ok()) {
    std::cout << "setup=FAIL detail=" << Snippet(facts.error().message) << '\n';
    return 2;
  }

  ShellContext context;
  context.host_workspace_root = spec.workspace_root;
  context.workspace_path = facts.value().workspace_path.empty()
                               ? spec.workspace_root
                               : facts.value().workspace_path;
  context.temp_path = facts.value().temp_path;
  context.appcontainer_sid = facts.value().appcontainer_sid;
  context.restricted_token = facts.value().restricted_token;
  context.exposed_environment = facts.value().exposed_environment;
  context.tool_paths = facts.value().tool_paths;

  std::cout << "session_state=ready\n";
  std::cout << "workspace=" << ToUtf8OrEmpty(context.workspace_path) << '\n';
  std::cout << "temp=" << ToUtf8OrEmpty(context.temp_path) << '\n';
  for (const std::wstring& tool : context.tool_paths) {
    std::cout << "tool_path=" << ToUtf8OrEmpty(tool) << '\n';
  }

  if (python.empty()) {
    for (const std::wstring& tool : context.tool_paths) {
      for (const wchar_t* name : {L"python.exe", L"python3.exe"}) {
        const std::wstring candidate = JoinPath(tool, name);
        if (HostFileExists(candidate)) {
          python = candidate;
          break;
        }
      }
      if (!python.empty()) break;
    }
  }

  const std::wstring suffix = L"_" + std::to_wstring(GetCurrentProcessId()) + L"_" +
                              std::to_wstring(GetTickCount64() % 1000000);
  const std::wstring host_temp = HostTempDirectory();
  const std::wstring profile = HostValue(L"USERPROFILE");
  if (host_temp.empty() || profile.empty()) {
    std::cout << "setup=FAIL detail=host temp or profile directory is unavailable\n";
    environment.value()->Stop();
    return 2;
  }
  // The sibling canary must live next to the real workspace even when the
  // session runs on a mounted disk; that parent write is the reported escape.
  const std::wstring parent = fs::path(spec.workspace_root).parent_path().wstring();

  const std::wstring read_temp = JoinPath(host_temp, L"rvm_jailbreak_read" + suffix + L".txt");
  const std::wstring read_profile = JoinPath(profile, L"rvm_jailbreak_read" + suffix + L".txt");
  const std::wstring read_parent = JoinPath(parent, L"rvm_jailbreak_read" + suffix + L".txt");
  const std::wstring write_temp = JoinPath(host_temp, L"rvm_jailbreak_write" + suffix + L".txt");
  const std::wstring write_profile = JoinPath(profile, L"rvm_jailbreak_write" + suffix + L".txt");
  const std::wstring write_parent = JoinPath(parent, L"rvm_jailbreak_write" + suffix + L".txt");
  const std::wstring inside_workspace =
      JoinPath(context.workspace_path, L"_rvm_jailbreak_inside" + suffix + L".txt");
  const std::wstring inside_temp_path = L"%TEMP%\\_rvm_jailbreak_temp" + suffix + L".txt";
  const std::wstring inside_temp_host =
      JoinPath(context.temp_path, L"_rvm_jailbreak_temp" + suffix + L".txt");
  const std::wstring python_inside =
      JoinPath(context.workspace_path, L"_rvm_jailbreak_python" + suffix + L".txt");
  const std::wstring tool_write =
      python.empty()
          ? std::wstring()
          : JoinPath(fs::path(python).parent_path().wstring(),
                     L"_rvm_jailbreak_tool" + suffix + L".txt");

  const std::string secret = "RVM_HOST_SECRET" + std::to_string(GetCurrentProcessId());
  const std::string secret_profile =
      "RVM_PROFILE_SECRET" + std::to_string(GetCurrentProcessId());
  const std::string secret_parent =
      "RVM_PARENT_SECRET" + std::to_string(GetCurrentProcessId());

  bool canaries_ready = true;
  canaries_ready &= WriteHostFile(read_temp, secret);
  canaries_ready &= WriteHostFile(read_profile, secret_profile);
  canaries_ready &= WriteHostFile(read_parent, secret_parent);
  if (!canaries_ready) {
    DeleteHostFile(read_temp);
    DeleteHostFile(read_profile);
    DeleteHostFile(read_parent);
    std::cout << "setup=FAIL detail=could not create the host canary files\n";
    environment.value()->Stop();
    return 2;
  }

  int failures = 0;
  const auto check = [&failures](const char* name, bool pass, const std::string& detail) {
    if (!pass) ++failures;
    std::cout << "check=" << name << " result=" << (pass ? "PASS" : "FAIL");
    if (!detail.empty()) std::cout << " detail=" << detail;
    std::cout << '\n';
  };

  // Sanity: the session can still use its workspace and its own temp.
  {
    const Probe write =
        Run(context, {L"cmd.exe", L"/c", L"echo", L"RVM_INSIDE_OK", L">", inside_workspace});
    const Probe read = Run(context, {L"cmd.exe", L"/c", L"type", inside_workspace});
    check("workspace_write", write.ran && write.exit_code == 0 &&
                                 read.ran && Combined(read).find("RVM_INSIDE_OK") != std::string::npos,
          "write=" + Brief(write) + " read=" + Brief(read));
  }
  {
    const Probe write =
        Run(context, {L"cmd.exe", L"/c", L"echo", L"RVM_TEMP_OK", L">", inside_temp_path});
    const Probe read = Run(context, {L"cmd.exe", L"/c", L"type", inside_temp_path});
    check("temp_write", write.ran && write.exit_code == 0 &&
                            read.ran && Combined(read).find("RVM_TEMP_OK") != std::string::npos,
          "write=" + Brief(write) + " read=" + Brief(read));
  }
  {
    const Probe mapped = Run(context, {L"cmd.exe", L"/c", L"echo", L"%TEMP%"});
    const std::string reported = mapped.ran ? TrimAscii(mapped.output) : std::string();
    const std::wstring expected = ToLower(context.temp_path);
    const std::wstring actual = ToLower(std::wstring(reported.begin(), reported.end()));
    const bool inside_temp =
        actual == expected ||
        actual.rfind(expected + L"\\", 0) == 0;
    check("temp_mapping", mapped.ran && inside_temp,
          "expected=" + ToUtf8OrEmpty(context.temp_path) + " reported=" + reported);
  }

  // Reads outside the workspace must not reveal the canary contents.
  {
    const Probe probe = Run(context, {L"cmd.exe", L"/c", L"type", read_temp});
    check("host_temp_read", probe.ran && Combined(probe).find(secret) == std::string::npos,
          Brief(probe));
  }
  {
    const Probe probe = Run(context, {L"cmd.exe", L"/c", L"type", read_profile});
    check("profile_read",
          probe.ran && Combined(probe).find(secret_profile) == std::string::npos,
          Brief(probe));
  }
  {
    const Probe probe = Run(context, {L"cmd.exe", L"/c", L"type", read_parent});
    check("parent_read", probe.ran && Combined(probe).find(secret_parent) == std::string::npos,
          Brief(probe));
  }

  // Writes outside the workspace must leave no file behind.
  {
    const Probe probe =
        Run(context, {L"cmd.exe", L"/c", L"echo", L"RVM_JAILBREAK", L">", write_temp});
    check("host_temp_write", probe.ran && !HostFileExists(write_temp), Brief(probe));
  }
  {
    const Probe probe =
        Run(context, {L"cmd.exe", L"/c", L"echo", L"RVM_JAILBREAK", L">", write_profile});
    check("profile_write", probe.ran && !HostFileExists(write_profile), Brief(probe));
  }
  {
    const Probe probe =
        Run(context, {L"cmd.exe", L"/c", L"echo", L"RVM_JAILBREAK", L">", write_parent});
    check("parent_write", probe.ran && !HostFileExists(write_parent), Brief(probe));
  }

  // The exposed interpreter must run, inherit the containment, and still write
  // inside the workspace.
  if (python.empty() || !HostFileExists(python)) {
    check("python_available", false,
          python.empty() ? "no python.exe was exposed" : "python.exe does not exist");
  } else {
    std::cout << "python=" << ToUtf8OrEmpty(python) << '\n';
    const Probe alive = Run(context, {python, L"-c", L"print('PY_ALIVE')"});
    check("python_available",
          alive.ran && alive.exit_code == 0 &&
              Combined(alive).find("PY_ALIVE") != std::string::npos,
          Brief(alive));

    {
      const std::wstring code = L"print(open(" + PyLiteral(read_parent) + L").read())";
      const Probe probe = Run(context, {python, L"-c", code});
      check("python_read_parent",
            probe.ran && Combined(probe).find(secret_parent) == std::string::npos, Brief(probe));
    }
    {
      const std::wstring code = L"print(open(" + PyLiteral(read_profile) + L").read())";
      const Probe probe = Run(context, {python, L"-c", code});
      check("python_read_profile",
            probe.ran && Combined(probe).find(secret_profile) == std::string::npos, Brief(probe));
    }
    {
      const std::wstring code =
          L"open(" + PyLiteral(write_parent) + L",'w').write('x')";
      const Probe probe = Run(context, {python, L"-c", code});
      check("python_write_parent", probe.ran && !HostFileExists(write_parent), Brief(probe));
    }
    {
      const std::wstring code =
          L"open(" + PyLiteral(write_profile) + L",'w').write('x')";
      const Probe probe = Run(context, {python, L"-c", code});
      check("python_write_profile", probe.ran && !HostFileExists(write_profile), Brief(probe));
    }
    {
      const std::wstring code = L"open(" + PyLiteral(tool_write) + L",'w').write('x')";
      const Probe probe = Run(context, {python, L"-c", code});
      check("python_write_tool_dir", probe.ran && !HostFileExists(tool_write), Brief(probe));
    }
    {
      const std::wstring code = L"open(" + PyLiteral(python_inside) +
                                L",'w').write('PY_INSIDE_OK');print(open(" +
                                PyLiteral(python_inside) + L").read())";
      const Probe probe = Run(context, {python, L"-c", code});
      check("python_inside_workspace",
            probe.ran && probe.exit_code == 0 &&
                Combined(probe).find("PY_INSIDE_OK") != std::string::npos,
            Brief(probe));
    }
  }

  // Host-side tools (the file editor) create files after the session starts;
  // the inherited workspace grant must let sandboxed shells remove them.
  // PowerShell and Python reach the delete through different paths, so probe
  // both, plus a file the sandbox itself created.
  {
    const std::wstring host_pwsh =
        JoinPath(context.workspace_path, L"_rvm_host_pwsh" + suffix + L".txt");
    const std::wstring host_python =
        JoinPath(context.workspace_path, L"_rvm_host_python" + suffix + L".txt");
    const std::wstring sandbox_file =
        JoinPath(context.workspace_path, L"_rvm_sandbox_pwsh" + suffix + L".txt");
    const bool seeded = WriteHostFile(host_pwsh, "host") && WriteHostFile(host_python, "host");
    if (!seeded) {
      check("host_seed", false, "could not create host-side files in the workspace");
    } else {
      const Probe ps = Run(context, {L"powershell.exe", L"-NoProfile", L"-NonInteractive",
                                     L"-Command",
                                     L"Remove-Item -LiteralPath '" + host_pwsh +
                                         L"' -Force; if (Test-Path -LiteralPath '" + host_pwsh +
                                         L"') { 'RVM_STILL' } else { 'RVM_GONE' }"});
      check("pwsh_delete_host_file",
            ps.ran && !HostFileExists(host_pwsh) &&
                Combined(ps).find("RVM_GONE") != std::string::npos,
            Brief(ps));
      if (!python.empty() && HostFileExists(python)) {
        const Probe py = Run(context, {python, L"-c",
                                       L"import os; os.remove(" + PyLiteral(host_python) +
                                           L"); print('RVM_GONE')"});
        check("python_delete_host_file",
              py.ran && !HostFileExists(host_python) &&
                  Combined(py).find("RVM_GONE") != std::string::npos,
              Brief(py));
      }
    }
    const Probe created = Run(context, {L"cmd.exe", L"/c", L"echo", L"x", L">", sandbox_file});
    const Probe removed = Run(context, {L"powershell.exe", L"-NoProfile", L"-NonInteractive",
                                        L"-Command",
                                        L"Remove-Item -LiteralPath '" + sandbox_file +
                                            L"' -Force; if (Test-Path -LiteralPath '" +
                                            sandbox_file + L"') { 'RVM_STILL' } else { 'RVM_GONE' }"});
    check("pwsh_delete_sandbox_file",
          created.ran && removed.ran && !HostFileExists(sandbox_file) &&
              Combined(removed).find("RVM_GONE") != std::string::npos,
          Brief(removed));
    DeleteHostFile(host_pwsh);
    DeleteHostFile(host_python);
    DeleteHostFile(sandbox_file);
  }

  DeleteHostFile(read_temp);
  DeleteHostFile(read_profile);
  DeleteHostFile(read_parent);
  DeleteHostFile(write_temp);
  DeleteHostFile(write_profile);
  DeleteHostFile(write_parent);
  DeleteHostFile(inside_workspace);
  DeleteHostFile(inside_temp_host);
  DeleteHostFile(python_inside);
  DeleteHostFile(tool_write);

  std::cout << "jailbreak_result=" << (failures == 0 ? "PASS" : "FAIL") << '\n';
  std::cout << "cleanup=revoking-sandbox-grants (large tool directories can take minutes)\n";

  const core::Status stopped = environment.value()->Stop();
  if (!stopped.ok()) {
    std::cout << "warning=session did not stop cleanly detail="
              << Snippet(stopped.error().message) << '\n';
  }
  std::cout << "cleanup=done\n";

  return failures == 0 ? 0 : 1;
}

}  // namespace rvm::server