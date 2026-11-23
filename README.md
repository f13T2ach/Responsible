<div align="center">
<h1>Responsible</h1>

Taking responsibility for every model response.

A strict file-isolation harness for agents

<p>
  <a href="https://github.com/f13T2ach/Responsible/actions/workflows/build-release.yml"><img alt="build-release" src="https://github.com/f13T2ach/Responsible/actions/workflows/build-release.yml/badge.svg?branch=main"></a>
  <a href="https://github.com/f13T2ach/Responsible/commits/main"><img alt="last commit" src="https://img.shields.io/github/last-commit/f13T2ach/Responsible?branch=main"></a>
  <img alt="license: MIT" src="https://img.shields.io/badge/license-MIT-blue">
  <img alt="platform: Windows x64" src="https://img.shields.io/badge/platform-Windows%20x64-0078D4">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4">
</p>

<p>
  <b>English</b> | <a href="README.zh_CN.md">简体中文</a>
</p>
</div>

## Introduction

Responsible's core is DeepSeek Harness. It loads custom plugins to reroute the AI's instruction requests into the Responsible Virtual Machine (RVM), and pairs AppContainer-based toolchain penetration with independent workspaces to provide a safe agent-assisted coding environment. The aim is that neither a person nor an AI can break out of Responsible to read private data or mount a man-in-the-middle attack.

~~Responsible can install DSH plugins built specifically for it (RDsh) to define how sensitive tools that carry local configuration — Git, SSH, and the like — are invoked.~~

## Architecture

Responsible runs as three processes with clear boundaries, each replaceable on its own:

| Process | Language / runtime | Responsibility |
| --- | --- | --- |
| `Responsible.exe` | C# / WPF (net10.0-windows) | UI, session and workspace management, permission profile, RVM lifecycle |
| `RDshCore` (node) | DSH kernel | Model conversation, tool execution, the decision point for approvals and questions |
| `RvmEngine.exe` | C++ (CMake) | RVM sandbox: AppContainer identity, per-path ACLs, session volume, `subst` drive letters |

The channels between them:

- Host ↔ kernel: NDJSON JSON-RPC over stdio, plus a local HTTP bridge (`127.0.0.1:<port>` + `x-responsible-token`) that the frontend polls for approval, question, and task state
- Host ↔ engine: a named pipe, 24-byte header + `rvm.v1` protobuf
- Host ↔ frontend: WebView2 `postMessage`

The host injects the kernel plugins (`RDshPlugins/`) into the kernel process; they rewrite the system prompt, take over the sandbox policy, and hand approvals and questions to the UI. **Isolation is enforced by the RVM engine; the plugins only relay requests** — what stops an agent is always a Windows ACL and an AppContainer, never a prompt.

## Typical workflow

The cross-platform Responsible-CLI is still in development; only the Windows build is supported for now.

1. Clone the repository (.NET 10), configure the NuGet packages, and build — or download the GitHub Actions build from Releases.
2. In "Edit workspace", tick "RVM isolation protection" and create a Git index ("Full index (recommended for office)" for office directories, "Normal index" for code repositories).
3. Configure network and device penetration under Settings → RVM, then add the toolchains and environment variables to penetrate in the workspace settings.
4. Configure RDsh plugins to bring in the sensitive tools the AI may use.
5. Start working. When a round finishes, audit the session-volume diff, then Sync back to the host, Rollback the session volume, or use the CLI's `--flushback`.

## Build

Requires the .NET 10 SDK, Node.js 20+, MinGW-w64 + CMake + Ninja (RVM and RTray), and the WebView2 Runtime.

```powershell
# 1. Frontend (chat area and right sidebar)
cd RWebView; npm install; node build.mjs      # emits dist/index.html and dist/diff.html

# 2. RVM engine
cd RVM; cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release; cmake --build build
.\build\bin\rvm_tests.exe                     # self-test: 79 passed, 0 failed

# 3. Watchdog (optional; last-resort cleanup of drive letters and orphan processes)
cd RTray; cmake -S . -B build -G Ninja; cmake --build build

# 4. Host
cd Responsible; dotnet build                  # or dotnet publish -c Release
```

Once started, `Responsible.exe` launches `RDshCore` and `RvmEngine` on demand; `RTray.exe` is started by the host and watches the host process handle, so if the host is force-killed it clears the `subst` drive letters and any leftover child processes.

Release packaging, the disk traps that come with it, and the flow actually in use are documented in [BUILD-GUIDE.md](BUILD-GUIDE.md) (English) and [BUILD-GUIDE.zh_CN.md](BUILD-GUIDE.zh_CN.md) (简体中文).

