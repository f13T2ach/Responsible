#include <windows.h>

#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>

#include "runtime/backend.h"
#include "sandbox/backend.h"
#include "test_registry.h"

namespace {

namespace fs = std::filesystem;

std::wstring TempRoot() {
  wchar_t buffer[MAX_PATH] = {};
  const DWORD length = GetTempPathW(MAX_PATH, buffer);
  if (length == 0 || length >= MAX_PATH) return {};
  return std::wstring(buffer, length);
}

std::wstring StorageDir(const std::wstring& key) {
  wchar_t local[MAX_PATH] = {};
  const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH);
  if (length == 0 || length >= MAX_PATH) return {};
  return (fs::path(std::wstring(local, length)) / L"Responsible" / L"RVM" / key).wstring();
}

bool RunGit(const std::wstring& directory, const std::wstring& arguments) {
  const std::wstring command =
      L"git -C \"" + directory + L"\" " + arguments + L" >nul 2>&1";
  return _wsystem(command.c_str()) == 0;
}

void WriteText(const fs::path& path, const std::string& text) {
  std::ofstream output(path, std::ios::trunc | std::ios::binary);
  output << text;
}

std::string ReadText(const fs::path& path) {
  std::ifstream input(path, std::ios::binary);
  return std::string((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
}

bool HasChange(const rvm::runtime::WorkspaceDiff& diff, const std::wstring& path,
               rvm::runtime::WorkspaceChangeKind kind) {
  for (const auto& change : diff.changes) {
    if (change.path == path && change.kind == kind) return true;
  }
  return false;
}

}  // namespace

RVM_TEST(workspace_archive_diff_sync_rollback) {
  const std::wstring root = TempRoot();
  RVM_CHECK(!root.empty());
  if (root.empty()) return;

  const std::wstring stamp = std::to_wstring(GetCurrentProcessId()) + L"-" +
                             std::to_wstring(GetTickCount64());
  const fs::path workspace = fs::path(root) / (L"rvm-archive-ws-" + stamp);
  std::error_code error;
  fs::create_directories(workspace, error);
  RVM_CHECK(!error);
  if (error) return;
  WriteText(workspace / L"a.txt", "one\n");
  WriteText(workspace / L"b.txt", "two\n");

  RVM_CHECK(RunGit(workspace.wstring(), L"init -q"));
  RVM_CHECK(RunGit(workspace.wstring(), L"add -A"));

  const std::wstring key = L"archivetest" + stamp;
  rvm::runtime::EnvironmentSpec spec;
  spec.session_id = L"archive-test";
  spec.storage_key = key;
  spec.workspace_root = workspace.wstring();
  spec.overlay_workspace = true;
  spec.policy.mode = rvm::core::SandboxMode::kWorkspaceWrite;

  auto backend = rvm::sandbox::CreateSandboxBackend();
  auto environment = backend->Create(spec);
  RVM_CHECK(environment.ok());
  if (!environment.ok()) {
    fs::remove_all(workspace, error);
    return;
  }
  auto facts = environment.value()->Start();
  if (!facts.ok()) {
    // AppContainer unavailable on this machine; nothing to verify.
    std::fprintf(stderr, "workspace archive test skipped: %s\n",
                 facts.error().message.c_str());
    fs::remove_all(workspace, error);
    return;
  }

  const fs::path session_disk(facts.value().workspace_path);
  WriteText(session_disk / L"a.txt", "one changed in sandbox\n");
  WriteText(session_disk / L"new.txt", "created in sandbox\n");

  auto diff = environment.value()->DiffWorkspace();
  RVM_CHECK(diff.ok());
  if (diff.ok()) {
    RVM_CHECK(HasChange(diff.value(), L"a.txt", rvm::runtime::WorkspaceChangeKind::kModified));
    RVM_CHECK(HasChange(diff.value(), L"new.txt", rvm::runtime::WorkspaceChangeKind::kAdded));
    RVM_CHECK(diff.value().changes.size() == 2);
  }

  auto applied = environment.value()->ApplyWorkspace({}, true);
  RVM_CHECK(applied.ok());
  if (applied.ok()) {
    RVM_CHECK(applied.value().conflicts.empty());
    RVM_CHECK(applied.value().applied.size() == 2);
  }
  RVM_CHECK(ReadText(workspace / L"a.txt") == "one changed in sandbox\n");
  RVM_CHECK(ReadText(workspace / L"new.txt") == "created in sandbox\n");

  auto after_sync = environment.value()->DiffWorkspace();
  RVM_CHECK(after_sync.ok());
  if (after_sync.ok()) RVM_CHECK(after_sync.value().changes.empty());

  WriteText(session_disk / L"a.txt", "changed again\n");
  WriteText(session_disk / L"b.txt", "deleted in sandbox?\n");
  fs::remove(session_disk / L"b.txt", error);
  auto second = environment.value()->DiffWorkspace();
  RVM_CHECK(second.ok());
  if (second.ok()) {
    RVM_CHECK(HasChange(second.value(), L"a.txt", rvm::runtime::WorkspaceChangeKind::kModified));
    RVM_CHECK(HasChange(second.value(), L"b.txt", rvm::runtime::WorkspaceChangeKind::kDeleted));
  }

  const rvm::core::Status rolled = environment.value()->RollbackWorkspace();
  RVM_CHECK(rolled.ok());
  RVM_CHECK(ReadText(session_disk / L"a.txt") == "one changed in sandbox\n");
  RVM_CHECK(ReadText(session_disk / L"b.txt") == "two\n");
  RVM_CHECK(fs::exists(session_disk / L"new.txt"));

  auto after_rollback = environment.value()->DiffWorkspace();
  RVM_CHECK(after_rollback.ok());
  if (after_rollback.ok()) RVM_CHECK(after_rollback.value().changes.empty());

  const fs::path archive = StorageDir(key);
  RVM_CHECK(fs::exists(archive / L"baseline.manifest"));
  RVM_CHECK(fs::exists(archive / L"system.json"));
  RVM_CHECK(fs::exists(archive / L"system.log"));
  RVM_CHECK(fs::exists(archive / L"audit.log"));

  const rvm::core::Status stopped = environment.value()->Stop();
  RVM_CHECK(stopped.ok());
  fs::remove_all(workspace, error);
  fs::remove_all(archive, error);
}

RVM_TEST(workspace_archive_preserves_cross_session_conflict) {
  const std::wstring root = TempRoot();
  RVM_CHECK(!root.empty());
  if (root.empty()) return;

  const std::wstring stamp = std::to_wstring(GetCurrentProcessId()) + L"-" +
                             std::to_wstring(GetTickCount64());
  const fs::path workspace = fs::path(root) / (L"rvm-conflict-ws-" + stamp);
  std::error_code error;
  fs::create_directories(workspace, error);
  RVM_CHECK(!error);
  if (error) return;
  WriteText(workspace / L"a.txt", "host original\n");
  RVM_CHECK(RunGit(workspace.wstring(), L"init -q"));
  RVM_CHECK(RunGit(workspace.wstring(), L"add -A"));

  const std::wstring key = L"conflicttest" + stamp;
  rvm::runtime::EnvironmentSpec spec;
  spec.session_id = L"conflict-one";
  spec.storage_key = key;
  spec.workspace_root = workspace.wstring();
  spec.overlay_workspace = true;
  spec.policy.mode = rvm::core::SandboxMode::kWorkspaceWrite;

  auto backend = rvm::sandbox::CreateSandboxBackend();
  auto first = backend->Create(spec);
  RVM_CHECK(first.ok());
  if (!first.ok()) {
    fs::remove_all(workspace, error);
    return;
  }
  auto first_facts = first.value()->Start();
  if (!first_facts.ok()) {
    std::fprintf(stderr, "cross-session conflict test skipped: %s\n",
                 first_facts.error().message.c_str());
    fs::remove_all(workspace, error);
    return;
  }
  WriteText(fs::path(first_facts.value().workspace_path) / L"a.txt", "sandbox edit\n");
  const rvm::core::Status first_stop = first.value()->Stop();
  RVM_CHECK(first_stop.ok());

  WriteText(workspace / L"a.txt", "host edit after baseline\n");

  spec.session_id = L"conflict-two";
  auto second = backend->Create(spec);
  RVM_CHECK(second.ok());
  if (!second.ok()) {
    fs::remove_all(workspace, error);
    return;
  }
  auto second_facts = second.value()->Start();
  RVM_CHECK(second_facts.ok());
  if (second_facts.ok()) {
    auto diff = second.value()->DiffWorkspace();
    RVM_CHECK(diff.ok());
    if (diff.ok()) {
      bool saw_conflict = false;
      for (const auto& change : diff.value().changes) {
        if (change.path == L"a.txt" && change.conflict) saw_conflict = true;
      }
      RVM_CHECK(saw_conflict);
    }
    auto applied = second.value()->ApplyWorkspace({}, true);
    RVM_CHECK(applied.ok());
    if (applied.ok()) {
      RVM_CHECK(applied.value().applied.empty());
      RVM_CHECK(!applied.value().conflicts.empty());
    }
    RVM_CHECK(ReadText(workspace / L"a.txt") == "host edit after baseline\n");
    second.value()->Stop();
  }

  fs::remove_all(workspace, error);
  fs::remove_all(StorageDir(key), error);
}

RVM_TEST(workspace_archive_requires_git_index) {
  const std::wstring root = TempRoot();
  RVM_CHECK(!root.empty());
  if (root.empty()) return;

  const fs::path workspace =
      fs::path(root) / (L"rvm-no-git-" + std::to_wstring(GetCurrentProcessId()) + L"-" +
                        std::to_wstring(GetTickCount64()));
  std::error_code error;
  fs::create_directories(workspace, error);
  RVM_CHECK(!error);
  if (error) return;
  WriteText(workspace / L"a.txt", "one\n");

  rvm::runtime::EnvironmentSpec spec;
  spec.session_id = L"archive-no-git";
  spec.workspace_root = workspace.wstring();
  spec.overlay_workspace = true;
  spec.policy.mode = rvm::core::SandboxMode::kWorkspaceWrite;

  auto backend = rvm::sandbox::CreateSandboxBackend();
  auto environment = backend->Create(spec);
  RVM_CHECK(environment.ok());
  if (!environment.ok()) {
    fs::remove_all(workspace, error);
    return;
  }
  auto facts = environment.value()->Start();
  RVM_CHECK(!facts.ok());
  if (!facts.ok()) {
    RVM_CHECK(facts.error().message.find("Git") != std::string::npos);
  }
  environment.value()->Stop();
  fs::remove_all(workspace, error);
}