#pragma once

#include "core/state_machine.h"

namespace rvm::core {

enum class EngineState {
  kCreated,
  kStarting,
  kReady,
  kDegraded,
  kStopping,
  kStopped,
  kFailed,
};

enum class EngineEvent {
  kStart,
  kReady,
  kDegrade,
  kBeginStop,
  kStopComplete,
  kFail,
};

using EngineStateMachine = StateMachine<EngineState, EngineEvent>;
EngineStateMachine MakeEngineStateMachine();

enum class EnvironmentState {
  kAbsent,
  kCreating,
  kBooting,
  kReady,
  kStopping,
  kStopped,
  kFailed,
};

enum class EnvironmentEvent {
  kCreate,
  kBoot,
  kReady,
  kBeginStop,
  kStopComplete,
  kReset,
  kFail,
};

using EnvironmentStateMachine = StateMachine<EnvironmentState, EnvironmentEvent>;
EnvironmentStateMachine MakeEnvironmentStateMachine();

enum class ExecState {
  kQueued,
  kRunning,
  kExited,
  kTimedOut,
  kCancelled,
  kFailed,
};

enum class ExecEvent {
  kStart,
  kExit,
  kTimeout,
  kCancel,
  kFail,
};

using ExecStateMachine = StateMachine<ExecState, ExecEvent>;
ExecStateMachine MakeExecStateMachine();

enum class WorkspaceState {
  kClean,
  kDirty,
  kApplying,
  kApplied,
  kConflicted,
};

enum class WorkspaceEvent {
  kMarkDirty,
  kBeginApply,
  kApplyComplete,
  kConflict,
  kReset,
};

using WorkspaceStateMachine = StateMachine<WorkspaceState, WorkspaceEvent>;
WorkspaceStateMachine MakeWorkspaceStateMachine();

}  // namespace rvm::core