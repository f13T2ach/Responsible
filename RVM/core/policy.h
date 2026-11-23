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
  // 沙箱强度测试：宿主借它切到 jailbreak 提示词；隔离本身不变。
  bool jailbreak_test = false;
};

struct EngineConfig {
  std::wstring pipe_name;
};

}  // namespace rvm::core