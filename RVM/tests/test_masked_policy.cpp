#include <string>
#include <vector>

#include "security/masked_policy.h"
#include "test_registry.h"

namespace {
const std::wstring kWorkspace = L"D:\\Work";
}

RVM_TEST(masked_policy_matches_basenames_and_patterns) {
  const rvm::security::MaskedPolicy policy({L".env*", L"*.pem", L"**\\credentials*"});
  RVM_CHECK(policy.IsMasked(L"D:\\Work\\.env", kWorkspace));
  RVM_CHECK(policy.IsMasked(L"D:\\Work\\sub\\.env.local", kWorkspace));
  RVM_CHECK(policy.IsMasked(L"D:\\Work\\certs\\server.pem", kWorkspace));
  RVM_CHECK(policy.IsMasked(L"D:\\Work\\cfg\\credentials.json", kWorkspace));
  RVM_CHECK(!policy.IsMasked(L"D:\\Work\\src\\main.cpp", kWorkspace));
  RVM_CHECK(!policy.IsMasked(L"D:\\Work\\config.yaml", kWorkspace));
}

RVM_TEST(masked_policy_is_case_insensitive) {
  const rvm::security::MaskedPolicy policy({L".env"});
  RVM_CHECK(policy.IsMasked(L"D:\\Work\\.ENV", kWorkspace));
  RVM_CHECK(policy.IsMasked(L"D:\\WORK\\.Env", kWorkspace));
}

RVM_TEST(masked_policy_matches_relative_separator_patterns) {
  const rvm::security::MaskedPolicy policy({L"src\\private\\**"});
  RVM_CHECK(policy.IsMasked(L"D:\\Work\\src\\private\\token.txt", kWorkspace));
  RVM_CHECK(!policy.IsMasked(L"D:\\Work\\src\\public\\token.txt", kWorkspace));
}

RVM_TEST(masked_policy_defaults_cover_sensitive_files) {
  const rvm::security::MaskedPolicy policy(rvm::security::MaskedPolicy::DefaultPatterns());
  RVM_CHECK(policy.IsMasked(L"D:\\Work\\.env", kWorkspace));
  RVM_CHECK(policy.IsMasked(L"D:\\Work\\home\\.ssh\\id_rsa", kWorkspace));
  RVM_CHECK(policy.IsMasked(L"D:\\Work\\.git\\config", kWorkspace));
  RVM_CHECK(policy.IsMasked(L"D:\\Work\\browser\\Cookies", kWorkspace));
  RVM_CHECK(policy.IsMasked(L"D:\\Work\\browser\\Login Data", kWorkspace));
  RVM_CHECK(policy.IsMasked(L"D:\\Work\\certs\\client.pem", kWorkspace));
  RVM_CHECK(!policy.IsMasked(L"D:\\Work\\src\\main.cpp", kWorkspace));
  RVM_CHECK(!policy.IsMasked(L"D:\\Work\\README.md", kWorkspace));
}

RVM_TEST(masked_policy_empty_patterns_mask_nothing) {
  const rvm::security::MaskedPolicy policy({});
  RVM_CHECK(!policy.IsMasked(L"D:\\Work\\.env", kWorkspace));
  RVM_CHECK(policy.patterns().empty());
}

RVM_TEST(masked_policy_does_not_match_prefix_collisions) {
  const rvm::security::MaskedPolicy policy({L".env"});
  RVM_CHECK(!policy.IsMasked(L"D:\\Work\\.envrc", kWorkspace));
  RVM_CHECK(!policy.IsMasked(L"D:\\Work\\.environment", kWorkspace));
}