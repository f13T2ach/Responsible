#include <windows.h>

#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#include "sandbox/appcontainer.h"
#include "test_registry.h"

namespace {

struct CapabilityFixture {
  std::wstring root;
  std::wstring sid;
  std::wstring profile;
  std::wstring file;
  std::vector<std::wstring> ancestors;
  bool granted_file = false;

  ~CapabilityFixture() {
    if (!sid.empty()) {
      if (granted_file) rvm::sandbox::RevokeDirectory(sid, file, false);
      for (auto it = ancestors.rbegin(); it != ancestors.rend(); ++it)
        rvm::sandbox::RevokeDirectory(sid, *it, false);
    }
    if (!profile.empty()) rvm::sandbox::DeleteProfile(profile);
    if (!root.empty()) {
      std::error_code error;
      std::filesystem::remove_all(root, error);
    }
  }
};

std::wstring TempRoot() {
  wchar_t buffer[MAX_PATH] = {};
  const DWORD length = GetTempPathW(MAX_PATH, buffer);
  std::wstring base(buffer, length);
  while (!base.empty() && (base.back() == L'\\' || base.back() == L'/')) base.pop_back();
  return base;
}

rvm::sandbox::ProcessResult RunType(const std::wstring& working_directory,
                               const std::wstring& appcontainer_sid,
                               const std::wstring& file,
                               const std::vector<std::wstring>& capabilities) {
  rvm::sandbox::ProcessOptions options;
  options.command_line = L"cmd.exe /d /c type \"" + file + L"\"";
  options.working_directory = working_directory;
  options.appcontainer_sid = appcontainer_sid;
  options.capabilities = capabilities;
  options.timeout_ms = 20000;
  auto result = rvm::sandbox::RunProcess(options);
  if (!result.ok()) throw std::runtime_error(result.error().message);
  return result.value();
}

}  // namespace

RVM_TEST(sandbox_capability_sid_grants_tool_read) {
  CapabilityFixture fixture;
  const std::wstring suffix =
      std::to_wstring(GetCurrentProcessId()) + L"_" + std::to_wstring(GetTickCount64());
  fixture.root = TempRoot() + L"\\rvm_cap_" + suffix;
  fixture.profile = L"RvmCapTest" + suffix;
  std::error_code error;
  std::filesystem::create_directories(fixture.root, error);
  RVM_CHECK(!error);
  fixture.file = fixture.root + L"\\secret.txt";
  {
    std::ofstream output(std::filesystem::path(fixture.file), std::ios::binary);
    output << "rvm-capability-ok";
  }

  auto capability =
      rvm::sandbox::DeriveCapabilitySid(L"ResponsibleRvmTest" + suffix);
  if (!capability.ok()) {
    throw std::runtime_error(capability.error().message + " " + capability.error().detail);
  }
  fixture.sid = capability.value();
  RVM_CHECK(!fixture.sid.empty());

  RVM_CHECK(rvm::sandbox::GrantDirectoryReadExecute(fixture.sid, fixture.root, false).ok());
  RVM_CHECK(rvm::sandbox::GrantDirectoryReadExecute(fixture.sid, fixture.file, false).ok());
  fixture.granted_file = true;

  auto profile = rvm::sandbox::CreateProfile(fixture.profile);
  RVM_CHECK(profile.ok());

  // The capability alone must satisfy the tool directory DACL.
  const auto allowed =
      RunType(fixture.root, profile.value().sid, fixture.file, {fixture.sid});
  RVM_CHECK_EQ(allowed.exit_code, 0u);
  RVM_CHECK(allowed.output.find("rvm-capability-ok") != std::string::npos);

  // Without it the same child token must be denied, proving the SID is the
  // persistent grant surface rather than the session AppContainer identity.
  const auto denied = RunType(fixture.root, profile.value().sid, fixture.file, {});
  RVM_CHECK(denied.output.find("rvm-capability-ok") == std::string::npos);
}