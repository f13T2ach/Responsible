#include "test_registry.h"

#include <exception>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <utility>

namespace rvm::test {

std::vector<TestCase>& Registry() {
  static std::vector<TestCase> registry;
  return registry;
}

Registrar::Registrar(std::string name, std::function<void()> body) {
  Registry().push_back({std::move(name), std::move(body)});
}

void Check(bool condition, const char* expression, const char* file, int line) {
  if (condition) return;
  std::ostringstream message;
  message << file << ':' << line << ": check failed: " << expression;
  throw std::runtime_error(message.str());
}

}  // namespace rvm::test

namespace {
int RunAll() {
  int failures = 0;
  for (const rvm::test::TestCase& test : rvm::test::Registry()) {
    try {
      test.body();
      std::cout << "[PASS] " << test.name << '\n';
    } catch (const std::exception& error) {
      ++failures;
      std::cout << "[FAIL] " << test.name << ": " << error.what() << '\n';
    }
  }
  std::cout << rvm::test::Registry().size() - static_cast<std::size_t>(failures) << " passed, "
            << failures << " failed\n";
  return failures == 0 ? 0 : 1;
}
}  // namespace

int main() { return RunAll(); }