<div align="center">
<h1>Responsible</h1>

试着对模型的每一响应负责。

严格的 Agent 文件隔离 Harness

<p>
  <a href="https://github.com/f13T2ach/Responsible/actions/workflows/build-release.yml"><img alt="build-release" src="https://github.com/f13T2ach/Responsible/actions/workflows/build-release.yml/badge.svg?branch=main"></a>
  <a href="https://github.com/f13T2ach/Responsible/commits/main"><img alt="last commit" src="https://img.shields.io/github/last-commit/f13T2ach/Responsible?branch=main"></a>
  <img alt="license: MIT" src="https://img.shields.io/badge/license-MIT-blue">
  <img alt="platform: Windows x64" src="https://img.shields.io/badge/platform-Windows%20x64-0078D4">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4">
</p>

<p>
  <a href="README.md">English</a> | <b>简体中文</b>
</p>
</div>

## 介绍

Responsible 的内核是 Deepseek Harness，通过载入自定插件来重新路由 AI 的指令请求到 Responsible Virtual Machine（RVM），同时支持基于 AppContainer 的工具链穿透、独立工作区来提供安全的 Agent 辅助编程环境，期望任何人和 AI 都无法突破 Responsible 进行隐私读取和中间人攻击。

~~Responsible 支持安装专为 Responsible 定制的 DSH 插件（RDsh）来定义含本地配置文件的敏感工具的调用方式（如 Git、SSH）。~~

## 架构

Responsible 由三个进程组成，边界清晰、各自可替换：

| 进程 | 语言/运行 | 职责 |
| --- | --- | --- |
| `Responsible.exe` | C# / WPF（net10.0-windows） | 界面、会话与工作区管理、权限档位、RVM 生命周期 |
| `RDshCore`（node） | DSH 内核 | 模型对话、工具执行、审批与提问的决策点 |
| `RvmEngine.exe` | C++（CMake） | RVM 沙箱：AppContainer 身份、逐路径 ACL、会话盘、subst 盘符 |

三者之间的通道：

- 宿主 ↔ 内核：stdio 上的 NDJSON JSON-RPC，另有一条本地 HTTP 桥（`127.0.0.1:<port>` + `x-responsible-token`）供前端轮询审批/提问/任务状态
- 宿主 ↔ 引擎：命名管道，24 字节头 + `rvm.v1` protobuf
- 宿主 ↔ 前端：WebView2 `postMessage`

宿主把内核插件（`RDshPlugins/`）注入内核进程，用它们进行会话操控。

## 典型的工作方式

Responsible-CLI 跨平台版本正在开发，目前只支持 Windows 版本。

1. 克隆仓库（.NET 10），配置 NuGet 包，构建编译。或者直接从 Releases 下载 GitHub Actions 工作流的构建产物。
2. 在“编辑工作区”中勾选“RVM 隔离保护”，并建立 Git 索引（办公目录选“全面索引（推荐办公）”，代码仓库选“普通索引”）。
3. 在设置 → RVM 中配置网络与设备穿透；在工作区设置中引入要穿透的工具链和环境变量。
4. 配置 RDsh 插件，引入 AI 可用的敏感工具。
5. 开始工作。当一轮工作完成后，审计会话盘差异，再 Sync 回宿主、Rollback 会话盘，或用 CLI 的 `--flushback` 同步。

## 构建

需要 .NET 10 SDK、Node.js 20+、MinGW-w64 + CMake + Ninja（RVM 与 RTray）、WebView2 Runtime。

```powershell
# 1. 前端
cd RWebView; npm install; node build.mjs      # 产出 dist/index.html 与 dist/diff.html

# 2. RVM 引擎
cd RVM; cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release; cmake --build build
.\build\bin\rvm_tests.exe                     # 自测：79 passed, 0 failed

# 3. Watch Dog 进程
cd RTray; cmake -S . -B build -G Ninja; cmake --build build

# 4. 宿主
cd Responsible; dotnet build                  # 或 dotnet publish -c Release
```

启动后 `Responsible.exe` 会按需拉起 `RDshCore` 与 `RvmEngine`；`RTray.exe` 由宿主启动并监视宿主进程句柄，宿主被强杀时由它清掉 `subst` 盘符与遗留子进程。

要**打包构建而非开发构建**，见 [BUILD-GUIDE.zh_CN.md](BUILD-GUIDE.zh_CN.md)（英文版为 [BUILD-GUIDE.md](BUILD-GUIDE.md)）。你可能会在编译 DSH 内核时遇到问题，可以参考指南。

## RDsh 插件

