#pragma once

#include <functional>
#include <string>
#include <vector>

namespace rvm::test {

struct TestCase {
  std::string name;
  std::function<void()> body;
};

std::vector<TestCase>& Registry();

struct Registrar {
  Registrar(std::string name, std::function<void()> body);
};

void Check(bool condition, const char* expression, const char* file, int line);

}  // namespace rvm::test

#define RVM_TEST(name)                                              \
  static void name();                                               \
  static ::rvm::test::Registrar name##_registrar(#name, name);      \
  static void name()

#define RVM_CHECK(expression) \
  ::rvm::test::Check(static_cast<bool>(expression), #expression, __FILE__, __LINE__)

#define RVM_CHECK_EQ(left, right)                                  \
  do {                                                             \
    const auto& rvm_left = (left);                                 \
    const auto& rvm_right = (right);                               \
    ::rvm::test::Check(rvm_left == rvm_right, #left " == " #right, \
                       __FILE__, __LINE__);                        \
  } while (false)