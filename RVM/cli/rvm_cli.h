#pragma once

namespace rvm::cli {

// Server-paradigm workspace CLI:
//   RvmEngine --create-session <alias> <id> [<absolute_path>]
//   RvmEngine --join <id>
//   RvmEngine --terminate <id>
//   RvmEngine --flushback <id>
//   RvmEngine --save <id>
// Sessions survive between invocations: the first command starts a detached
// engine daemon and records its pipe/token under %LOCALAPPDATA%\Responsible\RVM\cli.
int Run(int argc, wchar_t** argv);

bool IsCommand(const wchar_t* argument);

}  // namespace rvm::cli