# Responsible Release Guide

**English** | [简体中文](BUILD-GUIDE.zh_CN.md)

> Read this before running anything.
>
> An earlier attempt to assemble the release on a workstation ate a 100 GB disk down to
> 50 GB. That is why the release is built on **GitHub Actions**, and why this guide leads
> with CI instead of a local script.

---

## Contents

1. [What gets built](#1-what-gets-built)
2. [Deploy with GitHub Actions (default)](#2-deploy-with-github-actions-default)
3. [Local deployment](#3-local-deployment)
4. [How the pipeline is put together](#4-how-the-pipeline-is-put-together)
5. [Verifying a build](#5-verifying-a-build)
6. [Known failure modes](#6-known-failure-modes)
7. [Maintenance](#7-maintenance)

---

## 1. What gets built

One artifact, `Responsible-<version>-win-x64.zip`, produced by
[`.github/workflows/build-release.yml`](.github/workflows/build-release.yml).

| Item | Size | Notes |
| --- | --- | --- |
| Extracted tree | **~1,067 MB** | hard limit 2,048 MB |
| Zip | **~374 MB** | what you download |
| Files | ~46,000 | 6,801 directories, zero symlinks/junctions |
| CI wall time | ~15 min | kernel build is ~8 min of it |

Contents:

| Component | Source | Built by |
| --- | --- | --- |
| `Responsible.exe` + .NET runtime | `Responsible/` (WPF, `net10.0-windows`) | CI (`dotnet publish -r win-x64 --self-contained`) |
| `RDshCore/` | `RDshCore/` submodule | CI (`pnpm install`, `pnpm run build`, then a hoisted production closure) |
| `RWebView/` | `RWebView/` | CI (`npm ci` + `node build.mjs`) |
| `RDshPlugins/` | `RDshPlugins/` | CI (copied) |
| `node/node.exe` | Node 24 from the runner | CI (copied) |
| `RvmEngine.exe`, `RTray.exe`, 3 MinGW DLLs | `RVM/`, `RTray/` | **you, locally** — committed under `natives/win-x64/` |

Three rules the pipeline enforces, because each one used to be violated:

1. **≤ 2 GB extracted.** The script measures the tree before packaging and fails with a
   per-directory breakdown if it is over.
2. **Windows-only native packages.** Anything matching `-darwin-`, `-linux-`, `-arm64-`,
   … is removed.
3. **No symlinks or junctions in the artifact.** A Windows user with a normal account
   cannot recreate them when unzipping, so the tree is materialized into real files.

---

## 2. Deploy with GitHub Actions (default)

This is the supported path. Nothing but the two native binaries is built on your machine.

### 2.1 One-time prerequisites

| Requirement | Why |
| --- | --- |
| Private repo works fine | the runner clones it with the app's own token |
| Submodule `RDshCore` = `deepseek-ai/deepseek-harness` | public, so the runner clones it anonymously |
| `natives/win-x64/` committed | the five native files; see [§7.2](#72-rebuild-the-native-components) |

Check the submodule points at a commit that still exists upstream:

```powershell
git submodule status          # e.g. " 2e39e70496... RDshCore (heads/master)"
```

### 2.2 Trigger a build

**Automatically** — push to `main`:

```powershell
git add -A
git commit -m "your change"
git push
```

`.md`-only pushes are ignored on purpose (`paths-ignore: '**.md'`), so editing docs does
not burn a 15-minute Windows runner.

**Manually** — Actions → **build-release** → *Run workflow* → optionally set `version`.

**While a build runs**, a second push queues behind it (`concurrency.group` with
`cancel-in-progress: false`); it will not run in parallel.

### 2.3 Watch it

Actions → the run → job *Windows x64 package*. It has three named steps so you can see
where it is:

| Step | Typical | What it does |
| --- | --- | --- |
| `1. 前端（RWebView）` | 5 s | `npm ci`, `node build.mjs` |
| `2. 内核（DSH CLI 闭包）` | ~11 min | kernel install + build + hoisted production closure |
| `3. 组装发行包` | ~4 min | publish, materialize, smoke test, trim, size gate, zip |

### 2.4 Get the artifact

Actions → the finished run → **Artifacts** → `Responsible-<version>-win-x64`.

- Retained **30 days**.
- `version.txt` inside records the commit SHA, so you can tell two builds apart.

Extract it **with the extracted-folder name intact**; the host looks for
`<dir>\RDshCore\apps\cli\lib\bin.js` relative to itself.

### 2.5 Change the version

Two places, keep them in sync:

- `.github/workflows/build-release.yml` → `workflow_dispatch.inputs.version.default`
  and the `inputs.version || '…'` fallbacks
- `scripts/Build-ReleasePackage.ps1` → `[string]$Version = '…'`

### 2.6 If the size gate trips

`Assert-UnderLimit` prints the per-directory breakdown, then fails the run. The two usual
causes:

- nested `node_modules` were not excluded during materialization (see §6.4)
- non-Windows platform packages were reinstalled

---

## 3. Local deployment

Two different goals, two different procedures. **Do not** run the packaging script on a
small system drive.

### 3.1 Run from source (normal development loop)

This is the fast loop: the kernel and the app run from the working tree, no packaging.

```powershell
# 1. natives (only needed once, or when RVM/RTray sources change)
.\build-release.ps1                  # -> natives\win-x64\

# 2. kernel dependencies + build
cd RDshCore
pnpm install --frozen-lockfile       # first run downloads a lot; needs ~10 GB free
pnpm run build                       # ~8 min, produces apps\cli\lib\bin.js
cd ..

# 3. front end
cd RWebView
npm ci
node build.mjs
cd ..

# 4. run the app
dotnet run --project Responsible\Responsible.csproj
```

Toolchain required:

| Tool | Version | Note |
| --- | --- | --- |
| Node.js | ≥ 24.9 | the kernel's `engines` field says so |
| pnpm | 11.28.5 | `corepack enable`; the repo pins it in `packageManager` |
| .NET SDK | 10.x | WPF `net10.0-windows` |
| MinGW-w64 g++ | C++20 | for `RVM` / `RTray`; the MSVC toolchain also works |
| CMake | ≥ 4.3 | both `CMakeLists.txt` declare `cmake_minimum_required(VERSION 4.3)` |
| Ninja | any | recommended generator |

When running from source, the host falls back to `node` on `PATH`; in a package it
prefers `node\node.exe` next to `Responsible.exe`.

### 3.2 Build the full package locally (heavy)

Only do this when you cannot wait for CI. **Budget ~15–20 GB of free disk**, and never
do it on a drive with less than that.

```powershell
# pick a disk with room first
Get-PSDrive D | Select-Object Used,Free

.\scripts\Build-ReleasePackage.ps1 -Phase frontend
.\scripts\Build-ReleasePackage.ps1 -Phase kernel      # heaviest: store + build + closure
.\scripts\Build-ReleasePackage.ps1 -Phase package     # publish + materialize + zip

# result
dist\Responsible-<version>-win-x64.zip
```

Disk accounting for the worst case:

| What | Size |
| --- | --- |
| pnpm content store (`%LOCALAPPDATA%\pnpm\store`) | ~8 GB |
| `RDshCore\node_modules` (isolated, with dev deps) | ~2.5 GB |
| `dist\_kernel-stage` (hoisted production closure) | ~1.5 GB |
| `dist\Responsible-…` (the tree) | ~1.07 GB |
| `dist\*.zip` | ~0.37 GB |

Clean up afterwards — **with `rd`, never `Remove-Item -Recurse`** (the latter follows
junctions and can walk for tens of minutes):

```powershell
cmd /c rd /s /q dist
cmd /c rd /s /q RDshCore\node_modules
pnpm store prune                      # or delete %LOCALAPPDATA%\pnpm\store
```

If a build is interrupted, deleting is still the right move; `rd` is slow but it does
return the space. Do not interrupt `rd` and conclude "the space never came back".

### 3.3 Only rebuild the natives

`.\build-release.ps1` builds `RVM` and `RTray` in Release with MinGW and copies the
binaries plus the three runtime DLLs into `natives\win-x64\`. Add `-Force` to reconfigure
from scratch. Commit the result so CI ships it.

---

## 4. How the pipeline is put together

```
.github/workflows/build-release.yml     three steps, one job, windows-latest
scripts/Build-ReleasePackage.ps1        the actual work (-Phase frontend | kernel | package | all)
scripts/Materialize-KernelTree.mjs      turns pnpm's link layout into real files
build-release.ps1                       local-only: RVM + RTray -> natives\win-x64
natives/win-x64/                        committed native binaries, CI input
```

### 4.1 Why the kernel needs a second install

`pnpm run build` needs devDependencies, so the first install is a normal (isolated)
install. That layout is a forest of junctions — impossible to ship in a zip. The packaging
step therefore:

1. builds in place with the isolated layout (`apps/cli/lib/bin.js` comes out of this)
2. `robocopy`s the tree without `node_modules` into `dist\_kernel-stage`
3. appends `nodeLinker: hoisted` to **that copy's** `pnpm-workspace.yaml` (the submodule
   stays untouched) and installs the CLI production closure there
4. materializes the result into `dist\Responsible-…\RDshCore`

Do not try to flip `nodeLinker` inside the existing install — pnpm tries to remove the
old workspace links and fails with `EPERM` (see §6.2).

### 4.2 What `Materialize-KernelTree.mjs` guarantees

- files are hard-linked into the destination (same volume), so pnpm's dedup survives
- symlinks and junctions are resolved and copied as **real directories/files**
- any `node_modules` below the root is skipped — those are redundant in a hoisted layout,
  and copying them is what once produced 346,000 files
- leftover dangling links are dropped and counted

Exit code is 0 with counters on stdout; the packaging step fails on a non-zero code and
prints the counters.

### 4.3 Guards inside the packaging step

| Guard | Catches |
| --- | --- |
| `--help` smoke test of the materialized kernel | pruned a package the kernel actually needs |
| assertion that `@deepseek-ai/dsh-sandbox` and `dsh-session-stats` exist | the class of bug in [§6.5](#65-packaged-app-sits-on-loading-session-forever--runtime-exited) |
| `Assert-UnderLimit` (2 GB) | runaway installs |
| platform-package trim | shipping darwin/linux native binaries |

---

## 5. Verifying a build

**Before shipping**, in the CI log:

- `内核物化完成: ~1,067 MB` and `--help 正常`
- `完成：目录 1,067 MB / zip 374 MB（上限 2,048 MB）`

**After unzipping**, on a machine:

```powershell
# 1. kernel answers on its own
node RDshCore\apps\cli\lib\bin.js --help

# 2. nothing in the tree is a link
(Get-ChildItem . -Recurse -Force | Where-Object {
    $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count    # must be 0

# 3. which commit is this
Get-Content version.txt
```

**After launching the app**, the host writes a trace that answers "did it really start?":

```powershell
Get-Content "$env:LOCALAPPDATA\Responsible\logs\rvm-session.log" -Tail 20
```

Healthy looks like:

```
ensure cwd=… protected=True
ensure: engine up 143ms
ensure: session created 5607ms
ensure: ready root=R:\My Documents_750ca0\ id=1
```

A repeating `warm: restarting kernel with the RVM binding` every ~120 ms is the failure
mode in [§6.5](#65-packaged-app-sits-on-loading-session-forever--runtime-exited).

---

## 6. Known failure modes

Every entry below has actually happened.

### 6.1 `tar` dies with `-1073741819` (0xC0000005)

**Symptom** — packaging fails at the archive step; `bsdtar` access-violates.

**Cause** — `C:\Windows\system32\tar.exe` follows pnpm's junctions into cycles. It also
cannot be fixed with `--exclude`.

**Fix** — do not archive the tree at all. Use the hoisted install plus
`Materialize-KernelTree.mjs` ([§4.1](#41-why-the-kernel-needs-a-second-install)). `robocopy /E`
is not an alternative either: it expands the hard links into hundreds of thousands of
duplicate files.

### 6.2 `EPERM rmdir …node_modules\@deepseek-ai`

**Symptom** — switching `nodeLinker` in place fails inside
`unlinkPriorWorkspaceHoists`.

**Cause** — pnpm tries to delete workspace links that Windows still has open.

**Fix** — never flip `nodeLinker` in an existing install; install in a clean staging
directory instead.

### 6.3 `ERR_PNPM_LOCKFILE_CONFIG_MISMATCH`

**Symptom** — `injectWorkspacePackages` cannot be used with `--frozen-lockfile`.

**Cause** — the lockfile records the previous setting.

**Note** — `injectWorkspacePackages` did *not* actually remove workspace symlinks when
combined with `--filter`, so it was dropped rather than working around the lockfile
conflict.

### 6.4 346,000 files / 3.5 GB after materializing

**Symptom** — `bytes` around 3.5 GB in the materialize counters; size gate trips.

**Cause** — every workspace project has its own `node_modules` in a hoisted layout, all
pointing back at the root. Copying them duplicates the whole closure.

**Fix** — skip every `node_modules` below the root (already implemented). Result:
59,179 files / 1.01 GB.

### 6.5 Packaged app sits on "loading session" forever + "Runtime exited"

**Symptom** — the sidebar never finishes loading, the status flickers
`Runtime exited`, and `rvm-session.log` repeats `warm: restarting kernel` every ~120 ms.

**Cause** — the host resolves the RVM takeover modules at
`<RDshCore>\packages\sandbox\sandbox\lib\index.js`, which only exists in a development
tree. A packaged tree has `node_modules` only, so `WritePatch()` returned "sandbox module
was not found", the kernel failed to start within milliseconds, `_startedRvmKey` was never
set, and every session open triggered another restart.

**Fix** — `ResolveSandboxModule()` / `ResolveStatsPlugin()` now look in
`node_modules/@deepseek-ai/<pkg>/lib/index.js` first and fall back to `packages/…`.
If you touch those resolvers, keep both branches, and keep the assertion in
`Build-ReleasePackage.ps1` that both packages exist in the release tree.

### 6.6 Session list returns HTTP 502

**Symptom** — `logs\approval.log` shows `Bridge endpoint returned 502`; the conversation
list stays empty.

**Cause** — the host's bridge client used the default `HttpClient` handler. On a machine
with a system proxy and an empty `ProxyOverride`, requests to `127.0.0.1:<bridge>` went
through the local proxy, which answers 502 for a loopback upstream that is not listening
yet.

**Fix** — the bridge client uses `SocketsHttpHandler { UseProxy = false }`, and 502/503/504
are treated as transient (retried) instead of fatal.

### 6.7 "Runtime exited" after a normal restart

**Symptom** — the status flashes `Runtime exited` when switching model or workspace.

**Cause** — `ShutdownAsync` stops the process on purpose, but the `Exited` handler set the
status unconditionally.

**Fix** — `_expectedExit` marks planned shutdowns; only unexpected exits report
`Runtime exited`.

### 6.8 Workspace encoding

`RDshCore` is a submodule; never commit changes inside it. The CI mutates only the copy in
`dist\_kernel-stage`.

---

## 7. Maintenance

### 7.1 Version bumps

See [§2.5](#25-change-the-version). The artifact name follows the version.

### 7.2 Rebuild the native components

```powershell
.\build-release.ps1              # Release, MinGW
.\build-release.ps1 -Force       # ignore the CMake cache
git add natives\win-x64
git commit -m "natives: rebuild"
```

Sanity check before committing:

```powershell
.\natives\win-x64\RvmEngine.exe --probe-availability
# backend=sandbox / appcontainer=ok / winfsp=ok / rvm_available=true
```

### 7.3 Updating the kernel submodule

```powershell
cd RDshCore
git fetch && git checkout <new-commit>
cd ..
git add RDshCore
```

Then run a CI build. If the kernel changes its package layout, the assertions in
`Build-ReleasePackage.ps1` are the first thing to update.

### 7.4 Where the numbers live

The sizes and timings in [§1](#1-what-gets-built) come from the run of 2026-11-23
(`0.0.1-beta.2`). Update them when they drift; they are the fastest way to notice that
the packaging set changed.