## RDsh plugins

RDsh plugins exist to invoke sensitive tools. A plugin receives the Session that `rshell` passes in, audits it, and forwards it to the host machine — in other words, it implements a credential proxy.

Sensitive tools are complex (Git being one), so each needs a purpose-written RDsh plugin to define how it is invoked. Responsible also keeps one general-purpose plugin for forwarding a Session when no RDsh plugin is available: `runiverse`.

Note that `runiverse` is a stopgap: with human auditing out of the picture, an AI can use `runiverse` to take control of the host machine.

**Learn more: RDshPlugins/README.md**

## RVM virtual machine

RVM is not an image-driven virtual machine. It achieves file isolation through access control, but not kernel isolation. Enabling memory integrity protection and keeping up with Windows Update are hardening tasks the user is responsible for; RVM does not check these settings and does not block a session because of them — the risk is yours.

RVM keeps local snapshot archives that record the system configuration and workspace snapshots inside the VM: system configuration, package store entries, and authorization and session logs go to `system.json` / `system.log` / `sessions.log` / `audit.log`; the system layer itself is not writable (an AppContainer has no system write path), and Rollback restores only the workspace.

### RVM media

1. **RVM Infrastructure** — RVM neither copies nor links system files. The session infrastructure is made of an AppContainer identity and per-path ACL grants: the host user directory, credentials, and SSH and browser data are never authorized; only the session volume, the session Temp, explicitly authorized tool directories, and serial ports are. System components are still provided by Windows under AppContainer rules, so the implementation never links read-only system files.
2. **RVM Workspace** — The RVM workspace is a Git-indexed snapshot, not a clone of the original workspace: the engine selectively copies tracked files and non-ignored untracked files onto the local session volume according to the workspace's Git index, so dependency directories, build outputs, and caches never enter the snapshot. Later startups sync host changes incrementally, and in-sandbox modifications stay on the session volume. Without a Git index RVM refuses to create a session and does not fall back to scanning the whole directory. After auditing the diff, the user can Rollback (restores the session volume only) or Sync (writes back only non-conflicting items).
3. **RVM Toolchain** — RVM grants read/execute authorization to the tool directories the user specifies. When the AI invokes those tools the processes still run inside the session, and their output and side effects land on the session volume; the actual invocation of sensitive tools goes through RDsh plugins for credential proxying and auditing.

### RVM Penetrate

1. RVM lets you set Penetrate Devices: Settings → RVM enumerates only host serial (COM) devices. Once ticked, the engine adds a read/write ACE for that session's AppContainer identity to the device object's security descriptor and revokes it when the session stops. A device that cannot be opened, or a failed grant, makes session startup fail rather than pretend the device is exposed; peripherals you did not tick are not passed through as-is.
2. RVM lets you set Penetrate Network: off by default, in which case the session has no network capability at all. When on, only the `internetClient` outbound capability is granted; `privateNetworkClientServer` is not granted and there is no loopback exemption. Port masking is fixed semantics: host loopback ports and session inbound ports are mutually invisible, so a session can neither reach host service ports nor bind or listen inside the masked range; there is no per-port allowance. Network auditing samples the host TCP/UDP tables along the command process tree and writes newly observed endpoints to the session archive's `network.log`; sampling has an interval, so very short-lived connections can be missed.

### RVM CLI

RVM follows a server model. `--create-session <alias> <id> <absolute_path>` creates a workspace, and when `<absolute_path>` is omitted it creates an empty workspace. `--join <id>` enters an ssh-like CLI that can execute commands. `--terminate <id>` removes the workspace and the local state archives. `--flushback <id>` syncs changes inside the workspace back to the local disk. `--save <id>` saves the state of the given RVM id (system state and workspace state).

Implementation notes:

