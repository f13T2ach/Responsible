#pragma once

#include <string>

#include "core/result.h"

namespace rvm::hyperv {

struct BaseLayerInfo {
  std::wstring path;
  std::string id;
  std::string windows_build;
};

// Host-derived read-only base OS layer store.
// Layers are generated from host file metadata by HcsSetupBaseOSLayer during an
// explicit, elevated preparation step. Sessions never create or mutate them.
class BaseLayerStore {
 public:
  explicit BaseLayerStore(std::wstring root);

  static BaseLayerStore Default();
  static std::wstring DefaultRoot();
  static std::string CurrentWindowsBuild();
  static bool IsProcessElevated();

  const std::wstring& root() const { return root_; }

  core::Result<BaseLayerInfo> Find() const;
  core::Result<BaseLayerInfo> Ensure(bool allow_create) const;

  core::Result<std::wstring> PrepareScratch(const std::string& session_id) const;
  core::Status RemoveScratch(const std::wstring& scratch_path) const;

 private:
  std::wstring base_root() const;
  std::wstring sessions_root() const;

  std::wstring root_;
};

}  // namespace rvm::hyperv