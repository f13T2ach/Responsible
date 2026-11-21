#pragma once

#include "hyperv/backend.h"

namespace rvm::hyperv {

class HcsBackend final : public runtime::IEnvironmentBackend {
 public:
  runtime::BackendKind kind() const override { return runtime::BackendKind::kHcs; }
  runtime::BackendProbe Probe() const override;
  core::Result<std::unique_ptr<runtime::IEnvironment>> Create(
      const runtime::EnvironmentSpec& spec) override;
};

}  // namespace rvm::hyperv