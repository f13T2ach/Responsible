# Responsible 发行构建指南

[English](BUILD-GUIDE.md) | **简体中文**

> 动手之前先把这份读完。
>
> 上一轮在开发机上组装发行包，把一块 100GB 可用空间的盘吃到 50GB 以下。
> 所以现在的发行包**在 GitHub Actions 上构建**，这份文档也以 CI 为主、本地为辅。

---

## 目录

1. 产出什么
2. 用 GitHub Actions 出包（默认路径）
3. 本地部署
4. 流水线是怎么搭的
5. 怎么验证一个包
6. 已知故障模式
7. 维护

---

## 1. 产出什么

一个文件：`Responsible-<版本>-win-x64.zip`，由
[`.github/workflows/build-release.yml`](.github/workflows/build-release.yml) 产出。

| 项 | 值 | 说明 |
| --- | --- | --- |
| 解压后目录 | **约 1,067 MB** | 硬上限 2,048 MB |
| zip | **约 374 MB** | 你下载的就是它 |
| 文件数 | 约 46,000 | 6,801 个目录，**零**符号链接/junction |
| CI 端到端 | 约 15 分钟 | 其中内核构建约 8 分钟 |

包含内容：

| 组件 | 来源 | 谁构建 |
| --- | --- | --- |
| `Responsible.exe` + .NET 运行时 | `Responsible/`（WPF，`net10.0-windows`） | CI（`dotnet publish -r win-x64 --self-contained`） |
| `RDshCore/` | `RDshCore/` 子模块 | CI（`pnpm install`、`pnpm run build`，再装一份 hoisted 生产闭包） |
| `RWebView/` | `RWebView/` | CI（`npm ci` + `node build.mjs`） |
| `RDshPlugins/` | `RDshPlugins/` | CI（复制） |
| `node/node.exe` | runner 上的 Node 24 | CI（复制） |
| `RvmEngine.exe`、`RTray.exe`、3 个 MinGW DLL | `RVM/`、`RTray/` | **本机构建**，提交到 `natives/win-x64/` |

流水线强制三条规则，因为每一条都被违反过：

1. **解压后不超过 2GB。** 打包前量体积，超了就打印各目录占用并让这一步失败。
2. **只带 Windows 原生包。** 名字里含 `-darwin-`、`-linux-`、`-arm64-` 的一律剪掉。
3. **发行包里不能有符号链接或 junction。** 普通 Windows 账号解压时建不出来，
   所以整棵树被物化成真文件。

---

## 2. 用 GitHub Actions 出包（默认路径）

这是受支持的路径。本机除了两个原生组件，什么都不用建。

### 2.1 一次性准备

| 要求 | 说明 |
| --- | --- |
| 私有仓库没问题 | runner 用仓库自己的 token 拉代码 |
| 子模块 `RDshCore` = `deepseek-ai/deepseek-harness` | 公开仓库，runner 匿名就能拉 |
| `natives/win-x64/` 已提交 | 那 5 个原生文件，见 §7.2 |

确认子模块指向的提交在上游仍然存在：

```powershell
git submodule status          # 例如 " 2e39e70496... RDshCore (heads/master)"
```

### 2.2 触发构建

**自动** —— push 到 `main`：

```powershell
git add -A
git commit -m "你的改动"
git push
```

只改 `.md` 的 push 会被跳过（`paths-ignore: '**.md'`），
所以改文档不会白烧一次 15 分钟的 Windows runner。

**手动** —— Actions → **build-release** → *Run workflow*，可以顺便填 `version`。

**构建进行中**再 push 一次，新的一轮会排队（`concurrency.group` +
`cancel-in-progress: false`），不会并行跑。

### 2.3 看进度

Actions → 这一轮 → job *Windows x64 package*。三步各有名字，能看出卡在哪：

| 步骤 | 典型耗时 | 做什么 |
| --- | --- | --- |
| `1. 前端（RWebView）` | 5 秒 | `npm ci`、`node build.mjs` |
| `2. 内核（DSH CLI 闭包）` | 约 11 分钟 | 内核安装 + 构建 + hoisted 生产闭包 |
| `3. 组装发行包` | 约 4 分钟 | publish、物化、冒烟、剪平台、体积锁、zip |

### 2.4 取产物

