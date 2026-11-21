#pragma once

#include <cstdint>
#include <memory>
#include <string>
#include <utility>
#include <vector>

#include "core/policy.h"
#include "core/result.h"

namespace rvm::runtime {

enum class BackendKind {
  kHcs,
  kSandbox,
};

enum class EnvironmentPhase {
  kAbsent,
  kCreating,
  kBooting,
  kReady,
  kStopping,
  kStopped,
  kFailed,
};

// A tool the user authorized once. The capability SID owns the persistent
// DACL grant; the session token must list it to use the tool.
struct ToolAccess {
  std::wstring path;
  std::wstring capability_sid;
};

struct EnvironmentSpec {
  std::wstring session_id;
  // Per-workspace storage name; empty keeps the legacy per-session layout.
  std::wstring storage_key;
  std::wstring workspace_root;
  std::wstring guest_workspace_path = L"C:\\RVM\\Workspace";
  std::vector<std::wstring> masked_patterns;
  core::SandboxPolicy policy;
  std::uint32_t memory_mb = 2048;
  std::uint32_t vcpu_count = 2;
  bool allow_base_layer_creation = false;
  // Expose the workspace through the WinFsp overlay drive instead of handing
  // out the real directory. Empty overlay_drive picks the first free letter.
  bool overlay_workspace = false;
  std::wstring overlay_drive;
  // User-exposed environment (name/value) and tool directories. Tool dirs are
  // granted read/execute to the sandbox identity and prepended to PATH.
  std::vector<std::pair<std::wstring, std::wstring>> exposed_environment;
  std::vector<std::wstring> tool_paths;
  std::vector<ToolAccess> tools;
};

struct EnvironmentFacts {
  std::wstring windows_build;
  std::uint32_t memory_mb = 0;
  std::uint32_t vcpu_count = 0;
  bool vbs_enabled = false;
  bool hvci_enabled = false;
  bool network_enabled = false;
  std::wstring workspace_path;  // where the session sees the workspace
  // Session-scoped temp directory handed to shell children via TMP/TEMP/TMPDIR.
  std::wstring temp_path;
  // AppContainer SID granted access to the workspace, temp and exposed tools.
  std::wstring appcontainer_sid;
  // Only set when the host falls back to a restricted token (no DACL isolation).
  bool restricted_token = false;
  std::vector<std::pair<std::wstring, std::wstring>> exposed_environment;
  std::vector<std::wstring> tool_paths;
  std::vector<ToolAccess> tools;
};

class IEnvironment {
 public:
  virtual ~IEnvironment() = default;

  virtual core::Result<EnvironmentFacts> Start() = 0;
  virtual core::Status Stop() = 0;
  virtual core::Status Reset() = 0;
  virtual EnvironmentPhase phase() const = 0;
};

struct BackendProbe {
  bool ready = false;
  std::string detail;

  bool ok() const { return ready; }
};

class IEnvironmentBackend {
 public:
  virtual ~IEnvironmentBackend() = default;

  virtual BackendKind kind() const = 0;
  virtual BackendProbe Probe() const = 0;
  virtual core::Result<std::unique_ptr<IEnvironment>> Create(const EnvironmentSpec& spec) = 0;
};

}  // namespace rvm::runtime