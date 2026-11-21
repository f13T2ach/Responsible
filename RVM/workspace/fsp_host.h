#pragma once

#include <memory>
#include <string>
#include <vector>

#include "core/result.h"

namespace rvm::workspace {

class UnionView;

struct MountOptions {
  std::wstring mount_point;              // "R:"
  std::wstring appcontainer_sid;         // optional SID granted full access
  std::vector<std::wstring> extra_sids;  // additional SIDs granted full access (SDDL form)
  std::wstring volume_label = L"RVM";
};

// Hosts a UnionView as a WinFsp drive (base read-only, diff writable,
// tombstones hidden). Mount and unmount must happen on the engine thread that
// owns the session; callbacks run on WinFsp dispatcher threads.
class MountedWorkspace {
 public:
  static core::Result<std::unique_ptr<MountedWorkspace>> Mount(UnionView* view,
                                                               const MountOptions& options);
  ~MountedWorkspace();

  MountedWorkspace(const MountedWorkspace&) = delete;
  MountedWorkspace& operator=(const MountedWorkspace&) = delete;

  const std::wstring& mount_point() const { return mount_point_; }
  void Unmount();

 private:
  struct Impl;
  MountedWorkspace(std::unique_ptr<Impl> impl, std::wstring mount_point);

  std::unique_ptr<Impl> impl_;
  std::wstring mount_point_;
};

}  // namespace rvm::workspace