# Contributing to Responsible

Responsible exists to make one claim testable: an agent working inside it cannot reach host data, host credentials, or the host network without a grant the user made on purpose. Every contribution is judged on two questions — is the change useful, and does it leave that claim provable?

This document is the contribution contract. It covers what to send, how to build and verify it, what each component expects, and the exact structure a pull request body must have. It applies to the whole repository: the WPF host, the RVM engine, the kernel plugins, the embedded frontend, the watchdog, and CI.

## Contents

- [Before you start](#before-you-start)
- [The isolation contract](#the-isolation-contract)
- [Environment](#environment)
- [Build and verify](#build-and-verify)
- [Code in each component](#code-in-each-component)
- [Commits](#commits)
- [Pull request body](#pull-request-body)
- [Review and merge](#review-and-merge)
- [Documentation](#documentation)
- [Licensing](#licensing)

## Before you start

- Read the documents that own the area you are touching: [README.md](README.md) for the architecture, [BUILD-GUIDE.md](BUILD-GUIDE.md) before any packaging work, [RVM/README.md](RVM/README.md) for the engine's hard constraints, and [RDshCore/AGENTS.md](RDshCore/AGENTS.md) for kernel conventions.
- Open an issue before any non-trivial change. Describe the problem, the behavior you want, and the boundary you expect to touch. A short design discussion saves a large rejected patch.
- Changes that only fix a typo, a broken link, or an obvious bug can go straight to a pull request.
- Do not edit files inside `RDshCore/`. It is a git submodule pinned to [deepseek-ai/deepseek-harness](https://github.com/deepseek-ai/deepseek-harness); kernel changes are made upstream and this repository only moves the submodule pointer, in a dedicated pull request that states the old and new commit. `RDshPlugins/` is the integration seam and is where Responsible-specific kernel behavior belongs.
- Never send a vulnerability as a public issue or pull request. Use GitHub's private vulnerability reporting (Security tab → Report a vulnerability). If that channel is unavailable, open an issue that asks for a private channel and contains no technical detail. A useful report includes the build you tested, the exact boundary that was crossed, reproduction steps, and expected versus observed behavior. Reports are acknowledged within seven days; please allow up to 90 days for a fix and coordinated disclosure.

## The isolation contract

These invariants define the product. A pull request may strengthen them or leave them alone; it may not weaken them. Anything that touches one of these points must say so in its body and will be reviewed line by line.

1. **Authorized paths.** A session may reach only its session volume, its session temp (`.rvm_temp`), tool directories the user explicitly authorized, and serial devices the user explicitly selected. The host user directory, credential stores, SSH keys and agent, and browser profile data are never authorized, copied, or linked.
2. **No system-file shortcuts.** The engine does not copy or link system files into the sandbox, and does not re-expose the host filesystem through a new backend. System components stay provided by Windows under AppContainer rules.
3. **Network is off by default.** When enabled it grants only `internetClient`. `privateNetworkClientServer` is never granted, there is no loopback exemption, port masking is fixed, and no per-port allowlist may be introduced.
4. **Devices are opt-in and revocable.** A serial device is granted by adding an ACE for that session's AppContainer identity and is revoked when the session stops. If the device cannot be opened or the grant fails, session startup fails; it must never pretend the device was exposed.
5. **Fail closed, never downgrade.** Every unknown or failed state means deny. If isolation cannot be established the engine returns `RVM_UNAVAILABLE` and the host tells the user; a session never silently falls back to a weaker sandbox.
6. **No host execution from the kernel side.** The kernel and its plugins never spawn host processes. Commands are forwarded to `RvmEngine --shell-bridge` and run inside the session.
7. **`jailbreakTest` changes the prompt only.** It may not relax a single policy: ACLs, drive mapping, session volume scope, and network settings keep their values.
8. **The system layer is read-only.** Rollback restores the workspace and nothing else.
9. **Audit stays complete.** New state that affects the session goes into `system.json`, `system.log`, `sessions.log`, `audit.log`, or `network.log` as appropriate. Fields are added, not dropped.

A feature that needs more access needs an explicit user-facing switch that defaults to off, a documented consequence, and a test proving the denied case still denies.

## Environment

| Requirement | Version used by the project |
| --- | --- |
| Windows | 10 or 11, x64 |
| .NET SDK | 10 |
| Node.js | 24 in CI (20+ works for the frontend and kernel) |
| C++ toolchain | MinGW-w64 GCC with C++20 support (verified with GCC 16) |
| CMake / Ninja | CMake ≥ 4.3, Ninja |
| Runtime | WebView2 Runtime |
| Shell | PowerShell 7 recommended; `build-release.ps1` declares PowerShell 5.1 |

Do not run the full release packaging locally. The complete pipeline lives in `.github/workflows/build-release.yml` and runs on GitHub Actions; building it on a workstation is what [BUILD-GUIDE.md](BUILD-GUIDE.md) exists to prevent. Locally you build one component at a time.

## Build and verify

Host (WPF):

```powershell
dotnet build Responsible.slnx      # or: cd Responsible; dotnet build
```

Frontend (TypeScript + React, esbuild):

```powershell
cd RWebView
npm install
npm run typecheck
npm run build                      # writes dist/index.html and dist/diff.html
```

`RWebView/dist/` is tracked on purpose: the host loads the built files, so a frontend change is not complete until the regenerated `dist/` files are committed.

Engine (C++/CMake):

```powershell
cmake -S RVM -B RVM/build -G Ninja -DCMAKE_BUILD_TYPE=Debug
cmake --build RVM/build
ctest --test-dir RVM/build --output-on-failure
```

The baseline is **79 passed, 0 failed**. `ctest` must be at least as green after your change, and behavior changes need a test registered through `RVM/tests/test_registry.h` following the existing files in `RVM/tests/`.

Watchdog (C++):

```powershell
cmake -S RTray -B RTray/build -G Ninja -DCMAKE_BUILD_TYPE=Debug
cmake --build RTray/build
```

Committed native binaries: `natives/win-x64/` (`RvmEngine.exe`, `RTray.exe`, and the three MinGW runtime DLLs) ships with the repository and is what CI packages. A pull request that changes C++ must rebuild it and commit the result:

```powershell
.\build-release.ps1
```

Note that the script configures the engine with `-DRVM_BUILD_TESTS=OFF`; run `ctest` in your own `RVM/build` tree instead. State the toolchain versions used (`g++ --version`, `cmake --version`, `ninja --version`) in the pull request body.

What verification each area owes:

| Area | Evidence a pull request must contain |
| --- | --- |
| `RVM/` | `ctest` output; for isolation or ACL changes, the reproduction command and the audited log lines from a real session |
| `Responsible/`, `Responsible.MoeViews/` | warning-free build; numbered manual steps for the UI flow, with screenshots |
| `RDshPlugins/` | kernel starts with the patch (`dsh --profile sdk --patch RDshPlugins/responsible-host.cordis.yml`), and the approval and question round-trips still reach the UI |
| `RWebView/` | `npm run typecheck` and `npm run build`; committed `dist/`; screenshots for visual changes |
| `RTray/` | kill the host process, then show that the `subst` drive letter and orphaned `RvmEngine.exe` / `node.exe` are gone |
| `.github/`, `scripts/` | the workflow run link, or the exact local command output for the script you changed |

Paste real command output, not a claim that something was tested. Redact the `x-responsible-token`, pipe names, absolute user paths, and anything from a session volume before pasting. Never commit logs, `%LOCALAPPDATA%` dumps, session volumes, or credentials.

## Code in each component

### Host — `Responsible/`, `Responsible.MoeViews/`

C# on `net10.0-windows`, WPF, .NET 10, with CommunityToolkit.Mvvm for view models, `Microsoft.Extensions.DependencyInjection` for composition, and WPF-UI for controls; nullable reference types are enabled project-wide. The host renders state and forwards decisions; it does not own approval or sandbox policy, which belong to the kernel and the engine. Keep the UI responsive: approval and question flows must never block the dispatcher. User-visible strings go through the existing localization path so the English and Simplified Chinese interfaces stay in step. Follow the existing project structure and dependency-injection style; a feature pull request is not the place to restructure the host.

### Kernel plugins — `RDshPlugins/`

Plain ESM modules (`.mjs`) loaded by the cordis patch in `responsible-host.cordis.yml`; there is no build step and no package install. A module exports `name` and `inject` and is mounted by id. Plugins run inside the kernel process, which means:

- Never spawn a host process and never bypass `RvmEngine --shell-bridge`.
- Never persist or log credentials, tokens, or full command lines that carry them.
- Keep the loopback HTTP surface and the named-pipe bridge bound to `127.0.0.1` and token-guarded; treat every model-controlled string as untrusted input.
- Do not fake a stronger guarantee in reported capability. If enforcement is policy-level rather than a kernel boundary, say `partial`, as `rvm-sandbox.mjs` does.
- Keep the prompt contract in `prompts.mjs` consistent with the sandbox the session actually has.

### Engine — `RVM/`

C++20, CMake and Ninja, no exceptions across the pipe boundary; errors travel as the existing `Result` model. `protocol/rvm/v1` is a wire contract shared with the host and the kernel: a change there updates both sides in the same pull request, and an incompatible format change bumps the version rather than mutating v1. Path handling goes through `security/` normalization and out-of-bounds checks — do not add a second path-matching implementation. New dependencies need a justification covering size, license, and maintenance, and any new state that a session can observe belongs in the audit logs. Test additions go in `RVM/tests/`.

### Frontend — `RWebView/`

TypeScript and React 18, bundled by esbuild through `build.mjs`; no new UI framework and no bundler change. Network access is limited to the documented localhost endpoints. Keep keyboard reachability and focus behavior for the cards and the sidebar, and keep both the side-by-side and single-column diff views working when you touch either.

### Watchdog — `RTray/`

A watchdog and nothing else: clean up `subst` mappings and orphaned children, write the crash report, exit. It holds no session state and implements no product behavior. It must keep working when the host dies without cooperating.

### CI and scripts — `.github/`, `scripts/`

The workflow builds the full package on `windows-latest`, keeps the kernel submodule checkout, enforces the 2 GB package limit, and uploads the artifact. Do not remove a step, raise the size limit, or add a required secret without saying so in the pull request. Documentation-only pushes are excluded by `paths-ignore`, so a docs-only change is verified by the reviewer rather than by CI.

## Commits

- One logical change per commit; split refactors, formatting, and behavior changes apart.
- Imperative subject line, at most 72 characters, no trailing period. The body explains why, not what, and references issues (`Fixes #123`).
- English or Simplified Chinese, one language per commit and per pull request.
- Rebase onto `main`; no merge commits inside your branch.
- Do not reformat untouched code. A diff that mixes whitespace churn with a fix will be sent back.
- Do not commit `bin/`, `obj/`, `node_modules/`, `artifacts/`, or a root `dist/`. The two deliberate exceptions are `RWebView/dist/` and `natives/win-x64/`, which are shipped artifacts.

## Pull request body

Every pull request is created by filling in [`.github/pull_request_template.md`](.github/pull_request_template.md). A body that does not follow the structure is returned without review: the reviewer needs the change and its evidence before reading the diff, not after. Write the body in English or Simplified Chinese, matching your commits.

```markdown
## Summary

One to three sentences: what changes and what problem it solves.

## Related issue

Fixes #<issue>, or "No issue — typo/link fix".

## Changes

- <area>: <what changed>

## Verification

Commands run and their actual results. Screenshots for UI changes. Session evidence for isolation changes.

## Isolation impact

None, with one sentence saying why no ACL, pipe, policy, or grant code was touched — or the boundary, the grant, its default state, and the test proving the denied case still denies.

## Risk and rollback

What can break, for whom, and how to revert.

## Checklist

- [ ] Relevant build and tests pass (see CONTRIBUTE.md)
- [ ] Isolation contract is unchanged, or the change is a documented strengthening
- [ ] Regenerated `RWebView/dist/` committed (frontend changes)
- [ ] `natives/win-x64/` rebuilt and committed with toolchain versions (C++ changes)
- [ ] Docs updated in both languages where behavior or setup changed
- [ ] No secrets, tokens, session volumes, logs, or local paths in the diff or pasted output
- [ ] Commits are rebased; no merge commits
```

Section by section:

- **Summary** — the change, not the implementation narrative. If you cannot state it in three sentences, the pull request is probably too large.
- **Related issue** — a link. A security fix references its advisory instead.
- **Changes** — a short list, one line per area, so the reviewer can map the diff to the intent.
- **Verification** — the table in [Build and verify](#build-and-verify) says what this section owes. Include the command and the result; "tested locally" is not evidence.
- **Isolation impact** — required even when there is none. `None` is a claim and must be justified in one sentence. A change that grants any new access is not described here as a side effect; it is the subject of the pull request, with its own approval.
- **Risk and rollback** — be concrete: affected component, affected users, and the revert path, including any data written outside the repository.
- **Checklist** — tick what applies and delete the rest. An unticked box with no explanation blocks review.

A compact example of a passing body:

```markdown
## Summary

Revoke the serial-device ACE when session startup fails after the grant, so a failed
session no longer leaves a device authorized for an AppContainer identity that no
longer exists.

## Related issue

Fixes #148

## Changes

- RVM/: track granted device ACLs before the probe and roll them back on any startup failure
- RVM/tests/: add a case that fails the probe and asserts the ACE is gone

## Verification

- `ctest --test-dir RVM/build --output-on-failure` → 80 passed, 0 failed (was 79)
- Manual: selected COM3, forced `--sandbox-session` to fail, then read the device
  security descriptor with `Get-Acl` — the session SID is absent, and `system.log`
  records the revoke event alongside the original failure.

## Isolation impact

Strengthens invariant 4. No new grant path; a previously leaked ACE is now removed.
Denied case still denied: a device the user did not select never receives an ACE.

## Risk and rollback

Only affects the failure path of session startup. Reverting restores the previous
leak; the change writes nothing outside `%LOCALAPPDATA%\Responsible\RVM\`.

## Checklist

- [x] Relevant build and tests pass
- [x] Isolation contract is unchanged, or the change is a documented strengthening
- [ ] Regenerated `RWebView/dist/` committed (frontend changes)
- [ ] `natives/win-x64/` rebuilt and committed with toolchain versions (C++ changes)
- [ ] Docs updated in both languages where behavior or setup changed
- [x] No secrets, tokens, session volumes, logs, or local paths
- [x] Commits are rebased; no merge commits
```

(The example is illustrative. A real C++ pull request ticks the native-binaries box; a frontend one ticks `dist/`.)

## Review and merge

- At least one maintainer approves. An isolation-boundary change also needs the maintainer to state, in the thread, that the specific grant is accepted — an approving review alone is not that statement.
- CI must be green. `build-release` runs on every push to `main` and on manual dispatch; uncommitted local success does not substitute for a workflow run when your change touches `.github/`, `scripts/`, or packaging.
- Reviewers may ask for a split. A pull request that changes a boundary and three unrelated things at once will be asked to come back as several.
- Do not merge your own pull request. Maintainers choose the merge style; keeping your branch rebased makes either work.
- A pull request with no activity for 60 days may be closed. Reopening it is welcome, and costs nothing.

## Documentation

- User-facing documentation is bilingual. `README.md` is the English default and `README.zh_CN.md` is the Simplified Chinese version; a change to one is a change to both, in the same pull request. The same holds for [`BUILD-GUIDE.md`](BUILD-GUIDE.md) and [`BUILD-GUIDE.zh_CN.md`](BUILD-GUIDE.zh_CN.md), and for any component README that has a translated pair.
- [`BUILD-GUIDE.md`](BUILD-GUIDE.md) changes whenever the build or packaging flow changes, including a trap you had to work around; update its Chinese counterpart in the same pull request.
- One physical line per paragraph, matching the existing files; UTF-8 without a BOM; no trailing whitespace.
- This document is intentionally English-only. The contribution contract is read once, and duplicating it invites the two copies to disagree.
- A comment, a commit message, and a document may be in English or Simplified Chinese, but stay in one language within the file or commit you are editing.

## Licensing

Responsible is distributed under the MIT license. By opening a pull request you agree that your contribution may be distributed under those terms; there is no CLA and no copyright assignment.

- Do not copy code, prompts, or assets from a source whose license is unknown or incompatible with MIT.
- Vendored or third-party material needs an entry in the relevant `THIRD_PARTY_NOTICES.md` (`RWebView/THIRD_PARTY_NOTICES.md`, `RDshCore/THIRD_PARTY_NOTICES.md`), with its upstream location and license.
- Keep generated code and generated binaries accompanied by the command that produced them, so the next contributor can reproduce them.