RDsh 插件专为调用敏感工具而生。它接收 `rshell` 传入的 Session，进行审计后转发到宿主机，也就是实现一个凭证代理。

由于敏感的工具比较复杂（例如 Git），需要专门编写 RDsh 插件来定义调用方式。不过，Responsible 也保留了一个专门的、用于在无 RDsh 插件可用时转发 Session 的插件：`runiverse`。

请注意：使用 `runiverse` 是缓兵之计，因为在人工审计失效的情况下，AI 能利用 `runiverse` 来控制宿主机。

**要了解更多，前往 RDshPlugins/README.md**

## RVM 虚拟机

RVM 并不是镜像驱动的虚拟机，它通过访问控制实现了文件隔离，但是没有实现内核隔离。**启用内存完整性保护、跟随 Windows 更新** 可以有效避免 AI 利用内核漏洞逃逸。

RVM 在本地有快照档案，能记录 VM 内部的系统配置与工作区快照：系统配置、包存储条目、授权与会话日志记入 `system.json`/`system.log`/`sessions.log`/`audit.log`；系统层本身不可写（AppContainer 没有系统写入通道），Rollback 只还原工作区。

### RVM 介质

1. **RVM Infrastructure** —— RVM 不复制、也不链接系统文件。会话基础设施由 AppContainer 身份与逐路径 ACL 授权构成：宿主用户目录、凭据、SSH 与浏览器数据永不授权；只授权会话盘、会话 Temp、用户显式授权的工具目录和串口。系统组件仍由 Windows 按 AppContainer 规则提供，实现里不存在“链接只读系统文件”这一层。
2. **RVM Workspace** —— RVM 的工作区是 Git 索引快照，不是原工作区的克隆：引擎按工作区的 Git 索引把 tracked 文件与未被忽略的 untracked 文件选择性复制到本地会话盘，依赖目录、构建产物和缓存不进入快照；之后每次启动只增量同步宿主变更，沙箱内的修改保留在会话盘。没有 Git 索引时 RVM 拒绝创建会话，不会退化为扫描整个目录。用户可以在审计差异后执行 Rollback（只还原会话盘）或 Sync（只写回未冲突项）。
3. **RVM Toolchain** —— RVM 对用户指定的工具目录做只读/执行授权，AI 调用这些工具时进程仍运行在会话内，输出与副作用落在会话盘；敏感工具的实际调用由 RDsh 插件做凭证代理与审计。

### RVM Penetrate

1. RVM 允许你设置 Penetrate Devices：设置 → RVM 只枚举宿主串口（COM）设备，勾选后引擎为该会话的 AppContainer 身份在设备对象安全描述符上添加读写 ACE，停止会话时撤销。设备打不开或授权失败会让会话启动失败，不会伪装成已暴露；未勾选的外设不会被原样穿透。
2. RVM 允许你设置 Penetrate Network：默认关闭，会话没有任何网络能力。开启后只授予 `internetClient` 出站能力，不授予 `privateNetworkClientServer`，也没有 loopback 豁免。等效网络的实现方式是对不可访问的网段进行遮蔽：宿主 loopback 端口与会话入站端口互不可见，会话既不能访问宿主服务端口，也不能在遮罩范围内绑定/监听；没有按端口单独放行的配置。网络审计按命令进程树采样宿主 TCP/UDP 表，把新观察到的端点写入会话档案 `network.log`；采样有间隔，极短连接可能漏记。

### RVM CLI

RVM 是 Server 范式的，给出 `--create-session <alias> <id> <absolute_path>` 能创建工作区，当 `<absolute_path>` 省略时，创建空头工作区。给出 `--join <id>`，能进入一个类似 ssh 的 CLI，能执行命令。给出 `--terminate <id>`，移除工作区和本地状态档案。给出 `--flushback <id>`，同步工作区内部的更改到本地磁盘。给出 `--save <id>`，保存 RVM 指定 id 的状态（含系统状态和工作区状态）。

实现约定：

