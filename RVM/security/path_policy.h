#pragma once

#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

#include "core/result.h"

namespace rvm::security {

struct PathPolicyOptions {
  std::size_t max_path_length = 32767;
};

class PathPolicy {
 public:
  explicit PathPolicy(PathPolicyOptions options = {});

  core::Result<std::wstring> Normalize(std::wstring_view input, std::wstring_view cwd) const;
  core::Status Authorize(std::wstring_view normalized,
                         const std::vector<std::wstring>& roots) const;

  static bool IsWithin(std::wstring_view path, std::wstring_view root);
  static std::wstring FoldCase(std::wstring_view value);

 private:
  PathPolicyOptions options_;
};

}  // namespace rvm::security