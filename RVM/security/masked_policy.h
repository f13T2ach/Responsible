#pragma once

#include <string>
#include <string_view>
#include <vector>

namespace rvm::security {

class MaskedPolicy {
 public:
  explicit MaskedPolicy(std::vector<std::wstring> patterns);

  bool IsMasked(std::wstring_view absolute_path, std::wstring_view workspace_root) const;

  const std::vector<std::wstring>& patterns() const { return patterns_; }

  static std::vector<std::wstring> DefaultPatterns();

 private:
  static bool Match(std::wstring_view pattern, std::wstring_view text);
  static bool MatchBasename(std::wstring_view pattern, std::wstring_view path);

  std::vector<std::wstring> patterns_;
};

}  // namespace rvm::security