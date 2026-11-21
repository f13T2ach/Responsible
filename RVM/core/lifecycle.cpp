#include "core/lifecycle.h"

namespace rvm::core {

EngineStateMachine MakeEngineStateMachine() {
  return EngineStateMachine(EngineState::kCreated, {
      {EngineState::kCreated, EngineEvent::kStart, EngineState::kStarting},
      {EngineState::kCreated, EngineEvent::kFail, EngineState::kFailed},
      {EngineState::kStarting, EngineEvent::kReady, EngineState::kReady},
      {EngineState::kStarting, EngineEvent::kDegrade, EngineState::kDegraded},
      {EngineState::kStarting, EngineEvent::kFail, EngineState::kFailed},
      {EngineState::kStarting, EngineEvent::kBeginStop, EngineState::kStopping},
      {EngineState::kReady, EngineEvent::kDegrade, EngineState::kDegraded},
      {EngineState::kReady, EngineEvent::kBeginStop, EngineState::kStopping},
      {EngineState::kReady, EngineEvent::kFail, EngineState::kFailed},
      {EngineState::kDegraded, EngineEvent::kBeginStop, EngineState::kStopping},
      {EngineState::kDegraded, EngineEvent::kFail, EngineState::kFailed},
      {EngineState::kStopping, EngineEvent::kStopComplete, EngineState::kStopped},
      {EngineState::kStopping, EngineEvent::kFail, EngineState::kFailed},
  });
}

EnvironmentStateMachine MakeEnvironmentStateMachine() {
  return EnvironmentStateMachine(EnvironmentState::kAbsent, {
      {EnvironmentState::kAbsent, EnvironmentEvent::kCreate, EnvironmentState::kCreating},
      {EnvironmentState::kCreating, EnvironmentEvent::kBoot, EnvironmentState::kBooting},
      {EnvironmentState::kCreating, EnvironmentEvent::kFail, EnvironmentState::kFailed},
      {EnvironmentState::kBooting, EnvironmentEvent::kReady, EnvironmentState::kReady},
      {EnvironmentState::kBooting, EnvironmentEvent::kFail, EnvironmentState::kFailed},
      {EnvironmentState::kReady, EnvironmentEvent::kBeginStop, EnvironmentState::kStopping},
      {EnvironmentState::kReady, EnvironmentEvent::kFail, EnvironmentState::kFailed},
      {EnvironmentState::kStopping, EnvironmentEvent::kStopComplete, EnvironmentState::kStopped},
      {EnvironmentState::kStopping, EnvironmentEvent::kFail, EnvironmentState::kFailed},
      {EnvironmentState::kStopped, EnvironmentEvent::kReset, EnvironmentState::kAbsent},
      {EnvironmentState::kFailed, EnvironmentEvent::kReset, EnvironmentState::kAbsent},
  });
}

ExecStateMachine MakeExecStateMachine() {
  return ExecStateMachine(ExecState::kQueued, {
      {ExecState::kQueued, ExecEvent::kStart, ExecState::kRunning},
      {ExecState::kQueued, ExecEvent::kCancel, ExecState::kCancelled},
      {ExecState::kQueued, ExecEvent::kFail, ExecState::kFailed},
      {ExecState::kRunning, ExecEvent::kExit, ExecState::kExited},
      {ExecState::kRunning, ExecEvent::kTimeout, ExecState::kTimedOut},
      {ExecState::kRunning, ExecEvent::kCancel, ExecState::kCancelled},
      {ExecState::kRunning, ExecEvent::kFail, ExecState::kFailed},
  });
}

WorkspaceStateMachine MakeWorkspaceStateMachine() {
  return WorkspaceStateMachine(WorkspaceState::kClean, {
      {WorkspaceState::kClean, WorkspaceEvent::kMarkDirty, WorkspaceState::kDirty},
      {WorkspaceState::kClean, WorkspaceEvent::kReset, WorkspaceState::kClean},
      {WorkspaceState::kDirty, WorkspaceEvent::kBeginApply, WorkspaceState::kApplying},
      {WorkspaceState::kDirty, WorkspaceEvent::kReset, WorkspaceState::kClean},
      {WorkspaceState::kApplying, WorkspaceEvent::kApplyComplete, WorkspaceState::kApplied},
      {WorkspaceState::kApplying, WorkspaceEvent::kConflict, WorkspaceState::kConflicted},
      {WorkspaceState::kApplying, WorkspaceEvent::kReset, WorkspaceState::kClean},
      {WorkspaceState::kApplied, WorkspaceEvent::kMarkDirty, WorkspaceState::kDirty},
      {WorkspaceState::kApplied, WorkspaceEvent::kReset, WorkspaceState::kClean},
      {WorkspaceState::kConflicted, WorkspaceEvent::kReset, WorkspaceState::kClean},
      {WorkspaceState::kConflicted, WorkspaceEvent::kMarkDirty, WorkspaceState::kDirty},
  });
}

}  // namespace rvm::core