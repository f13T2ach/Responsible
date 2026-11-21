#pragma once

#include <string>
#include <vector>

namespace rvm::server {

struct JailbreakOptions {
  std::wstring workspace_root;
  std::vector<std::wstring> tool_paths;
  std::wstring python_exe;
};

// Starts a real sandbox session and runs bounded escape probes through the
// same RunShell path the DSH shell bridge uses: read/write canaries outside the
// workspace and an exposed python interpreter exercising both operations.
// Canaries live in the host temp, the host profile and the workspace parent;
// nothing else on the host is touched. Returns 0 when every probe observed the
// expected isolation result, 1 when a probe failed, 2 on setup failure.
int RunJailbreakSmoke(const JailbreakOptions& options);

}  // namespace rvm::server