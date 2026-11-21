# RVM Engine

Responsible 的沙箱执行环境。模型仍然调用 `pwsh`/`read`/`write` 等原有工具；`rshell`、`rfs` 只是引擎内部的实现命名，不会出现在提示词或工具 id 中。

当前实现路线（2026-11 转向）：**纯软件强沙箱**。宿主 Windows 内核/用户态原语（AppContainer + Job Object + 后续 WinFsp 虚拟盘 + WFP）代替 Hypervisor 虚拟机。原因：Windows 11 家庭版没有 Containers 组件，HCS 容器不可用；HCS 虚拟机需要客机镜像，不符合“免镜像”要求。Hyper-V/HCS 相关代码仅保留为诊断工具，不再作为默认后端。

## 硬约束

- 默认后端是 `sandbox`，但 Responsible 不再默认启用 RVM：工作区需显式勾选“RVM 隔离保护”（新建工作区窗口或审批强度下拉菜单顶部）才会创建会话；未启用时继续使用 DSH 自带沙箱。
- 启动期门禁：`appcontainer`、`vbs`、`hvci` 全部可用才开放会话；`winfsp` 只影响虚拟工作区视图（P2），单独报告。失败时 Responsible 回退到 DSH 自带沙箱并提示“RVM 不可用”，不静默降级。
- 运行中引擎失联一律返回 `RVM_UNAVAILABLE`，不中途切换后端。
- AppContainer 子进程无法自行创建控制台；引擎自行持有一个隐藏控制台，否则子进程在加载器初始化阶段以 `STATUS_DLL_INIT_FAILED` 退出。
- 子进程只继承 stdin/stdout/stderr 三个句柄（`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`），避免宿主管道/句柄泄漏进沙箱。
- 工作区 ACL 授权失败（常见于卷根）不再直接判定会话失败；会话启动会真实执行一次沙箱内探测（写模式写入探针文件，只读模式枚举目录），以实际能力为准。
- 每个工作区使用独立存储目录 `%LOCALAPPDATA%\Responsible\RVM\<工作区名>_<6位工作区ID>\`（`diff/`、`tombstones/`）；会话关闭只卸载叠加盘，diff 保留给同一工作区后续会话复用，不再把所有工作区混在 `sessions/` 下。
- 每个会话有专属临时目录 `%LOCALAPPDATA%\Responsible\RVM\.rvm_temp\<工作区名>_<6位工作区ID>`；引擎把它设为子进程的 `TMP`/`TEMP`/`TMPDIR`，会话结束时清理，绝不回退到宿主 `%TEMP%`。
- 祖先目录 traverse 授权逐级记录，会话/诊断结束按逆序撤销；工作区 ACE 一并撤销。AppContainer profile 名带引擎 PID，避免与残留 profile 冲突。
- Node 插件永不 spawn 宿主进程；所有命令由引擎转发到沙箱执行。

## 目录

| 目录 | 职责 |
|---|---|
| `protocol/rvm/v1/` | v1 protobuf 契约（握手、会话、shell、fs、工作区、审计、RPC 信封） |
| `core/` | 错误模型、Result、四套状态机、引擎生命周期、日志 |
| `runtime/` | 后端抽象：`IEnvironmentBackend`、`IEnvironment`、`EnvironmentSpec/Facts` |
| `sandbox/` | AppContainer 配置/ACL/进程运行器，`SandboxBackend`/`SandboxEnvironment` |
| `environment/` | AppContainer/WinFsp/VBS/HVCI 探测与可用性聚合 |
| `hyperv/` | 旧 HCS/虚拟机路径，保留作诊断（`--hcs-*`），不参与默认服务 |
| `security/` | Windows 路径规范化/越界判定、masked glob 策略 |
| `channel/` | 固定帧头、帧 I/O、protobuf 编解码、域模型与协议映射 |
| `server/` | 命名管道服务、握手鉴权与 RPC 分发 |
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
RVM/build/bin/RvmEngine --sandbox-session <workspace> --overlay-drive R:  # 工作区经 WinFsp 叠加盘暴露
RVM/build/bin/RvmEngine --winfsp-probe [drive]                 # 直接挂载 UnionView 并做基本读写
RVM/build/bin/RvmEngine --winfsp-smoke <workspace> [drive]     # 端到端：挂载、受限令牌子进程写读、copy-up、tombstone
RVM/build/bin/RvmEngine --sandbox-smoke <workspace-path>       # cmd 内验证：工作目录、C:\Users、网络、工作区写入
RVM/build/bin/RvmEngine --sandbox-exec <workspace-path> <命令行> # 在沙箱中执行任意命令并回收输出
RVM/build/bin/RvmEngine --sandbox-debug <workspace-path> <命令行> # 调试器模式：打印 DLL 加载与异常
RVM/build/bin/RvmEngine --token-info                           # 在沙箱内运行可核对 AppContainer/完整性级别
printf 'engine-token\n' | RVM/build/bin/RvmEngine --pipe responsible-rvm-<pid> --token-stdin
RVM/build/bin/RvmEngine --shell-bridge --pipe <name> --session <id> -- <argv...>  # DSH 工具转发入口（token 见环境）
```

