#pragma once

#include <cstdint>
#include <memory>
#include <string>
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
  // AppContainer SID granted access for direct (NTFS) workspaces.
  std::wstring appcontainer_sid;
  // Overlay workspaces run under a restricted token instead: WinFsp volumes
  // reject AppContainer opens (winfsp#705), so DACL isolation does not apply.
  bool restricted_token = false;
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