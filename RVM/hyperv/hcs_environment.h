#pragma once

#include <string>

#include "hyperv/backend.h"
#include "hyperv/hcs_api.h"
#include "hyperv/hcs_layer.h"

namespace rvm::hyperv {

class HcsEnvironment final : public runtime::IEnvironment {
 public:
  HcsEnvironment(runtime::EnvironmentSpec spec, BaseLayerStore store);
  ~HcsEnvironment() override;

  HcsEnvironment(const HcsEnvironment&) = delete;
  HcsEnvironment& operator=(const HcsEnvironment&) = delete;

  core::Result<runtime::EnvironmentFacts> Start() override;
  core::Status Stop() override;
  core::Status Reset() override;
  runtime::EnvironmentPhase phase() const override { return phase_; }

 private:
  core::Status ReleaseSystem(bool graceful);
  core::Status RemoveScratch();

  runtime::EnvironmentSpec spec_;
  BaseLayerStore store_;
  HcsApi api_;
  HcsSystem system_ = nullptr;
  std::wstring scratch_path_;
  std::wstring system_id_;
  runtime::EnvironmentPhase phase_ = runtime::EnvironmentPhase::kAbsent;
};

}  // namespace rvm::hyperv