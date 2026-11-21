#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

#include "core/result.h"

namespace rvm::workspace {

struct UnionPaths {
  std::wstring base_root;
  std::wstring diff_root;
  std::wstring tombstone_root;
  std::wstring opaque_root;
};

struct UnionEntry {
  std::wstring relative;
  bool is_directory = false;
  bool from_diff = false;
  std::uint64_t size = 0;
  std::uint64_t last_write_time = 0;
};

// Read-only base overlaid with a writable diff and delete tombstones. Paths are
// workspace-relative ("", "src", "src/main.cpp"); the root is the empty string.
class UnionView {
 public:
  static core::Result<UnionView> Create(UnionPaths paths);

  const UnionPaths& paths() const { return paths_; }

  core::Result<UnionEntry> Stat(std::wstring_view relative) const;
  core::Result<std::vector<UnionEntry>> List(std::wstring_view relative) const;

  core::Status CreateEntry(std::wstring_view relative, bool directory);
  core::Status EnsureDirectory(std::wstring_view relative);
  core::Status RemoveEntry(std::wstring_view relative);
  core::Status Rename(std::wstring_view from, std::wstring_view to, bool replace);

  core::Result<std::wstring> DiffPathFor(std::wstring_view relative) const;
  core::Result<std::wstring> ResolvePath(std::wstring_view relative) const;
  core::Result<std::wstring> CopyUp(std::wstring_view relative) const;
  bool IsTombstoned(std::wstring_view relative) const;

 private:
  explicit UnionView(UnionPaths paths) : paths_(std::move(paths)) {}

  static core::Result<std::wstring> Normalize(std::wstring_view relative);
  bool HasAncestorTombstone(std::wstring_view relative) const;
  bool IsOpaque(std::wstring_view relative) const;
  core::Status ClearExactTombstone(std::wstring_view relative) const;
  core::Status RecordTombstone(std::wstring_view relative) const;
  core::Status RecordOpaque(std::wstring_view relative) const;
  core::Status CopyUpTree(std::wstring_view relative) const;
  core::Status EnsureDiffParent(std::wstring_view relative) const;

  std::wstring BasePath(std::wstring_view relative) const;
  std::wstring DiffPath(std::wstring_view relative) const;
  std::wstring TombstonePath(std::wstring_view relative) const;
  std::wstring OpaquePath(std::wstring_view relative) const;

  UnionPaths paths_;
};

}  // namespace rvm::workspace