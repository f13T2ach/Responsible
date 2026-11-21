#include <windows.h>

#include <filesystem>
#include <string>
#include <vector>

#include "server/shell_runner.h"
#include "test_registry.h"

namespace {

std::wstring TempRoot() {
  wchar_t buffer[MAX_PATH] = {};
  const DWORD length = GetTempPathW(MAX_PATH, buffer);
  std::wstring base(buffer, length);
  while (!base.empty() && (base.back() == L'\\' || base.back() == L'/')) base.pop_back();
  return base;
}

}  // namespace

RVM_TEST(host_path_filters_relative_unc_and_session_paths) {
  const std::wstring root =
      TempRoot() + L"\\rvm_hostpath_" + std::to_wstring(GetCurrentProcessId());
  const std::wstring workspace = root + L"\\ws";
  const std::wstring temp = root + L"\\tmp";
  std::error_code error;
  std::filesystem::remove_all(root, error);
  for (const std::wstring& dir : {root + L"\\a", root + L"\\b", workspace, temp}) {
    RVM_CHECK(std::filesystem::create_directories(dir, error) && !error);
  }

  const std::wstring host_path = root + L"\\a;;relative;" + root + L"\\b;" + workspace + L";" +
                                 temp + L";\\\\server\\share;missing;" + root + L"\\a";

  const std::vector<std::wstring> entries = rvm::server::FilterHostPathEntries(
      host_path, workspace, std::wstring(), temp);

  RVM_CHECK_EQ(entries.size(), 2u);
  RVM_CHECK_EQ(entries[0], root + L"\\a");
  RVM_CHECK_EQ(entries[1], root + L"\\b");

  std::filesystem::remove_all(root, error);
}