旧诊断（需要 Hyper-V/HCS，家庭版无 Containers 时不可用）：`--probe-hcs-service`、`--prepare-base-layer`、`--hcs-smoke`、`--hcs-probe-system`。

`--probe-availability` 只输出真实探测结果；`winfsp=failed` 表示尚未安装 WinFsp，叠加盘与 `--winfsp-smoke` 不可用（RVM 会话仍可用，工作区直连）。

## 宿主通道（P1）

- 管道名为 `\\.\pipe\<name>`、字节流、拒绝远程客户端；DACL 仅授予 SYSTEM 与当前用户。每个连接一个实例与线程，宿主与各工具桥可并发连接。
- 首次调用 `CreateNamedPipe` 使用 `FILE_FLAG_FIRST_PIPE_INSTANCE`，防止同名管道被其他进程抢先创建。
- 引擎 token 由调用方通过 stdin 传入（`--token-stdin`），不使用命令行参数，避免出现在进程命令行中。
- 客户端第一帧必须是 `RpcRequest` 且 body 为 `HelloRequest`；引擎校验协议版本、token（常量时间比较）和 `client_pid == GetNamedPipeClientProcessId`。失败返回 `RVM_UNAUTHENTICATED` 并断开。
- 握手成功后允许 `Ping`/`Shutdown`。`Shutdown` 会停止引擎、关闭全部 RVM 会话并将状态迁移到 `stopped`。
- `CreateSession`/`CloseSession`/`GetSession`/`ListSessions` 由 `SessionManager` 处理：可用时真实创建并启动沙箱环境（AppContainer 配置 + 工作区授权 + 真实探测），不可用时返回 `RVM_UNAVAILABLE` 并附带探测原因；额外可写根、`danger-full-access`、`network_access=true` 返回 `RVM_NOT_IMPLEMENTED`，不会静默忽略。
- shell、fs、workspace、audit 请求在 P1 返回 `RVM_UNAVAILABLE`（引擎不可用）或 `RVM_NOT_IMPLEMENTED`（引擎可用但阶段未到），不会伪造执行结果。
- 每次客户端断开后管道重建并继续接受同一用户的下一连接；token 不变。

## Shell 转发（P4）

- 引擎实现 `ShellExec`：校验会话与 argv，把宿主工作区内的 workdir 映射到会话叠加盘（越界即 `RVM_PATH_OUT_OF_SCOPE`，不做兜底），转发调用方的环境（剔除 `RESPONSIBLE_RVM_*`，并用会话专属目录覆盖 `TMP`/`TEMP`/`TMPDIR`），在会话的 AppContainer/受限令牌里启动进程，把 stdout/stderr 按 60 KiB 分片以流帧返回，并给出真实退出码、超时/取消标记与 `sandbox.enforcement`。
- `RvmEngine --shell-bridge --pipe <name> --session <id> [--timeout-ms N] -- <argv...>` 是模型工具的转发入口：从环境读取 token，握手后执行 argv，镜像 stdio 与退出码；连接失败写 `RVM_SANDBOX_UNAVAILABLE: ...` 并以 127 退出，超时 124、取消 130。
- DSH 侧由 `RDshPlugins/rvm-sandbox.mjs` 替换原 `sandbox` 服务：`confine()` 返回上述桥接 argv，`enforcement` 如实上报 `partial`（shell 已隔离，fs 工具仍直连宿主）；工作区不匹配、引擎/会话缺失或 `read-only` 模式一律 fail closed。
- 多客户端：每个连接独立线程，WPF 宿主与每个工具桥可同时连接；`Shutdown` 由客户端触发后会唤醒 accept 循环并收尾，不依赖外部 kill。

## 协议规则

- package 固定 `rvm.v1`；字段号只增不改，删除字段必须 `reserved`。`AvailabilityReport` 已由 `hypervisor/hcs_*` 改为 `appcontainer/winfsp/vbs/hvci`（3/6/7/8 reserved）。
- 顶层枚举值必须带类别前缀（例如 `RVM_*`、`SANDBOX_MODE_*`、`AUDIT_KIND_*`），避免 proto3 作用域冲突。
- 帧头固定 24 字节：magic `RVM1`（线上字节 `52 56 4D 31`）、header version、type、payload size、flags、request id；payload 上限 8 MiB。
- 不提供 JSON 临时协议。调试输出仅使用 `key=value` 文本。
- 未识别的错误码、策略枚举、帧类型一律报 `RVM_PROTOCOL_ERROR`，不做默认放行。

## 当前状态（P1–P2 完成，P4 shell 链已接通）

已完成：

