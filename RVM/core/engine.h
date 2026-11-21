#pragma once

#include <memory>
#include <string>

#include "core/lifecycle.h"
#include "core/policy.h"
#include "environment/availability.h"

namespace rvm::core {

class Engine {
 public:
  explicit Engine(EngineConfig config,
                  std::unique_ptr<environment::IAvailabilityProbe> probe = nullptr);

  Status Initialize();
  Status BeginStop();
  Status CompleteStop();

  EngineState state() const { return machine_.state(); }
  const EngineConfig& config() const { return config_; }
  const environment::AvailabilityReport& availability() const { return availability_; }

 private:
  EngineConfig config_;
  std::unique_ptr<environment::IAvailabilityProbe> probe_;
  environment::AvailabilityReport availability_;
  EngineStateMachine machine_;
};

}  // namespace rvm::core