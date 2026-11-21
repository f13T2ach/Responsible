#include "core/engine.h"

#include <utility>

#include "core/log.h"
#include "environment/availability.h"

namespace rvm::core {

Engine::Engine(EngineConfig config, std::unique_ptr<environment::IAvailabilityProbe> probe)
    : config_(std::move(config)),
      probe_(std::move(probe)),
      machine_(MakeEngineStateMachine()) {
  if (!probe_) probe_ = std::make_unique<environment::SystemAvailabilityProbe>();
}

Status Engine::Initialize() {
  Status status = machine_.Apply(EngineEvent::kStart);
  if (!status.ok()) return status;

  availability_ = probe_->Probe();
  if (availability_.rvm_available()) {
    machine_.Apply(EngineEvent::kReady);
    LogInfo("engine", "RVM is available");
    return Status::Ok();
  }

  machine_.Apply(EngineEvent::kDegrade);
  LogWarn("engine", std::string("RVM is unavailable: ") + availability_.reason());
  return Status::Ok();
}

Status Engine::BeginStop() {
  if (machine_.state() == EngineState::kStopped) return Status::Ok();
  return machine_.Apply(EngineEvent::kBeginStop);
}

Status Engine::CompleteStop() {
  if (machine_.state() == EngineState::kStopped) return Status::Ok();
  return machine_.Apply(EngineEvent::kStopComplete);
}

}  // namespace rvm::core