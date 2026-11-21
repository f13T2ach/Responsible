#include "core/error.h"
#include "core/lifecycle.h"
#include "test_registry.h"

using rvm::core::EngineEvent;
using rvm::core::EngineState;
using rvm::core::EnvironmentEvent;
using rvm::core::EnvironmentState;
using rvm::core::ExecEvent;
using rvm::core::ExecState;
using rvm::core::WorkspaceEvent;
using rvm::core::WorkspaceState;

RVM_TEST(engine_state_machine_accepts_ready_lifecycle) {
  auto machine = rvm::core::MakeEngineStateMachine();
  RVM_CHECK(machine.state() == EngineState::kCreated);
  RVM_CHECK(machine.Apply(EngineEvent::kStart).ok());
  RVM_CHECK(machine.state() == EngineState::kStarting);
  RVM_CHECK(machine.Apply(EngineEvent::kReady).ok());
  RVM_CHECK(machine.state() == EngineState::kReady);
  RVM_CHECK(machine.Apply(EngineEvent::kBeginStop).ok());
  RVM_CHECK(machine.state() == EngineState::kStopping);
  RVM_CHECK(machine.Apply(EngineEvent::kStopComplete).ok());
  RVM_CHECK(machine.state() == EngineState::kStopped);
}

RVM_TEST(engine_state_machine_rejects_skipped_transitions) {
  auto machine = rvm::core::MakeEngineStateMachine();
  RVM_CHECK(machine.Apply(EngineEvent::kReady).ok() == false);
  RVM_CHECK(machine.state() == EngineState::kCreated);
  RVM_CHECK(machine.Apply(EngineEvent::kStart).ok());
  const auto status = machine.Apply(EngineEvent::kStopComplete);
  RVM_CHECK(!status.ok());
  RVM_CHECK(status.error().code == rvm::core::ErrorCode::kInvalidState);
  RVM_CHECK(machine.state() == EngineState::kStarting);
}

RVM_TEST(environment_state_machine_covers_boot_and_reset) {
  auto machine = rvm::core::MakeEnvironmentStateMachine();
  RVM_CHECK(machine.state() == EnvironmentState::kAbsent);
  RVM_CHECK(machine.Apply(EnvironmentEvent::kCreate).ok());
  RVM_CHECK(machine.Apply(EnvironmentEvent::kBoot).ok());
  RVM_CHECK(machine.Apply(EnvironmentEvent::kReady).ok());
  RVM_CHECK(machine.Apply(EnvironmentEvent::kBeginStop).ok());
  RVM_CHECK(machine.Apply(EnvironmentEvent::kStopComplete).ok());
  RVM_CHECK(machine.Apply(EnvironmentEvent::kReset).ok());
  RVM_CHECK(machine.state() == EnvironmentState::kAbsent);
}

RVM_TEST(exec_state_machine_is_terminal_after_exit) {
  auto machine = rvm::core::MakeExecStateMachine();
  RVM_CHECK(machine.Apply(ExecEvent::kStart).ok());
  RVM_CHECK(machine.Apply(ExecEvent::kExit).ok());
  RVM_CHECK(machine.state() == ExecState::kExited);
  RVM_CHECK(!machine.Apply(ExecEvent::kTimeout).ok());
  RVM_CHECK(machine.state() == ExecState::kExited);
}

RVM_TEST(workspace_state_machine_detects_conflicts) {
  auto machine = rvm::core::MakeWorkspaceStateMachine();
  RVM_CHECK(machine.Apply(WorkspaceEvent::kMarkDirty).ok());
  RVM_CHECK(machine.Apply(WorkspaceEvent::kBeginApply).ok());
  RVM_CHECK(machine.Apply(WorkspaceEvent::kConflict).ok());
  RVM_CHECK(machine.state() == WorkspaceState::kConflicted);
  RVM_CHECK(machine.Apply(WorkspaceEvent::kReset).ok());
  RVM_CHECK(machine.state() == WorkspaceState::kClean);
}