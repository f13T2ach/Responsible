#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <utility>
#include <vector>

#include "core/result.h"
#include "rvm/v1/shell.pb.h"
#include "sandbox/appcontainer.h"
#include "server/session_manager.h"

namespace rvm::server {

struct ShellOutcome {
  std::uint64_t process_id = 0;
  std::uint64_t duration_ms = 0;
  bool output_truncated = false;
  bool error_truncated = false;
  sandbox::ProcessResult result;
};

// Execute one shell request inside a live session's confinement. The working
// directory is translated from the host workspace root onto the session's
// virtual root. The child environment is built by the engine (sandbox TEMP,
// clean PATH, user-exposed variables only); caller-provided environments are
// ignored. Cancellation is polled while the child runs and kills the whole job.
core::Result<ShellOutcome> RunShell(const ShellContext& context,
                                    const v1::ShellExecRequest& request,
                                    const std::function<bool()>& should_cancel);

// Keep only absolute local directories from the host PATH that are neither the
// workspace nor the session temp; BuildEnvironment uses this before appending.
std::vector<std::wstring> FilterHostPathEntries(const std::wstring& host_path,
                                                const std::wstring& workspace_path,
                                                const std::wstring& host_workspace_root,
                                                const std::wstring& temp_path);

}  // namespace rvm::server