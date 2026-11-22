#pragma once

namespace rvm::client {

// `RvmEngine --shell-bridge --pipe <name> --session <id> [--token <token>]
// [--timeout-ms N] -- <argv...>`.
// Connects to the running engine as this process (token from --token, else
// RESPONSIBLE_RVM_TOKEN), executes argv inside the session's confinement, and
// mirrors stdout/stderr and the exit code. Diagnostics use the
// RVM_SANDBOX_UNAVAILABLE marker so the caller can classify runner failure.
int RunShellBridge(int argc, wchar_t** argv);

}  // namespace rvm::client