- 首次调用会自动拉起一个分离的引擎守护进程（不占用当前控制台）；后续命令通过命名管道复用同一引擎与 token。状态保存在 `%LOCALAPPDATA%\Responsible\RVM\cli\`（`engine.ini`、`sessions\<id>.ini`、`workspaces\<id>`），会话档案在 `%LOCALAPPDATA%\Responsible\RVM\cli_<id>\`。
- `--create-session` 要求 `<absolute_path>` 是一个独立 Git 仓库根：引擎用 `git ls-files` 读取索引来确定快照范围。没有索引时会拒绝创建并提示先 `git init`；办公目录可直接 `git add -A` 建立全面索引，RVM 不会扫描整块磁盘。空头工作区会自动建立空仓库，只有身份和工作日志，没有宿主角落。
- 网络与设备穿透与设置页共用 `%APPDATA%\Responsible\rvm-policy.json`，在创建会话时固化。
- `--join` 的 `cd` 只允许会话盘内路径，命令经 `cmd.exe` 在沙箱里执行；`exit`/`quit` 断开但不销毁会话。
- `--flushback` 先打印审计差异，再同步未冲突项；存在冲突时返回退出码 2，宿主上被修改的文件不会被覆盖。空头工作区没有宿主角落，会提示改用 `--save`。
- `--terminate` 会先关闭运行中的引擎会话；关闭失败时保留档案，避免会话盘被删后沙箱仍向其写入。
- `--save` 生成 `<档案>\snapshots\<时间戳>\`：复制 `system.json`、`system.log`、`sessions.log`、`audit.log`、`network.log`、`seed.manifest`、`baseline.manifest`、`clone.ready`，写入 `workspace.manifest`（有上限，超时或超量会标记 truncated）和会话运行时的 `workspace.diff`，不含宿主机扫描，也不复制会话盘本身。

**要了解更多，前往 RVM/README.md**

## RWebView

RWebView 实现了部分可视化页面。

**要了解更多，前往 RWebView/README.md**

## 功能概览

- **RVM 隔离会话**：工作区按 Git 索引选择性快照到会话盘，命令与文件读写都落在会话盘与专用 temp；宿主用户目录、凭据、SSH/浏览器数据从不授权
- **权限档位**：RVM 会话固定只有「RVM 隔离保护」一档；会话盘挂载前下拉置灰、输入框不可发送，挂载后放开
- **审批（approval）**：模型请求提升到 `danger-full-access` 时弹出卡片（拒绝 / 允许一次）；RVM 会话直接拒绝，不打扰用户
- **提问（ask_user_question）**：对话区底部卡片，支持单选/多选/自定义文本与模型给的等待窗口倒计时
- **改动卡片与 review**：每一轮改动用一张卡片收口，点开在右侧栏看双行号 diff（side-by-side 与单列可切）
- **右侧栏**：顶部按钮展开；空态是入口页，有内容时是标签页（review / 工作区文件 / 开始）
- **底部状态栏**：只显示 RVM 运行态——就绪情况、会话盘挂载点、网络、设备

## 配置与日志

配置文件：

- `%APPDATA%\Responsible\rvm-policy.json`：`networkEnabled` / `devicesEnabled` / `devices`
- `%APPDATA%\Responsible\`：`workspaces.json`、`conversations.json`、`providers.json`、`theme.json`、`acl-authorizations.json`

运行数据：

| 路径 | 内容 |
| --- | --- |
| `%LOCALAPPDATA%\Responsible\logs\` | `kernel.log`（内核实录，含插件注册/审批/提问）、`rvm-session.log`、`rvm-engine.log`、`webview.log`、`approval.log`、`questions.log`、`crash.log` |
| `%LOCALAPPDATA%\Responsible\RVM\volumes\<key>\` | 会话盘（挂载为 `R:\<key>`） |
| `%APPDATA%\Responsible\changes\<sessionId>.jsonl` | 逐轮改动记录（重启后卡片与 diff 的来源） |

## 目录结构

```
Responsible/     WPF 宿主：界面、会话、工作区、权限、RVM 生命周期
RDshCore/        DSH 内核（node）：对话、工具、审批与提问的决策点
RDshPlugins/     注入内核的插件：系统提示、RVM 沙箱接管、审批/提问桥
RVM/             RVM 引擎（C++/CMake）：AppContainer、ACL、会话盘、subst
RWebView/        对话区与右侧栏前端（TypeScript + React，esbuild 打包）
RTray/           Watch Dog 进程（C++）：宿主异常退出后清理盘符与孤儿进程
```

## 贡献指南

提交 issue 或 PR 之前请先读 [CONTRIBUTE.md](CONTRIBUTE.md)（英文）。它写明了不得削弱的隔离契约、构建与测试命令、各组件的代码约定，以及每个 PR 必须采用的正文结构。

## 开源和免责声明

开源许可协议：MIT

免责声明：选择 Responsible，说明你认为此解决方案的强度和潜在风险能够接受和承受。除 MIT 协议要求的责任免除之外，Responsible 还不对任何包括但不限于 0day 漏洞、技术缺陷等原因造成的损失承担责任。Responsible 本身不提供任何 AI 计算供应商服务。
