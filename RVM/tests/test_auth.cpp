#include <string>

#include "security/auth.h"
#include "test_registry.h"

RVM_TEST(auth_constant_time_compare) {
  RVM_CHECK(rvm::security::ConstantTimeEquals("token-value", "token-value"));
  RVM_CHECK(!rvm::security::ConstantTimeEquals("token-value", "token-valuf"));
  RVM_CHECK(!rvm::security::ConstantTimeEquals("short", "longer"));
  RVM_CHECK(!rvm::security::ConstantTimeEquals("", "x"));
  RVM_CHECK(rvm::security::ConstantTimeEquals("", ""));
}

RVM_TEST(auth_trims_token_lines) {
  RVM_CHECK_EQ(rvm::security::TrimTokenLine("  abc123\r\n"), std::string("abc123"));
  RVM_CHECK_EQ(rvm::security::TrimTokenLine("abc123"), std::string("abc123"));
  RVM_CHECK_EQ(rvm::security::TrimTokenLine("\t\r\n"), std::string());
}