Actions → 跑完的这一轮 → **Artifacts** → `Responsible-<版本>-win-x64`。

- 保留 **30 天**。
- 包里的 `version.txt` 记了提交 SHA，两次构建不会搞混。

解压后**保持外层目录名不变**：宿主按
`<目录>\RDshCore\apps\cli\lib\bin.js` 相对自己去找内核。

### 2.5 改版本号

两处，必须同步：

- `.github/workflows/build-release.yml` → `workflow_dispatch.inputs.version.default`
  以及三处 `inputs.version || '…'` 兜底
- `scripts/Build-ReleasePackage.ps1` → `[string]$Version = '…'`

### 2.6 体积锁触发之后

`Assert-UnderLimit` 会打印各目录占用，然后让这一轮失败。常见两种原因：

- 物化时没排除嵌套的 `node_modules`（见 §6.4）
- 非 Windows 平台包又被装回来了

---

## 3. 本地部署

两个不同目的，两套做法。**不要**在系统盘容量不够时跑打包脚本。

### 3.1 从源码跑（日常开发）

这是快的那条路：内核和应用都从工作区直接跑，不打包。

```powershell
# 1. 原生组件（只需一次；改了 RVM/RTray 源码再跑）
.\build-release.ps1                  # -> natives\win-x64\

# 2. 内核依赖 + 构建
cd RDshCore
pnpm install --frozen-lockfile       # 首次下载量大，需要约 10GB 空闲
pnpm run build                       # 约 8 分钟，产出 apps\cli\lib\bin.js
cd ..

# 3. 前端
cd RWebView
npm ci
node build.mjs
cd ..

# 4. 跑应用
dotnet run --project Responsible\Responsible.csproj
```

需要的工具链：

| 工具 | 版本 | 说明 |
| --- | --- | --- |
| Node.js | ≥ 24.9 | 内核 `engines` 就是这么写的 |
| pnpm | 11.28.5 | `corepack enable`；仓库在 `packageManager` 里钉死了 |
| .NET SDK | 10.x | WPF `net10.0-windows` |
| MinGW-w64 g++ | 支持 C++20 | 给 `RVM` / `RTray`；MSVC 也能编 |
| CMake | ≥ 4.3 | 两个 `CMakeLists.txt` 都写了 `cmake_minimum_required(VERSION 4.3)` |
| Ninja | 任意 | 推荐生成器 |

从源码跑时，宿主找不到同目录的 `node\node.exe` 就回落到 `PATH` 上的 `node`；
打包版优先用 `node\node.exe`。

### 3.2 本地打完整包（重活）

只有在等不了 CI 时才这么做。**准备 15–20GB 空闲空间**，小于这个数就别开工。

```powershell
# 先看盘
Get-PSDrive D | Select-Object Used,Free

.\scripts\Build-ReleasePackage.ps1 -Phase frontend
.\scripts\Build-ReleasePackage.ps1 -Phase kernel      # 最重：store + 构建 + 闭包
.\scripts\Build-ReleasePackage.ps1 -Phase package     # publish + 物化 + zip

# 产物
dist\Responsible-<版本>-win-x64.zip
```

最坏情况的磁盘占用：

| 内容 | 大小 |
| --- | --- |
| pnpm 内容寻址存储（`%LOCALAPPDATA%\pnpm\store`） | 约 8 GB |
| `RDshCore\node_modules`（isolated，含 dev） | 约 2.5 GB |
| `dist\_kernel-stage`（hoisted 生产闭包） | 约 1.5 GB |
| `dist\Responsible-…`（发行树） | 约 1.07 GB |
| `dist\*.zip` | 约 0.37 GB |

收尾要清理，**用 `rd`，绝不要 `Remove-Item -Recurse`**（后者会跟着 junction 走，
能卡几十分钟）：

```powershell
cmd /c rd /s /q dist
cmd /c rd /s /q RDshCore\node_modules
pnpm store prune                      # 或者直接删 %LOCALAPPDATA%\pnpm\store
```

构建被打断也要删干净；`rd` 慢，但空间会回来。别中途打断 `rd` 就断言“空间没回来”。

### 3.3 只重建原生组件