- The first invocation launches a detached engine daemon automatically (it does not occupy the current console); later commands reuse the same engine and token over the named pipe. State lives in `%LOCALAPPDATA%\Responsible\RVM\cli\` (`engine.ini`, `sessions\<id>.ini`, `workspaces\<id>`), and the session archive in `%LOCALAPPDATA%\Responsible\RVM\cli_<id>\`.
- `--create-session` requires `<absolute_path>` to be a standalone Git repository root: the engine reads the index with `git ls-files` to determine the snapshot scope. Without an index it refuses to create the session and asks you to `git init` first; an office directory can build a full index with `git add -A`, and RVM never scans an entire disk. An empty workspace gets an empty repository with nothing but identity and work logs, and no host-side content.
- Network and device penetration share `%APPDATA%\Responsible\rvm-policy.json` with the settings page and are fixed when the session is created.
- `--join`'s `cd` allows paths on the session volume only; commands run inside the sandbox through `cmd.exe`; `exit`/`quit` disconnects without destroying the session.
- `--flushback` prints the audit diff first, then syncs non-conflicting items; when conflicts exist it returns exit code 2, and files modified on the host are never overwritten. An empty workspace has no host-side content and points you to `--save` instead.
- `--terminate` closes the running engine session first; if closing fails it keeps the archives, so the session volume is not deleted while the sandbox is still writing to it.
- `--save` produces `<archive>\snapshots\<timestamp>\`: it copies `system.json`, `system.log`, `sessions.log`, `audit.log`, `network.log`, `seed.manifest`, `baseline.manifest`, and `clone.ready`, and writes `workspace.manifest` (bounded; marked `truncated` on timeout or overflow) and the session runtime's `workspace.diff`. It performs no host scan and does not copy the session volume itself.

**Learn more: RVM/README.md**

## RWebView

RWebView implements part of the visual UI.

**Learn more: RWebView/README.md**

## Features

- **RVM isolated sessions**: a workspace is selectively snapshotted onto the session volume according to its Git index; commands and file I/O land on the session volume and a dedicated temp. The host user directory, credentials, and SSH/browser data are never authorized
- **Permission tier**: an RVM session has exactly one tier, "RVM isolation protection"; before the session volume is mounted the dropdown is greyed out and the input box cannot send, and both are released once it is mounted
- **Approvals**: when the model requests escalation to `danger-full-access`, a card appears (Deny / Allow once); RVM sessions deny outright instead of bothering the user
- **Questions (`ask_user_question`)**: a card at the bottom of the chat area, supporting single choice, multiple choice, custom text, and the countdown of the waiting window the model supplies
- **Change cards and review**: each round's changes are collapsed into one card; open it to see a diff with line numbers on both sides in the right sidebar (side-by-side and single-column, switchable)
- **Right sidebar**: a top button expands it; the empty state is a landing page, and with content it becomes tabs (review / workspace files / start)
- **Bottom status bar**: shows RVM runtime state only — readiness, session volume mount point, network, devices

## Configuration and logs

Configuration files:

- `%APPDATA%\Responsible\rvm-policy.json`: `networkEnabled` / `devicesEnabled` / `devices` / `jailbreakTest`
- `%APPDATA%\Responsible\`: `workspaces.json`, `conversations.json`, `providers.json`, `theme.json`, `acl-authorizations.json`

`jailbreakTest` (sandbox strength test) defaults to `false`. When on, it only replaces the system prompt with “you may actively attack the isolation and faithfully record the raw commands and errors”; **not one sandbox policy is relaxed** — ACLs, drive mapping, session volume scope, and the network switch all keep their original values. Its purpose is to make “how strong the isolation is” verifiable, not to make the model trusted.

Runtime data:

| Path | Contents |
| --- | --- |
| `%LOCALAPPDATA%\Responsible\logs\` | `kernel.log` (kernel journal, including plugin registration, approvals, and questions), `rvm-session.log`, `rvm-engine.log`, `webview.log`, `approval.log`, `questions.log`, `crash.log` |
| `%LOCALAPPDATA%\Responsible\RVM\volumes\<key>\` | the session volume (mounted as `R:\<key>`) |
| `%APPDATA%\Responsible\changes\<sessionId>.jsonl` | per-round change records (the source of cards and diffs after a restart) |

## Repository layout

```
Responsible/     WPF host: UI, sessions, workspaces, permissions, RVM lifecycle
RDshCore/        DSH kernel (node): conversation, tools, the approval and question decision points
RDshPlugins/     Plugins injected into the kernel: system prompt, RVM sandbox takeover, approval/question bridge
RVM/             RVM engine (C++/CMake): AppContainer, ACLs, session volume, subst
RWebView/        Chat area and right sidebar frontend (TypeScript + React, bundled with esbuild)
RTray/           Watchdog (C++): clears drive letters and orphan processes after an abnormal host exit
```

## Contributing

Read [CONTRIBUTE.md](CONTRIBUTE.md) before opening an issue or a pull request. It states the isolation contract a change may not weaken, the build and test commands, the per-component expectations, and the pull-request body structure that every PR must follow.

## License and disclaimer

License: MIT

Disclaimer: choosing Responsible means you consider the strength of this solution and its potential risks acceptable. Beyond the liability exclusion the MIT license requires, Responsible accepts no liability for any loss caused by, including but not limited to, 0day vulnerabilities or technical defects. Responsible itself does not provide any AI compute vendor service.
