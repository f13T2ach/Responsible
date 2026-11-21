#pragma once

#include <memory>

#include "runtime/backend.h"

namespace rvm::sandbox {

class SandboxBackend final : public runtime::IEnvironmentBackend {
 public:
  runtime::BackendKind kind() const override { return runtime::BackendKind::kSandbox; }
  runtime::BackendProbe Probe() const override;
  core::Result<std::unique_ptr<runtime::IEnvironment>> Create(
      const runtime::EnvironmentSpec& spec) override;
};

std::unique_ptr<runtime::IEnvironmentBackend> CreateSandboxBackend();

}  // namespace rvm::sandbox