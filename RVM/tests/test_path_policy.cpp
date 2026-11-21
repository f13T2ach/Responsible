#include <string>
#include <string_view>
#include <vector>

#include "security/path_policy.h"
#include "test_registry.h"

namespace {

const std::wstring kCwd = L"D:\\Work";

std::wstring ExpectNormalized(const rvm::security::PathPolicy& policy,
                              std::wstring_view path) {
  const auto result = policy.Normalize(path, kCwd);
  RVM_CHECK(result.ok());
  return result.value();
}

void ExpectRejected(const rvm::security::PathPolicy& policy, std::wstring_view path) {
  const auto result = policy.Normalize(path, kCwd);
  RVM_CHECK(!result.ok());
}

}  // namespace

RVM_TEST(path_policy_canonicalizes_relative_and_absolute_paths) {
  const rvm::security::PathPolicy policy;
  RVM_CHECK_EQ(ExpectNormalized(policy, L"src\\main.cpp"), std::wstring(L"D:\\Work\\src\\main.cpp"));
  RVM_CHECK_EQ(ExpectNormalized(policy, L"src/./lib/../main.cpp"),
               std::wstring(L"D:\\Work\\src\\main.cpp"));
  RVM_CHECK_EQ(ExpectNormalized(policy, L"D:/Work/src/../src/main.cpp"),
               std::wstring(L"D:\\Work\\src\\main.cpp"));
  RVM_CHECK_EQ(ExpectNormalized(policy, L"C:\\Windows\\System32"),
               std::wstring(L"C:\\Windows\\System32"));
  RVM_CHECK_EQ(ExpectNormalized(policy, L"C:\\"), std::wstring(L"C:\\"));
  RVM_CHECK_EQ(ExpectNormalized(policy, L"\\Temp\\x"), std::wstring(L"D:\\Temp\\x"));
  RVM_CHECK_EQ(ExpectNormalized(policy, L"."), std::wstring(L"D:\\Work"));
}

RVM_TEST(path_policy_rejects_escape_and_ambiguous_forms) {
  const rvm::security::PathPolicy policy;
  ExpectRejected(policy, L"");
  ExpectRejected(policy, L"\\\\server\\share\\file.txt");
  ExpectRejected(policy, L"\\\\?\\C:\\Windows");
  ExpectRejected(policy, L"\\\\.\\pipe\\rvm");
  ExpectRejected(policy, L"C:\\foo:bar");
  ExpectRejected(policy, L"C:foo");
  ExpectRejected(policy, L"C:\\foo.");
  ExpectRejected(policy, L"C:\\foo ");
  ExpectRejected(policy, L"C:\\CON");
  ExpectRejected(policy, L"C:\\CON.txt");
  ExpectRejected(policy, L"C:\\com1.log");
  ExpectRejected(policy, L"C:\\foo\\..\\..\\bar");
  ExpectRejected(policy, L"..\\..\\x");
  ExpectRejected(policy, std::wstring(L"C:\\foo\nbar"));
  ExpectRejected(policy, L"C:\\foo*bar");
  ExpectRejected(policy, L"C:\\foo?bar");
  ExpectRejected(policy, L"C:\\foo|bar");
  ExpectRejected(policy, L"C:\\foo<bar");
}

RVM_TEST(path_policy_containment_uses_component_boundaries) {
  RVM_CHECK(rvm::security::PathPolicy::IsWithin(L"C:\\Work\\a", L"C:\\Work"));
  RVM_CHECK(rvm::security::PathPolicy::IsWithin(L"C:\\Work", L"C:\\Work"));
  RVM_CHECK(rvm::security::PathPolicy::IsWithin(L"C:\\Work", L"C:\\Work\\"));
  RVM_CHECK(rvm::security::PathPolicy::IsWithin(L"c:\\work\\A", L"C:\\WORK"));
  RVM_CHECK(!rvm::security::PathPolicy::IsWithin(L"C:\\Work2\\a", L"C:\\Work"));
  RVM_CHECK(!rvm::security::PathPolicy::IsWithin(L"C:\\Workshop", L"C:\\Work"));
  RVM_CHECK(!rvm::security::PathPolicy::IsWithin(L"", L"C:\\Work"));
  RVM_CHECK(!rvm::security::PathPolicy::IsWithin(L"C:\\Work", L""));
}

RVM_TEST(path_policy_authorizes_only_configured_roots) {
  const rvm::security::PathPolicy policy;
  const std::vector<std::wstring> roots = {L"C:/Work/"};
  RVM_CHECK(policy.Authorize(L"C:\\Work\\a.txt", roots).ok());
  RVM_CHECK(policy.Authorize(L"C:\\Work\\sub\\a.txt", roots).ok());
  RVM_CHECK(!policy.Authorize(L"C:\\Workspace\\a.txt", roots).ok());
  RVM_CHECK(!policy.Authorize(L"C:\\Work\\a.txt", {}).ok());
}

RVM_TEST(path_policy_rejects_noncanonical_roots) {
  const rvm::security::PathPolicy policy;
  const std::vector<std::wstring> roots = {L"..\\Work"};
  const auto status = policy.Authorize(L"C:\\Work\\a.txt", roots);
  RVM_CHECK(!status.ok());
  RVM_CHECK(status.error().code == rvm::core::ErrorCode::kInvalidArgument);
}