`.\build-release.ps1` 用 MinGW 以 Release 构建 `RVM` 和 `RTray`，
把两个可执行文件加三个运行库复制进 `natives\win-x64\`。
加 `-Force` 会丢掉 CMake 缓存重新配置。改完记得提交，CI 才能带上。

---

## 4. 流水线是怎么搭的

```
.github/workflows/build-release.yml     三步一个 job，windows-latest
scripts/Build-ReleasePackage.ps1        真正干活的脚本（-Phase frontend | kernel | package | all）
scripts/Materialize-KernelTree.mjs      把 pnpm 的链接布局变成真文件
build-release.ps1                       只在本地跑：RVM + RTray -> natives\win-x64
natives/win-x64/                        提交进仓库的原生二进制，CI 的输入
```

### 4.1 内核为什么要装两遍

`pnpm run build` 需要 devDependencies，所以第一次是普通的（isolated）安装。
这种布局是一片 junction 森林——根本没法塞进 zip。于是打包这一步：

1. 用 isolated 布局就地构建（`apps/cli/lib/bin.js` 从这里来）
2. 用 `robocopy` 把不含 `node_modules` 的源码复制到 `dist\_kernel-stage`
3. 只在**那份副本**的 `pnpm-workspace.yaml` 里追加 `nodeLinker: hoisted`，
   在那里装 CLI 的生产闭包（子模块本身不动）
4. 把结果物化到 `dist\Responsible-…\RDshCore`

不要在已有的安装上原地改 `nodeLinker`——pnpm 会去删旧的 workspace 链接并报 `EPERM`
（见 §6.2）。

### 4.2 `Materialize-KernelTree.mjs` 保证什么

- 文件用同卷硬链接搬过去，pnpm 的去重不会丢
- 符号链接和 junction 一律解析成**真目录/真文件**
- 根以下的任何 `node_modules` 都跳过——hoisted 布局下它们是冗余的，
  照搬正是当初 34 万文件的来源
- 剩下的悬挂链接直接丢掉并计数

退出码为 0，计数器打在 stdout；打包步骤遇到非零退出码就失败并把这些数打出来。

### 4.3 打包步骤里的守卫

| 守卫 | 拦住什么 |
| --- | --- |
| 物化后跑内核 `--help` | 剪掉了内核真正需要的包 |
| 断言 `@deepseek-ai/dsh-sandbox`、`dsh-session-stats` 存在 | §6.5 那一类问题 |
| `Assert-UnderLimit`（2GB） | 装飞了 |
| 平台包裁剪 | 把 darwin/linux 原生包打进发行包 |

---

## 5. 怎么验证一个包

**发布之前**，看 CI 日志：

- `内核物化完成: ~1,067 MB` 和 `--help 正常`
- `完成：目录 1,067 MB / zip 374 MB（上限 2,048 MB）`

**解压之后**，在目标机器上：

```powershell
# 1. 内核自己能起来
node RDshCore\apps\cli\lib\bin.js --help

