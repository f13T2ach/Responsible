#include "security/path_policy.h"

#include <windows.h>

#include <algorithm>
#include <cwctype>
#include <unordered_set>

#include "core/error.h"

namespace rvm::security {
namespace {

bool IsDriveAbsolute(std::wstring_view path) {
  return path.size() >= 3 && std::iswalpha(path[0]) != 0 && path[1] == L':' &&
         (path[2] == L'\\' || path[2] == L'/');
}

bool IsReservedName(std::wstring_view segment) {
  const std::wstring folded = PathPolicy::FoldCase(segment);
  const std::size_t dot = folded.find(L'.');
  const std::wstring base = folded.substr(0, dot);
  static const std::unordered_set<std::wstring> reserved = {
      L"con",  L"prn",  L"aux",  L"nul",  L"com1", L"com2", L"com3", L"com4", L"com5",
      L"com6", L"com7", L"com8", L"com9", L"lpt1", L"lpt2", L"lpt3", L"lpt4", L"lpt5",
      L"lpt6", L"lpt7", L"lpt8", L"lpt9", L"conin$", L"conout$",
  };
  return reserved.count(base) != 0;
}

core::Status ValidateSegment(std::wstring_view segment) {
  if (segment.empty()) {
    return core::Status::Failure(core::InvalidArgument("path contains an empty segment"));
  }
  if (segment.back() == L'.' || segment.back() == L' ') {
    return core::Status::Failure(
        core::InvalidArgument("path segment may not end with a dot or space"));
  }
  if (IsReservedName(segment)) {
    return core::Status::Failure(core::InvalidArgument("path segment is a reserved device name"));
  }
  return core::Status::Ok();
}

core::Result<std::wstring> NormalizeAbsolute(std::wstring_view path, std::size_t max_length) {
  if (!IsDriveAbsolute(path)) {
    return core::InvalidArgument("path must be absolute");
  }
  if (path.size() > max_length) {
    return core::InvalidArgument("path exceeds the maximum length");
  }

  std::wstring drive;
  drive.push_back(static_cast<wchar_t>(std::towupper(path[0])));
  drive.append(L":\\");

  std::vector<std::wstring> segments;
  std::wstring current;
  for (std::size_t i = 3; i <= path.size(); ++i) {
    if (i == path.size() || path[i] == L'\\' || path[i] == L'/') {
      if (!current.empty()) {
        if (current == L".") {
          current.clear();
          continue;
        }
        if (current == L"..") {
          if (segments.empty()) {
            return core::Error(core::ErrorCode::kPathOutOfScope,
                               "path escapes its drive root");
          }
          segments.pop_back();
          current.clear();
          continue;
        }
        core::Status status = ValidateSegment(current);
        if (!status.ok()) return status.error();
        segments.push_back(current);
        current.clear();
      }
    } else {
      current.push_back(path[i]);
    }
  }

  std::wstring result = drive;
  for (std::size_t i = 0; i < segments.size(); ++i) {
    if (i != 0) result.push_back(L'\\');
    result.append(segments[i]);
  }
  if (result.size() > max_length) {
    return core::InvalidArgument("path exceeds the maximum length");
  }
  return core::Ok(std::move(result));
}

}  // namespace

PathPolicy::PathPolicy(PathPolicyOptions options) : options_(options) {}

std::wstring PathPolicy::FoldCase(std::wstring_view value) {
  if (value.empty()) return {};
  const int needed = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, value.data(),
                                   static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr, 0);
  if (needed <= 0) return std::wstring(value);
  std::wstring folded(static_cast<std::size_t>(needed), L'\0');
  const int written =
      LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, value.data(),
                    static_cast<int>(value.size()), folded.data(), needed, nullptr, nullptr, 0);
  if (written <= 0) return std::wstring(value);
  folded.resize(static_cast<std::size_t>(written));
  return folded;
}

core::Result<std::wstring> PathPolicy::Normalize(std::wstring_view input,
                                                 std::wstring_view cwd) const {
  if (input.empty()) {
    return core::InvalidArgument("path must not be empty");
  }
  if (input.size() > options_.max_path_length) {
    return core::InvalidArgument("path exceeds the maximum length");
  }

  std::wstring path(input);
  std::replace(path.begin(), path.end(), L'/', L'\\');

  for (wchar_t ch : path) {
    if (ch < 0x20) {
      return core::InvalidArgument("path contains a control character");
    }
    if (ch == L'<' || ch == L'>' || ch == L'"' || ch == L'|' || ch == L'?' ||
        ch == L'*') {
      return core::InvalidArgument("path contains a character that is not valid in a file name");
    }
  }

  if (path.size() >= 2 && path[0] == L'\\' && path[1] == L'\\') {
    return core::InvalidArgument("UNC and device paths are not allowed");
  }

  for (std::size_t i = 0; i < path.size(); ++i) {
    if (path[i] != L':') continue;
    const bool drive_prefix = i == 1 && std::iswalpha(path[0]) != 0;
    if (!drive_prefix) {
      return core::InvalidArgument("alternate data streams are not allowed");
    }
  }

  if (path.size() >= 2 && std::iswalpha(path[0]) != 0 && path[1] == L':' &&
      (path.size() == 2 || path[2] != L'\\')) {
    return core::InvalidArgument("drive-relative paths are not allowed");
  }

  if (IsDriveAbsolute(path)) {
    return NormalizeAbsolute(path, options_.max_path_length);
  }

  if (!IsDriveAbsolute(cwd)) {
    return core::InvalidArgument("working directory must be an absolute drive path");
  }
  core::Result<std::wstring> normalized_cwd = NormalizeAbsolute(cwd, options_.max_path_length);
  if (!normalized_cwd.ok()) return normalized_cwd.error();

  std::wstring combined;
  if (!path.empty() && path[0] == L'\\') {
    combined.push_back(normalized_cwd.value()[0]);
    combined.append(L":");
    combined.append(path);
  } else {
    combined = normalized_cwd.value();
    if (!combined.empty() && combined.back() != L'\\') combined.push_back(L'\\');
    combined.append(path);
  }
  return NormalizeAbsolute(combined, options_.max_path_length);
}

bool PathPolicy::IsWithin(std::wstring_view path, std::wstring_view root) {
  if (path.empty() || root.empty()) return false;
  const std::wstring folded_path = FoldCase(path);
  std::wstring folded_root = FoldCase(root);
  while (folded_root.size() > 3 && folded_root.back() == L'\\') folded_root.pop_back();
  if (folded_path == folded_root) return true;

  if (folded_root.back() != L'\\') folded_root.push_back(L'\\');
  return folded_path.size() > folded_root.size() &&
         folded_path.compare(0, folded_root.size(), folded_root) == 0;
}

core::Status PathPolicy::Authorize(std::wstring_view normalized,
                                   const std::vector<std::wstring>& roots) const {
  if (roots.empty()) {
    return core::Status::Failure(
        core::Error(core::ErrorCode::kPathOutOfScope, "no authorized roots are configured"));
  }
  for (const std::wstring& root : roots) {
    core::Result<std::wstring> normalized_root = Normalize(root, root);
    if (!normalized_root.ok()) {
      return core::Status::Failure(
          core::Error(core::ErrorCode::kInvalidArgument, "authorized root is not canonical",
                      false, normalized_root.error().message));
    }
    if (IsWithin(normalized, normalized_root.value())) return core::Status::Ok();
  }
  return core::Status::Failure(core::Error(core::ErrorCode::kPathOutOfScope,
                                           "path is outside the authorized roots"));
}

}  // namespace rvm::security