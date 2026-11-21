#include "security/masked_policy.h"

#include <algorithm>
#include <cstdint>
#include <vector>

#include "security/path_policy.h"

namespace rvm::security {
namespace {

std::wstring NormalizePattern(std::wstring_view pattern) {
  std::wstring value(pattern);
  std::replace(value.begin(), value.end(), L'/', L'\\');
  return PathPolicy::FoldCase(value);
}

bool ContainsSeparator(std::wstring_view pattern) {
  return pattern.find(L'\\') != std::wstring_view::npos;
}

bool MatchGlob(std::wstring_view pattern, std::wstring_view text) {
  const std::size_t p_size = pattern.size();
  const std::size_t t_size = text.size();
  if (p_size > 1024 || t_size > 32767) return false;
  if (p_size > 0 && t_size > 0 && p_size * t_size > 4 * 1024 * 1024) return false;

  std::vector<std::int8_t> memo((p_size + 1) * (t_size + 1), -1);

  const auto match_at = [&](auto&& self, std::size_t pi, std::size_t ti) -> bool {
    std::int8_t& cached = memo[pi * (t_size + 1) + ti];
    if (cached != -1) return cached != 0;

    bool result = false;
    if (pi == p_size) {
      result = ti == t_size;
    } else if (pattern[pi] == L'*') {
      if (pi + 1 < p_size && pattern[pi + 1] == L'*') {
        result = self(self, pi + 2, ti);
        if (!result && ti < t_size) result = self(self, pi, ti + 1);
      } else {
        result = self(self, pi + 1, ti);
        if (!result && ti < t_size && text[ti] != L'\\') {
          result = self(self, pi, ti + 1);
        }
      }
    } else if (pattern[pi] == L'?') {
      result = ti < t_size && text[ti] != L'\\' && self(self, pi + 1, ti + 1);
    } else {
      result = ti < t_size && pattern[pi] == text[ti] && self(self, pi + 1, ti + 1);
    }

    cached = result ? 1 : 0;
    return result;
  };

  if (p_size >= 3 && pattern[0] == L'*' && pattern[1] == L'*' && pattern[2] == L'\\') {
    if (match_at(match_at, 3, 0)) return true;
  }
  return match_at(match_at, 0, 0);
}

std::wstring Basename(std::wstring_view path) {
  const std::size_t position = path.find_last_of(L'\\');
  if (position == std::wstring_view::npos) return std::wstring(path);
  return std::wstring(path.substr(position + 1));
}

std::wstring RelativeTo(std::wstring_view path, std::wstring_view root) {
  if (root.empty()) return std::wstring(path);
  std::wstring folded_root = PathPolicy::FoldCase(root);
  while (folded_root.size() > 3 && folded_root.back() == L'\\') folded_root.pop_back();
  if (!folded_root.empty() && folded_root.back() != L'\\') folded_root.push_back(L'\\');
  const std::wstring folded_path = PathPolicy::FoldCase(path);
  if (folded_path.size() > folded_root.size() &&
      folded_path.compare(0, folded_root.size(), folded_root) == 0) {
    return std::wstring(path.substr(folded_root.size()));
  }
  return std::wstring(path);
}

}  // namespace

MaskedPolicy::MaskedPolicy(std::vector<std::wstring> patterns) {
  patterns_.reserve(patterns.size());
  for (const std::wstring& pattern : patterns) {
    std::wstring normalized = NormalizePattern(pattern);
    if (!normalized.empty()) patterns_.push_back(std::move(normalized));
  }
}

bool MaskedPolicy::Match(std::wstring_view pattern, std::wstring_view text) {
  return MatchGlob(pattern, text);
}

bool MaskedPolicy::MatchBasename(std::wstring_view pattern, std::wstring_view path) {
  return MatchGlob(pattern, PathPolicy::FoldCase(Basename(path)));
}

bool MaskedPolicy::IsMasked(std::wstring_view absolute_path,
                            std::wstring_view workspace_root) const {
  const std::wstring folded_path = PathPolicy::FoldCase(absolute_path);
  const std::wstring relative = PathPolicy::FoldCase(RelativeTo(absolute_path, workspace_root));

  for (const std::wstring& pattern : patterns_) {
    if (ContainsSeparator(pattern)) {
      if (Match(pattern, folded_path)) return true;
      if (relative != folded_path && Match(pattern, relative)) return true;
    } else if (MatchBasename(pattern, absolute_path)) {
      return true;
    }
  }
  return false;
}

std::vector<std::wstring> MaskedPolicy::DefaultPatterns() {
  return {
      L".env",
      L".env.*",
      L"**\\.ssh\\**",
      L"**\\id_rsa",
      L"**\\id_ed25519",
      L"**\\.git\\config",
      L"**\\credentials",
      L"**\\credentials.*",
      L"**\\Cookies",
      L"**\\Login Data",
      L"**\\*.pem",
      L"**\\*.pfx",
  };
}

}  // namespace rvm::security