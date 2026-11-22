# RVM Engine

Responsible 的沙箱执行环境。模型仍然调用 `pwsh`/`read`/`write` 等原有工具；`rshell`、`rfs` 只是引擎内部的实现命名，不会出现在提示词或工具 id 中。

当前实现路线（2026-11 转向）：**纯软件强沙箱**。宿主 Windows 内核/用户态原语（AppContainer + Job Object + 逐路径 ACL + 命名管道 + 会话盘盘符映射）代替 Hypervisor 虚拟机。原因：Windows 11 家庭版没有 Containers 组件，HCS 容器不可用；HCS 虚拟机需要客机镜像，不符合“免镜像”要求。Hyper-V/HCS 与 WinFsp 相关代码仅保留为诊断工具，不再作为会话隔离路径。

## 硬约束

- 默认后端是 `sandbox`，但 Responsible 不再默认启用 RVM：工作区需显式勾选“RVM 隔离保护”（新建工作区窗口或审批强度下拉菜单顶部）才会创建会话；未启用时继续使用 DSH 自带沙箱。
- 启动期门禁只有 `appcontainer`：它不可用时会话返回 `RVM_UNAVAILABLE`，Responsible 回退到 DSH 自带沙箱并提示“RVM 不可用”，不静默降级。`vbs`/`hvci` 只作为环境事实写入可用性报告与 `system.json`，不参与放行判断；是否启用内存完整性、是否跟随 Windows 更新由用户自行负责，RVM 不检查也不阻止会话。`winfsp` 只报告安装状态，不门禁。
- 运行中引擎失联一律返回 `RVM_UNAVAILABLE`，不中途切换后端。
- AppContainer 子进程无法自行创建控制台；引擎自行持有一个隐藏控制台，否则子进程在加载器初始化阶段以 `STATUS_DLL_INIT_FAILED` 退出。
- 子进程只继承 stdin/stdout/stderr 三个句柄（`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`），避免宿主管道/句柄泄漏进沙箱。
- 工作区 ACL 授权失败不影响会话：宿主工作区本身不再授权给 AppContainer。会话盘（`<storage_key>\workspace`）授权后，启动会在沙箱内真实探测：写模式真实写入探针并核对宿主侧没有同名文件泄漏，只读模式枚举目录，以实际能力为准。
- 每个工作区使用独立存储目录 `%LOCALAPPDATA%\Responsible\RVM\<storage_key>\`（Responsible 侧 key 为“工作区名前 24 字符_工作区 id 前 6 位”，CLI 为 `cli_<id>`）：`clone.ready`、`seed.manifest`、`baseline.manifest`、`system.json`、`system.log`、`sessions.log`、`audit.log`、`network.log` 常驻这里。会话盘本身放在同级共享目录 `volumes\<storage_key>\`，盘符（subst）挂到 `volumes` 根，因此沙箱里看到的是 `R:\<storage_key>\…`——多个工作区并存时按目录区分，切换会话不必换盘符。旧布局的 `<storage_key>\workspace` 会在下次开会话时自动搬到 `volumes\<storage_key>`。
- 每个会话的临时目录固定在会话盘内 `.rvm_temp`（非叠加会话为工作区下的 `.rvm_temp`）；引擎把它设为子进程的 `TMP`/`TEMP`/`TMPDIR`，会话结束时清理，绝不回退到宿主 `%TEMP%`。
- 祖先目录 traverse 授权逐级记录，会话/诊断结束按逆序撤销；会话盘、工具目录与串口 ACE 一并撤销。AppContainer profile 名带引擎 PID，避免与残留 profile 冲突。
- Node 插件永不 spawn 宿主进程；所有命令由引擎转发到沙箱执行。

## 目录

| 目录 | 职责 |
|---|---|
| `protocol/rvm/v1/` | v1 protobuf 契约（握手、会话、shell、fs、工作区、审计、RPC 信封） |
| `core/` | 错误模型、Result、四套状态机、引擎生命周期、日志 |
| `runtime/` | 后端抽象：`IEnvironmentBackend`、`IEnvironment`、`EnvironmentSpec/Facts` |
| `sandbox/` | AppContainer 配置/ACL/进程运行器，`SandboxBackend`/`SandboxEnvironment` |
| `cli/` | `--create-session`/`--join`/`--terminate`/`--flushback`/`--save`，守护进程拉起、会话记录与离线快照 |
| `environment/` | AppContainer/WinFsp/VBS/HVCI 探测与可用性聚合（仅 AppContainer 门禁） |
| `hyperv/` | 旧 HCS/虚拟机路径，保留作诊断（`--hcs-*`），不参与默认服务 |
| `security/` | Windows 路径规范化/越界判定、masked glob 策略 |
| `channel/` | 固定帧头、帧 I/O、protobuf 编解码、域模型与协议映射 |
| `server/` | 命名管道服务、握手鉴权与 RPC 分发、shell 运行器、网络审计 |
| `dsh/` | rshell/rfs 对 DSH 的语义契约（后续阶段填充） |
| `pipeline/` | 鉴权→策略→审批→审计→执行管线（后续阶段填充） |
| `shim/` | 沙箱内 agent 与降级共享客户端（后续阶段填充） |
| `grpc/` | 预留远程传输，v1 不启用 |
| `tests/` | 自包含测试注册器与断言 |

## 构建

要求：CMake ≥ 4.3、Ninja、C++20 编译器（已验证 MinGW GCC 16）。首次配置会通过 FetchContent 下载 protobuf v21.12 的官方 tarball，并用固定 SHA256 校验；离线环境可用 `-DRVM_PROTOBUF_SOURCE_DIR=<protobuf源码目录>` 指向已解压的源码。若系统已安装可用的 protobuf/protoc，则直接复用。

```sh
cmake -S RVM -B RVM/build -G Ninja -DCMAKE_BUILD_TYPE=Debug
cmake --build RVM/build
ctest --test-dir RVM/build --output-on-failure
```

产物：`RVM/build/bin/RvmEngine.exe`、`RVM/build/bin/rvm_tests.exe`。

## 自检

```sh
RVM/build/bin/RvmEngine --version
RVM/build/bin/RvmEngine --probe-availability
RVM/build/bin/RvmEngine --sandbox-session <workspace-path>     # 真实创建/探测/关闭一次沙箱会话
RVM/build/bin/RvmEngine --sandbox-session <workspace> --overlay-drive R:  # 会话盘挂到 R:（subst，非 WinFsp）
RVM/build/bin/RvmEngine --winfsp-probe [drive]                 # 诊断：直接挂载 UnionView 并做基本读写
RVM/build/bin/RvmEngine --winfsp-smoke <workspace> [drive]     # 诊断：WinFsp 挂载、令牌子进程读写、copy-up、tombstone
RVM/build/bin/RvmEngine --sandbox-smoke <workspace-path>       # cmd 内验证：工作目录、C:\Users、网络、工作区写入
RVM/build/bin/RvmEngine --sandbox-exec <workspace-path> <命令行> # 在沙箱中执行任意命令并回收输出
RVM/build/bin/RvmEngine --sandbox-debug <workspace-path> <命令行> # 调试器模式：打印 DLL 加载与异常
RVM/build/bin/RvmEngine --token-info                           # 在沙箱内运行可核对 AppContainer/完整性级别
printf 'engine-token\n' | RVM/build/bin/RvmEngine --pipe responsible-rvm-<pid> --token-stdin
RVM/build/bin/RvmEngine --shell-bridge --pipe <name> --session <id> [--token <token>] -- <argv...>  # DSH 工具转发入口（优先 --token，缺省读环境）
```

CLI（首次调用自动拉起分离的引擎守护进程）：

```sh
RVM/build/bin/RvmEngine --create-session <alias> <id> [<absolute_path>]  # 省略路径创建空头工作区
RVM/build/bin/RvmEngine --join <id>          # 类 ssh 的交互命令行，限制在会话盘内
RVM/build/bin/RvmEngine --flushback <id>     # 审计差异并同步未冲突项；冲突时退出码 2
RVM/build/bin/RvmEngine --save <id>          # 快照系统状态与工作区清单到 <archive>\snapshots\
RVM/build/bin/RvmEngine --terminate <id>     # 关闭会话并删除档案与本地状态
```

CLI 状态在 `%LOCALAPPDATA%\Responsible\RVM\cli\`，会话档案在 `%LOCALAPPDATA%\Responsible\RVM\cli_<id>\`；网络/设备策略与设置页共用 `%APPDATA%\Responsible\rvm-policy.json`。

旧诊断（需要 Hyper-V/HCS，家庭版无 Containers 时不可用）：`--probe-hcs-service`、`--prepare-base-layer`、`--hcs-smoke`、`--hcs-probe-system`。

`--probe-availability` 只输出真实探测结果；`winfsp=failed` 表示尚未安装 WinFsp，诊断用的 UnionView 与 `--winfsp-smoke` 不可用（RVM 会话不依赖 WinFsp，会话盘由 subst 映射）。`vbs`/`hvci` 只展示事实，不影响 `rvm_available`。

## 宿主通道（P1）

- 管道名为 `\\.\pipe\<name>`、字节流、拒绝远程客户端；DACL 仅授予 SYSTEM 与当前用户。每个连接一个实例与线程，宿主与各工具桥可并发连接。
- 首次调用 `CreateNamedPipe` 使用 `FILE_FLAG_FIRST_PIPE_INSTANCE`，防止同名管道被其他进程抢先创建。
- 引擎 token 由调用方通过 stdin 传入（`--token-stdin`），不使用命令行参数，避免出现在进程命令行中。
- 客户端第一帧必须是 `RpcRequest` 且 body 为 `HelloRequest`；引擎校验协议版本、token（常量时间比较）和 `client_pid == GetNamedPipeClientProcessId`。失败返回 `RVM_UNAUTHENTICATED` 并断开。
- 握手成功后允许 `Ping`/`Shutdown`。`Shutdown` 会停止引擎、关闭全部 RVM 会话并将状态迁移到 `stopped`。
- `CreateSession`/`CloseSession`/`GetSession`/`ListSessions` 由 `SessionManager` 处理：可用时真实创建并启动沙箱环境（AppContainer 配置 + 会话盘 + 真实探测）。`overlay_workspace` 使用 Git 索引选择性播种的会话盘；`network_access` 授予出站能力；`device_paths` 仅接受 COM 端口并逐设备授权；`tool_access` 必须命中用户先前的授权。额外可写根与 `danger-full-access` 仍返回 `RVM_NOT_IMPLEMENTED`，不会静默忽略。
- `ShellExec` 流式返回输出与真实退出码；`WorkspaceDiff`/`WorkspaceApply`/`WorkspaceRollback` 返回真实差异、冲突与落盘结果；`SetToolAuthorizations` 维护用户授权。`fs_*` 与审计查询当前返回 `RVM_NOT_IMPLEMENTED`，不会伪造执行结果。
- 每次客户端断开后管道重建并继续接受同一用户的下一连接；token 不变。

## Shell 转发（P4）

- 引擎实现 `ShellExec`：校验会话与 argv，把宿主工作区内的 workdir 映射到会话盘（越界即 `RVM_PATH_OUT_OF_SCOPE`，不做兜底），转发调用方的环境（剔除 `RESPONSIBLE_RVM_*`，并用会话专属目录覆盖 `TMP`/`TEMP`/`TMPDIR`），在会话的 AppContainer 身份里启动进程，把 stdout/stderr 按 60 KiB 分片以流帧返回，并给出真实退出码、超时/取消标记与 `sandbox.enforcement`。
- `RvmEngine --shell-bridge --pipe <name> --session <id> [--token <token>] [--timeout-ms N] -- <argv...>` 是模型工具的转发入口：从 `--token`（缺省读环境）取得令牌，握手后执行 argv，镜像 stdio 与退出码；连接失败写 `RVM_SANDBOX_UNAVAILABLE: ...` 并以 127 退出，超时 124、取消 130。
- DSH 侧由 `RDshPlugins/rvm-sandbox.mjs` 替换原 `sandbox` 服务：`confine()` 返回上述桥接 argv，`enforcement` 如实上报 `partial`（shell 已隔离，fs 工具仍直连宿主）；工作区不匹配、引擎/会话缺失或 `read-only` 模式一律 fail closed。
- 多客户端：每个连接独立线程，WPF 宿主与每个工具桥可同时连接；`Shutdown` 由客户端触发后会唤醒 accept 循环并收尾，不依赖外部 kill。

## 协议规则

- package 固定 `rvm.v1`；字段号只增不改，删除字段必须 `reserved`。`AvailabilityReport` 已由 `hypervisor/hcs_*` 改为 `appcontainer/winfsp/vbs/hvci`（3/6/7/8 reserved）。
- 顶层枚举值必须带类别前缀（例如 `RVM_*`、`SANDBOX_MODE_*`、`AUDIT_KIND_*`），避免 proto3 作用域冲突。
- 帧头固定 24 字节：magic `RVM1`（线上字节 `52 56 4D 31`）、header version、type、payload size、flags、request id；payload 上限 8 MiB。
- 不提供 JSON 临时协议。调试输出仅使用 `key=value` 文本。
- 未识别的错误码、策略枚举、帧类型一律报 `RVM_PROTOCOL_ERROR`，不做默认放行。

## 当前状态（P1–P4、会话档案与 CLI 已完成）

已完成：

- v1 契约与生成构建；错误/结果/状态机；路径逃逸与 masked 策略；
- AppContainer profile 创建/删除、SID 派生、目录 ACL 授权/撤销、祖先目录 traverse 授权；宿主工作区不授权给沙箱；
- 沙箱进程运行器：`SECURITY_CAPABILITIES` + `HANDLE_LIST`、Job Object（内存/进程数/关闭即杀）、超时终止、stdio 捕获；
- `SandboxBackend`/`SandboxEnvironment`：启动时创建 profile、按 Git 索引播种会话盘、真实探测会话盘可写且宿主无泄漏，停止时卸载盘符、撤销 ACL 并删除 profile；
- 会话盘与快照档案：`<storage_key>\workspace` 是持久会话盘，`seed.manifest`/`baseline.manifest` 记录宿主与会话盘状态；后续启动只增量复制宿主变更，双方都改过的文件保留宿主状态并标记冲突；`system.json`/`system.log` 记录系统配置与包存储条目，系统层无写入通道，Rollback 只还原会话盘；`audit.log`/`sessions.log` 记录同步、回滚与会话生命周期；
- 差异与同步：`WorkspaceDiff` 只遍历会话盘并与基线比对，识别宿主侧并发修改为冲突；`WorkspaceApply` 只写回未冲突项，`WorkspaceRollback` 只用基线覆盖会话盘、不碰宿主；
- 穿透：网络默认关闭，开启后仅授予 `internetClient` 出站能力，loopback/入站固定遮罩，`NetworkAudit` 按命令进程树采样宿主 TCP/UDP 表写入 `network.log`；设备仅支持枚举到的 COM 端口，逐设备添加读写 ACE，授权失败即会话失败；
- 可用性只以 `appcontainer` 为门禁，`vbs`/`hvci`/`winfsp` 仅作事实报告；
- shell 执行链（P4）：多客户端管道、`shell_exec` 流式输出、宿主路径到会话盘的 workdir 映射、`RvmEngine --shell-bridge` 与 DSH `rvm-sandbox` 插件；宿主（WPF）在会话就绪时把引擎参数写入运行时 patch 并注入内核环境；
- CLI（Server 范式）：`--create-session`/`--join`/`--terminate`/`--flushback`/`--save`，首次调用拉起分离守护进程，状态与会话记录持久化在 `%LOCALAPPDATA%\Responsible\RVM\cli\`；
- 握手/鉴权/分发与会话生命周期保持通过；79 个自包含测试全部通过。

### WinFsp 诊断约束（实测）

- WinFsp 的 `FileContext` 在默认模式下是**文件节点**：同名文件的所有打开共享同一节点，`Cleanup` 按每次打开触发、`Close` 才是最终关闭。适配器按“节点 + 引用计数”实现，重复 Close 或未知指针只记录告警，不会二次释放。
- WinFsp 2.2 的卷设备不允许 AppContainer 打开（[winfsp/winfsp#705](https://github.com/winfsp/winfsp/issues/705)）。会话盘因此改用 `subst` 把普通 NTFS 会话目录映射为盘符，进程仍使用 AppContainer 身份与 DACL 隔离；`--winfsp-probe`/`--winfsp-smoke` 仅作为诊断保留，配合受限令牌使用，不代表会话隔离路径。
- 会话盘按工作区持久复用，盘符来自请求或 `--overlay-drive`（缺省选首个空闲盘符）。

### 宿主实测（Windows 11 家庭版 25H2, build 26200）

- `--probe-availability`：`backend=sandbox`、`appcontainer=ok`、`vbs=ok`、`hvci=ok`、`winfsp=ok`、`rvm_available=true`（2026-11-21 已安装 WinFsp 2.2.26323，签名 NAVIMATICS LLC）；
- `--sandbox-session D:\...\smoke-ws`：`backend_ready=true`、`session_state=ready`、`windows_build=26200`、`session_state=stopped`；
- `--sandbox-smoke`：`cwd` 为工作区；`dir C:\Users` 返回“拒绝访问”；`ping baidu.com` 无法解析（无网络）；工作区内写读成功；
- CLI：真实 Git 工作区与空头工作区均可 `--create-session`/`--join`/`--flushback`/`--save`/`--terminate`；宿主与会话同时修改同一文件时，`--flushback` 返回冲突（退出码 2）且不覆盖宿主内容；
- 已定位并修复：沙箱子进程因非继承 stdin 句柄导致 `STATUS_INVALID_HANDLE`/`STATUS_DLL_INIT_FAILED`；AppContainer 子进程需要可继承控制台，否则加载器初始化失败。

尚未实现：

- `fs_*` RPC 执行链：fs 工具仍由宿主进程执行，`rvm-sandbox` 插件在 `resolve` 层把目标改写到会话盘并拒绝越界路径，因此 `enforcement` 为 `partial`；
- `shell_kill`（目前通过关闭发起客户端连接取消命令）；
- 引擎内审批/策略管线（`pipeline/`）与审计查询 RPC；审计目前只追加到 `audit.log`/`network.log`；
- P3 的 masked 回调：会话盘只包含 Git 索引范围内的文件，宿主其他路径不在沙箱可见范围内，但尚无“存在但不可读”的虚拟盘掩码；
- 预热、模糊测试、性能加固。

## 安全不变量

- 宿主用户目录、凭据、SSH、浏览器数据永不被授权给 AppContainer；仅会话盘、会话 Temp、用户显式授权的工具目录与串口获得访问权。
- 路径先规范化再授权；拒绝 UNC、设备路径、ADS、保留名、尾点/空格、控制字符与越界 `..`。
- 会话盘只包含 Git 索引范围内的文件；P3 的 masked 回调尚未实现，宿主其他路径不进入沙箱可见范围。
- VBS/HVCI 只记录为环境事实，不是 RVM 可用条件；启用内存完整性、跟随 Windows 更新由用户自行负责，RVM 不检查。
- 审计只追加；日志不记录文件内容、凭据或命令环境变量值。
- 管道 ACL 限当前用户，校验 token 与对端 PID；帧、命令、输出均有硬上限。