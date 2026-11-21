#include "security/auth.h"

#include <cstddef>

namespace rvm::security {

bool ConstantTimeEquals(std::string_view left, std::string_view right) {
  const std::size_t length = left.size() > right.size() ? left.size() : right.size();
  unsigned char difference = static_cast<unsigned char>(left.size() ^ right.size());
  for (std::size_t i = 0; i < length; ++i) {
    const unsigned char a = i < left.size() ? static_cast<unsigned char>(left[i]) : 0;
    const unsigned char b = i < right.size() ? static_cast<unsigned char>(right[i]) : 0;
    difference |= static_cast<unsigned char>(a ^ b);
  }
  return difference == 0;
}

std::string TrimTokenLine(std::string_view line) {
  std::size_t begin = 0;
  std::size_t end = line.size();
  const auto is_space = [](char ch) {
    return ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n';
  };
  while (begin < end && is_space(line[begin])) ++begin;
  while (end > begin && is_space(line[end - 1])) --end;
  return std::string(line.substr(begin, end - begin));
}

}  // namespace rvm::security