- v1 契约与生成构建；错误/结果/状态机；路径逃逸与 masked 策略；
- AppContainer profile 创建/删除、SID 派生、目录 ACL 授权/撤销、祖先目录 traverse 授权；
- 沙箱进程运行器：`SECURITY_CAPABILITIES` + `HANDLE_LIST`、Job Object（内存/进程数/关闭即杀）、超时终止、stdio 捕获；
- `SandboxBackend`/`SandboxEnvironment`：启动时创建 profile、授权工作区、真实探测工作区可访问性，停止时撤销 ACL 并删除 profile；
- WinFsp 叠加盘（P2）：`UnionView`（base 只读 + diff 写 + tombstone）由 `MountedWorkspace` 挂载为盘符；`SandboxEnvironment` 支持 `overlay_workspace`/`overlay_drive`，会话把 `R:`（或首个空闲盘符）作为工作区路径返回，`base` 保持只读，写入落在按工作区分目录的 `diff`，停止会话时卸载并清理会话临时目录（diff 保留）；
- 可用性门禁（appcontainer/vbs/hvci）与 `winfsp` 报告；`SessionManager` 已切换到 `runtime::` 后端抽象，默认使用沙箱后端；
- shell 执行链（P4）：多客户端管道、`shell_exec` 流式输出、宿主路径到叠加盘的 workdir 映射、`RvmEngine --shell-bridge` 与 DSH `rvm-sandbox` 插件；宿主（WPF）在会话就绪时把引擎参数写入运行时 patch 并注入内核环境；
- 握手/鉴权/分发与会话生命周期保持通过；73 个自包含测试全部通过。

### WinFsp 叠加盘约束（实测）

- WinFsp 的 `FileContext` 在默认模式下是**文件节点**：同名文件的所有打开共享同一节点，`Cleanup` 按每次打开触发、`Close` 才是最终关闭。适配器按“节点 + 引用计数”实现，重复 Close 或未知指针只记录告警，不会二次释放。
- AppContainer 令牌无法在设备层打开 WinFsp 卷（[winfsp/winfsp#705](https://github.com/winfsp/winfsp/issues/705)）。因此挂载盘上的进程使用**受限令牌**（`DISABLE_MAX_PRIVILEGE` + 空 SID 列表 + Job Object）：可用但**没有 AppContainer 的 DACL 隔离**，属于已知残余风险；NTFS 直连工作区仍使用 AppContainer。
- 本版 diff 为工作区级（同一工作区复用，不再随会话删除）；盘符来自请求或 `--overlay-drive`；Responsible 设置→通用→WinFsp 的 `R:` 默认值与 diff 管理属于后续接线。

### 宿主实测（Windows 11 家庭版 25H2, build 26200）

- `--probe-availability`：`backend=sandbox`、`appcontainer=ok`、`vbs=ok`、`hvci=ok`、`winfsp=ok`、`rvm_available=true`（2026-11-21 已安装 WinFsp 2.2.26323，签名 NAVIMATICS LLC）；
- `--sandbox-session D:\...\smoke-ws`：`backend_ready=true`、`session_state=ready`、`windows_build=26200`、`session_state=stopped`；
- `--sandbox-smoke`：`cwd` 为工作区；`dir C:\Users` 返回“拒绝访问”；`ping baidu.com` 无法解析（无网络）；工作区内写读成功；
- 卷根（如 `D:\`）通常不允许当前用户改写 DACL，祖先 traverse 授权会失败；会话以真实探测为准，因此该警告不阻止会话；
- 已定位并修复：沙箱子进程因非继承 stdin 句柄导致 `STATUS_INVALID_HANDLE`/`STATUS_DLL_INIT_FAILED`；AppContainer 子进程需要可继承控制台，否则加载器初始化失败。

尚未实现：

- 叠加盘与 Responsible 设置（默认 `R:`、diff 清理策略、安装包随应用分发）的对接；
- masked 文件不可见/不可读、按 AppContainer SID 授权的命名管道 IPC（P3）；
- rfs 真实执行链（fs 工具仍直连宿主，`enforcement` 因此为 `partial`）、masked 文件对 shell 的强制不可见；
- 预热、模糊测试、性能加固。

## 安全不变量

- 宿主用户目录、凭据、SSH、浏览器数据永不被授权给 AppContainer；仅工作区目录（及其必要的祖先 traverse）显式授权。
- 路径先规范化再授权；拒绝 UNC、设备路径、ADS、保留名、尾点/空格、控制字符与越界 `..`。
- masked 判定发生在任何后端操作之前；P3 起由虚拟盘回调返回“存在但不可读”。
- VBS/HVCI 作为 RVM 可用条件；显式降级必须由用户配置覆盖并在 UI/提示词中标明。
- 审计只追加；日志不记录文件内容、凭据或命令环境变量值。
- 管道 ACL 限当前用户，校验 token 与对端 PID；帧、命令、输出均有硬上限。