# 2. 树里没有链接
(Get-ChildItem . -Recurse -Force | Where-Object {
    $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count    # 必须是 0

# 3. 这是哪个提交
Get-Content version.txt
```

**启动应用之后**，宿主会写一份追踪日志，回答“到底起来没有”：

```powershell
Get-Content "$env:LOCALAPPDATA\Responsible\logs\rvm-session.log" -Tail 20
```

正常长这样：

```
ensure cwd=… protected=True
ensure: engine up 143ms
ensure: session created 5607ms
ensure: ready root=R:\My Documents_750ca0\ id=1
```

如果每 ~120ms 刷一次 `warm: restarting kernel with the RVM binding`，
那就是 §6.5。

---

## 6. 已知故障模式

下面每一条都真实发生过。

### 6.1 `tar` 以 `-1073741819`（0xC0000005）挂掉

**现象** —— 归档步骤失败，`bsdtar` 触发访问违例。

**原因** —— `C:\Windows\system32\tar.exe` 会跟着 pnpm 的 junction 递归进环里，
`--exclude` 也救不了。

**做法** —— 根本不要归档这棵树。用 hoisted 安装 + `Materialize-KernelTree.mjs`
（见 §4.1）。`robocopy /E` 同样不行：它会把硬链接展开成几十万份副本。

### 6.2 `EPERM rmdir …node_modules\@deepseek-ai`

**现象** —— 原地切 `nodeLinker` 时在 `unlinkPriorWorkspaceHoists` 里失败。

**原因** —— pnpm 要删的 workspace 链接还被 Windows 占着。

**做法** —— 不要在已有安装上切 `nodeLinker`，换一个干净的暂存目录装。

### 6.3 `ERR_PNPM_LOCKFILE_CONFIG_MISMATCH`

**现象** —— `injectWorkspacePackages` 和 `--frozen-lockfile` 不能一起用。

**原因** —— lockfile 里记着上一次的设置。

**备注** —— `injectWorkspacePackages` 配合 `--filter` 其实并没有把 workspace 的符号链接
去掉，所以最后是放弃它，而不是绕着 lockfile 冲突做兼容。

### 6.4 物化出 34 万文件 / 3.5GB

**现象** —— 物化计数器 `bytes` 约 3.5GB，体积锁触发。

**原因** —— hoisted 布局下每个 workspace 项目都有自己的 `node_modules`，
全部指回根上；照搬等于把整个闭包复制一遍。

**做法** —— 跳过根以下所有 `node_modules`（已实现）。结果是 59,179 文件 / 1.01GB。

### 6.5 打包版一直“正在加载会话” + `Runtime exited`

**现象** —— 侧栏一直转圈，状态闪 `Runtime exited`，
`rvm-session.log` 每 ~120ms 重复一次 `warm: restarting kernel`。

**原因** —— 宿主在 `<RDshCore>\packages\sandbox\sandbox\lib\index.js` 找 RVM 接管用的模块，
而这个路径只存在于开发树。发行树只有 `node_modules`，于是 `WritePatch()` 返回
“找不到沙箱模块”，内核几百毫秒内启动失败，`_startedRvmKey` 永远设不上，
每打开一次会话就再重启一次。

**做法** —— `ResolveSandboxModule()` / `ResolveStatsPlugin()` 改成先找
`node_modules/@deepseek-ai/<包>/lib/index.js`，再回退 `packages/…`。
以后动这两个解析函数时，两条分支都要留；同时保留
`Build-ReleasePackage.ps1` 里“这两个包必须存在”的断言。

### 6.6 会话列表返回 HTTP 502

**现象** —— `logs\approval.log` 出现 `Bridge endpoint returned 502`，会话列表一直空。

**原因** —— 宿主的桥接客户端用的是默认 `HttpClient` handler。机器开了系统代理、
`ProxyOverride` 又为空时，连 `127.0.0.1:<桥接端口>` 也被送进本地代理；
内核对端还没开始监听，代理就回 502。

**做法** —— 桥接客户端改用 `SocketsHttpHandler { UseProxy = false }`；
502/503/504 按暂时性故障重试，不再当致命错误。

### 6.7 正常重启之后闪 `Runtime exited`

**现象** —— 换模型、换工作区时状态闪一下 `Runtime exited`。

**原因** —— `ShutdownAsync` 是主动停进程，但 `Exited` 回调无条件写了这个状态。

**做法** —— `_expectedExit` 标记计划内关停，只有真正意外退出才报 `Runtime exited`。

### 6.8 工作区编码

`RDshCore` 是子模块，**不要**在里面提交任何改动。CI 只改 `dist\_kernel-stage` 里那份副本。

---

## 7. 维护

### 7.1 改版本号

见 §2.5。产物名跟着版本号走。

### 7.2 重建原生组件

```powershell
.\build-release.ps1              # Release，MinGW
.\build-release.ps1 -Force       # 丢掉 CMake 缓存重来
git add natives\win-x64
git commit -m "natives: rebuild"
```

提交前做个自检：

```powershell
.\natives\win-x64\RvmEngine.exe --probe-availability
# backend=sandbox / appcontainer=ok / winfsp=ok / rvm_available=true
```

### 7.3 更新内核子模块

```powershell
cd RDshCore
git fetch && git checkout <新提交>
cd ..
git add RDshCore
```

然后跑一次 CI。如果内核改了包的目录结构，第一个要改的就是
`Build-ReleasePackage.ps1` 里的那些断言。

### 7.4 这些数字从哪来

§1 的体积和耗时来自 2026-11-23 的 `0.0.1-beta.2` 那一轮构建。
数字变了就顺手更新——它们是发现“打包集合变了”最快的信号。
