#pragma once

#include <string>
#include <vector>

namespace rvm::core {

enum class SandboxMode {
  kReadOnly,
  kWorkspaceWrite,
  kDangerFullAccess,
};

enum class ApprovalMode {
  kAsk,
  kNever,
};

struct SandboxPolicy {
  SandboxMode mode = SandboxMode::kWorkspaceWrite;
  std::vector<std::wstring> writable_roots;
  bool network_access = false;
  ApprovalMode approval = ApprovalMode::kAsk;
};

struct EngineConfig {
  std::wstring pipe_name;
};

}  // namespace rvm::core