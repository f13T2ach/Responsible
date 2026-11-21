> **实现状态（2026-11）**：隔离后端已从 HCS 虚拟机转为 AppContainer + Job Object 纯软件沙箱（家庭版可用，免镜像）。RvmEngine 仍是 Responsible.exe 启动的 C++ 子进程；通道、鉴权、会话与协议设计不变，`AvailabilityReport` 字段已改为 `appcontainer/winfsp/vbs/hvci`。当前进度见 [RVM/README.md](RVM/README.md)。

下面给出**修正后的 RVM + RDsh + Responsible.exe 实现大纲**。

核心修正点是：

1. **Responsible.exe 是 WPF 主进程，也是唯一的产品控制面。**
2. **RVM Engine 是 Responsible.exe 启动的 C++ 子进程，不是独立常驻系统服务。**
3. **RDsh 不是独立 Daemon，而是 Dsh 插件。**
4. **RDsh 插件只有在支持 RVM Capability 的宿主客户端中才生效。**
   - 装入 Responsible.exe 内置 Dsh/Harness：生效。
   - 装入普通 Deepseek Harness 客户端、其他不支持 RVM 的 Dsh 客户端：不注册敏感工具、不改写 shell/fs、不接触凭证、不产生任何代理能力。
5. **所有强制安全边界在 Responsible.exe + RVM Engine 中实现，而不是依赖 RDsh 插件自觉。**
6. **Node.js/RDsh 不直接拥有宿主机执行权。敏感执行必须通过 Responsible Host Broker + RVM Engine。**

---

# 1. 修正后的总体架构

```text
┌──────────────────────────────────────────────────────────────┐
│ Responsible.exe / WPF 主进程                                  │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ UI / Session Manager / Policy Store / Approval Center   │  │
│  └────────────────────────────────────────────────────────┘  │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ Dsh / Harness Runtime                                  │  │
│  │                                                        │  │
│  │  ┌──────────────────────────────────────────────────┐  │  │
│  │  │ RDsh Dsh 插件                                    │  │  │
│  │  │ - capability-gated                               │  │  │
│  │  │ - Git / SSH / pip / Docker 等工具适配器          │  │  │
│  │  │ - 只生成受控执行计划，不直接拿凭证               │  │  │
│  │  └──────────────────────────────────────────────────┘  │  │
│  └────────────────────────────────────────────────────────┘  │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ Responsible Host Broker                                │  │
│  │ - 策略裁决                                             │  │
│  │ - 审批                                                 │  │
│  │ - 凭证引用管理                                         │  │
│  │ - 审计                                                 │  │
│  │ - 调用 RVM Engine                                      │  │
│  └───────────────────────┬────────────────────────────────┘  │
│                          │ Named Pipe / IPC                  │
└──────────────────────────┼───────────────────────────────────┘
                           │
                           ▼
┌──────────────────────────────────────────────────────────────┐
│ RvmEngine.exe / C++ 子进程                                    │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ RVM Core C++ Library                                   │  │
│  │ - VM / MicroVM / Sandbox Backend                       │  │
│  │ - Workspace CoW                                        │  │
│  │ - Masked FS：敏感文件存在但不可读                       │  │
│  │ - rshell 通道                                           │  │
│  │ - AppContainer / Restricted Token / Job Object          │  │
│  │ - Host Tool Worker 管理                                 │  │
│  └────────────────────────────────────────────────────────┘  │
└──────────────────────────┬───────────────────────────────────┘
                           │ hvsocket / vsock / 受控共享通道
                           ▼
┌──────────────────────────────────────────────────────────────┐
│ RVM Guest                                                    │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ Windows-like 用户态环境                                │  │
│  │ PowerShell / CMD / 基础工具 / rshell                   │  │
│  │                                                        │  │
│  │ - 无宿主真实用户目录                                    │  │
│  │ - 无 SSH 私钥                                           │  │
│  │ - 无 Git 凭证                                           │  │
│  │ - 无浏览器 Cookie                                       │  │
│  │ - 工作区为 CoW 沙箱视图                                 │  │
│  └────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────┘
```

---

# 2. 进程与职责边界

## 2.1 Responsible.exe：WPF 主进程

`Responsible.exe` 是产品外壳和策略中心。

它负责：

| 模块 | 职责 |
|---|---|
| WPF UI | 会话管理、审批弹窗、审计视图、Rollback / Sync 操作 |
| Session Manager | 创建 RVM 会话、绑定 workspace、绑定工具策略 |
| Policy Store | 保存用户配置的穿透规则、敏感文件 mask、插件权限 |
| Approval Center | 风险操作挂起、用户批准/拒绝、TTL |
| Credential Broker | 管理 Git/SSH/Docker/pip 凭证引用，但不把明文交给 AI |
| Dsh/Harness Runtime | 加载模型插件、工具插件、RDsh 插件 |
| Host Broker | RDsh 与 RVM Engine 之间的唯一受控桥梁 |
| Engine Supervisor | 启动、监控、终止 RvmEngine.exe 子进程 |

关键点：

> **Responsible.exe 不直接把宿主机权限暴露给 Node.js 插件。RDsh 只能调用 Host Broker 提供的窄 API。**

---

## 2.2 RvmEngine.exe：C++ 子进程

`RvmEngine.exe` 是 `Responsible.exe` 启动的子进程。

它可以由 C++ executable 包装一个 C++ library：

```text
RvmEngine.exe
  └── rvm_core.dll / rvm_core.lib
```

职责：

| 模块 | 职责 |
|---|---|
| VM Backend | 创建/停止 RVM Guest |
| Workspace Sandbox | CoW 工作区、Diff 层、Rollback、Sync 准备 |
| Masked FS | 敏感文件可见但不可读 |
| rshell Channel | 接收 Guest 内 rshell 的 Session |
| Process Sandbox | AppContainer、Restricted Token、Job Object |
| Tool Worker | 执行由 Host Broker 批准的宿主侧工具 |
| Audit Native Events | 文件、进程、网络、拒绝事件上报 |

关键点：

> **RvmEngine.exe 不做产品级策略决策。它执行 Responsible.exe 下发的策略，并提供底层隔离能力。**

例如：

- “这个 Git push 是否需要审批”由 Responsible.exe 决定。
- “如何以受限 token 启动 git.exe”由 RvmEngine.exe 实现。
- “哪些文件不可读”由 Policy Store 配置，由 RvmEngine.exe 强制执行。

---

## 2.3 RDsh：Dsh 插件，而不是 Daemon

RDsh 的实际形态应该是：

```text
@responsible/dsh-rdsh
```

或者：

```text
rdsh-plugin/
  package.json
  dist/
    index.js
    plugins/
      git.js
      ssh.js
      pip.js
      docker.js
      runiverse.js
```

它被 Dsh/Harness 加载。

但是，RDsh 必须做 **Capability Gate**：

```ts
export function activate(ctx: DshPluginContext) {
  const rvm = ctx.capabilities.get("responsible.rvm.v1");

  if (!rvm) {
    // 装入普通 Deepseek Harness 客户端时走这里
    // 不注册工具，不改写 shell，不连接宿主机，不申请凭证
    ctx.logger.info("[RDsh] inactive: RVM capability unavailable");
    return;
  }

  // 只有在 Responsible.exe 内部，且当前会话绑定了 RVM，才激活
  registerRDshTools(ctx, rvm);
}
```

因此：

| 运行环境 | RDsh 行为 |
|---|---|
| Responsible.exe 内置 Dsh/Harness | 生效 |
| 普通 Deepseek Harness 客户端 | 静默失效 |
| 其他没有 RVM Capability 的客户端 | 静默失效 |
| Responsible.exe 中但当前会话未启用 RVM | 不注册敏感工具，或返回 `RVM_SESSION_REQUIRED` |

这点非常重要：

> **RDsh 插件不是“安装后就能代理宿主机”的插件。它只有在 Responsible 提供的 RVM Capability 中才有功能。**

---

# 3. IPC 耦合方式

## 3.1 Responsible.exe ↔ RvmEngine.exe

建议使用：

```text
Named Pipe + Protobuf / MessagePack / JSON-RPC
```

更推荐：

```text
Named Pipe + Protobuf
```

原因：

- C++ / C# / Node.js 都容易支持。
- 可以强类型约束。
- Named Pipe 可以设置 DACL。
- 可以通过父子进程继承句柄防止 pipe squatting。
- 不需要开放 TCP 端口。

启动方式：

```text
Responsible.exe
  └── CreateProcess("RvmEngine.exe", "--parent-pid=... --pipe=...")
```

管道名不要使用固定全局名字，例如不要用：

```text
\\.\pipe\rvm
```

而应该使用随机实例名：

```text
\\.\pipe\Responsible.{install-id}.{pid}.{random}.engine
```

并由 Responsible.exe 创建 server，RvmEngine.exe 连接。

启动后握手：

```json
{
  "type": "ENGINE_HELLO",
  "engine_version": "0.1.0",
  "protocol_version": "rvm-engine.v1",
  "pid": 12345,
  "capabilities": [
    "workspace.cow",
    "fs.mask",
    "process.appcontainer",
    "guest.rshell",
    "network.proxy"
  ]
}
```

Responsible.exe 校验：

1. pipe peer PID 是否是自己启动的子进程。
2. 镜像签名是否匹配。
3. protocol version 是否兼容。
4. engine capability 是否满足当前会话策略。

---

## 3.2 RDsh ↔ Responsible Host Broker

RDsh 不直接连接 RvmEngine.exe。

RDsh 只拿到 Dsh 插件上下文中的 capability：

```ts
interface ResponsibleRvmCapability {
  sessionId: string;

  submitToolSession(session: RShellSession): Promise<ToolResult>;

  requestApproval(req: ApprovalRequest): Promise<ApprovalDecision>;

  getWorkspaceView(): Promise<WorkspaceView>;

  invokeHostPlan(plan: HostExecutionPlan): Promise<ToolResult>;

  audit(event: AuditEvent): Promise<void>;
}
```

也就是说：

```text
RDsh Plugin
   │
   ▼
ctx.capabilities["responsible.rvm.v1"]
   │
   ▼
Responsible Host Broker
   │
   ▼
RvmEngine.exe
```

而不是：

```text
RDsh Plugin ───直接───> RvmEngine.exe
```

更不是：

```text
RDsh Plugin ───child_process.spawn───> git.exe / docker.exe / ssh.exe
```

对于敏感工具，**Node.js 插件不应该直接 spawn 宿主进程**。

---

# 4. RVM Infrastructure 实现大纲

## 4.1 后端抽象

RVM Engine 中定义统一后端接口：

```cpp
class IRvmBackend {
public:
    virtual RvmSessionId CreateSession(const RvmSessionConfig&) = 0;
    virtual void StartGuest(RvmSessionId) = 0;
    virtual void StopGuest(RvmSessionId) = 0;

    virtual WorkspaceView MountWorkspace(
        RvmSessionId,
        const WorkspaceMountConfig&
    ) = 0;

    virtual RshellChannel OpenRshellChannel(RvmSessionId) = 0;

    virtual ProcessHandle CreateRestrictedProcess(
        const RestrictedProcessSpec&
    ) = 0;
};
```

可插拔后端：

```text
backend/
  windows_sandbox_backend.cpp
  hcs_backend.cpp
  hyperv_backend.cpp
  whp_microvm_backend.cpp
```

需要注意：

> 不建议把 Windows Sandbox 的私有实现细节当作稳定公开 SDK。可以抽象为后端能力，MVP 用可用方案验证，生产再替换为稳定的 HCS / Hyper-V / WHP 路径。

上层 RVM 不关心底层到底是：

- Windows Sandbox 风格轻量环境；
- HCS Utility VM；
- Hyper-V 最小 Windows Guest；
- WHP MicroVM；
- 其他隔离后端。

上层只关心：

```text
能否提供 Guest 边界？
能否提供 rshell？
能否提供 CoW workspace？
能否提供受限宿主执行？
```

---

## 4.2 Guest 原则

Guest 内必须满足：

1. 不挂载宿主真实用户目录。
2. 不复制宿主 `.ssh`、`.gitconfig`、`.aws`、`.kube`、浏览器 profile。
3. 环境变量只保留 RVM 生成的安全副本。
4. 工作区是沙箱视图，不是宿主原目录直写。
5. 默认无宿主网络。
6. 敏感工具默认不可用，必须走 RDsh。
7. rshell 是唯一对外请求通道。

---

# 5. Workspace 沙箱实现大纲

## 5.1 工作区结构

假设用户项目是：

```text
D:\Projects\App
```

RVM 为每个会话创建：

```text
D:\Projects\App\.rvm\
  sessions\
    sess-123\
      base-manifest.json
      diff\
      deleted\
      audit.log
      policy.snapshot.json
```

逻辑视图：

```text
Base: D:\Projects\App 原始文件
Diff: .rvm\sessions\sess-123\diff
Mask: 用户配置的敏感文件规则
```

Guest 看到：

```text
Z:\Workspace
```

宿主工具看到：

```text
X:\RVM\sess-123\Workspace
```

其中：

- `Z:\Workspace` 给 Guest AI 使用。
- `X:\RVM\sess-123\Workspace` 给宿主侧 RDsh 工具使用。
- 两者都指向同一个 CoW 合并视图。

---

## 5.2 CoW 行为

| 操作 | 行为 |
|---|---|
| read 未修改文件 | 读 base |
| read 已修改文件 | 读 diff |
| write 文件 | 复制到 diff 后修改 |
| delete 文件 | 在 deleted manifest 中记录 tombstone |
| rename 文件 | 记录 rename op |
| rollback | 丢弃 diff / deleted / manifest |
| sync | 用户审计后把 diff 应用回 base |

Sync 前必须做冲突检测：

```text
base_hash_at_session_start == current_base_hash
```

如果不一致，说明宿主文件在会话期间被别人改过，需要 UI 展示冲突。

---

## 5.3 敏感文件“存在但不可读”

Policy 示例：

```json
{
  "masked_files": [
    "**/.env",
    "**/.env.*",
    "**/.ssh/**",
    "**/.aws/credentials",
    "**/.git-credentials",
    "**/pip.ini",
    "**/pip.conf",
    "**/.npmrc",
    "**/.docker/config.json"
  ],
  "metadata_mode": "minimal"
}
```

行为：

| FS 操作 | 返回 |
|---|---|
| list dir | 可以看到文件名 |
| stat | 可以返回基础元数据 |
| open read | 拒绝 |
| read content | 拒绝 |
| hash content | 默认拒绝 |
| copy | 拒绝 |
| rename/delete | 根据策略拒绝或进入审批 |

错误码必须明确：

```json
{
  "code": "RVM_MASKED_FILE",
  "message": "This file exists but is masked by Responsible policy.",
  "path": ".env",
  "suggestion": "Use an RDsh plugin or ask the user for a non-secret value."
}
```

不要用模糊错误：

```text
FILE_NOT_FOUND
```

否则 AI 会误判为文件不存在，然后不断换路径尝试。

---

# 6. 工具链策略性穿透

工具不要简单分成“VM 内执行”和“宿主执行”。

应该分四类。

---

## 6.1 VM-local 工具

例如：

```text
python
node
pytest
npm test
cargo test
```

默认在 Guest 内运行。

特点：

- 不拿宿主凭证。
- 不访问宿主用户目录。
- 修改只进 CoW diff。
- 失败就返回普通执行结果。

路由：

```text
AI -> Dsh tool -> rshell -> Guest local exec
```

---

## 6.2 策略性穿透工具：pip / npm / cargo / winget

这类工具需要网络、缓存、私有源配置，但不能把配置里的 token 给 AI。

以 pip 为例：

### pip 策略

```json
{
  "tool": "pip",
  "mode": "brokered",
  "allowed_indexes": [
    "https://pypi.org/simple",
    "https://mirrors.example.com/pypi/simple"
  ],
  "private_index_credential": "cred:pypi-readonly",
  "mask_host_config": true,
  "install_target": "guest",
  "allow_arbitrary_index_url": false
}
```

执行流程：

```text
Guest: pip install requests
        │
        ▼
rshell 捕获 Session
        │
        ▼
RvmEngine -> Responsible Host Broker
        │
        ▼
RDsh pip 插件解析意图
        │
        ▼
Responsible 检查 index / package / credential policy
        │
        ▼
宿主侧代理下载或生成一次性认证参数
        │
        ▼
Guest 内安装到 VM 环境
```

关键原则：

> pip 的私有源 token 可以被子系统用于下载，但不能以明文文件、环境变量、命令行参数形式暴露给 AI。

---

## 6.3 Docker 插件

Docker 是高危工具，因为以下操作等价于宿主控制权：

```bash
docker run -v C:\Users:/host
docker run --privileged
docker run -v /var/run/docker.sock:/var/run/docker.sock
docker run --network host
```

所以 Docker 不能简单透传。

RDsh Docker 插件必须把命令转换成结构化计划：

```json
{
  "tool": "docker",
  "operation": "build",
  "context": "workspace",
  "dockerfile": "Dockerfile",
  "allow_host_mounts": false,
  "allow_privileged": false,
  "allow_docker_socket": false,
  "network_policy": "restricted"
}
```

默认禁止：

```text
--privileged
--cap-add
--pid=host
--network=host
-v 宿主敏感目录
挂载 docker.sock
读取 ~/.docker/config.json
```

允许用户显式配置：

```json
{
  "docker": {
    "enabled": true,
    "allowed_registries": ["ghcr.io", "docker.io"],
    "allow_publish": false,
    "allow_host_mounts": false,
    "build_context": "cow_workspace_only"
  }
}
```

---

## 6.4 敏感工具透传：Git / SSH

Git 和 SSH 的重点不是“能不能执行”，而是：

> AI 可以使用凭证完成有限操作，但永远看不到凭证。

### Git 插件

允许的操作应该结构化：

```text
rdsh.git.status
rdsh.git.diff
rdsh.git.add
rdsh.git.commit
rdsh.git.push
rdsh.git.pull
```

而不是：

```text
run("git " + arbitrary_args)
```

Git push 示例：

```json
{
  "tool": "git",
  "operation": "push",
  "remote": "origin",
  "branch": "feature/fix",
  "workspace_view": "cow",
  "credential_ref": "cred:github-push",
  "risk": "remote_write",
  "requires_approval": true
}
```

凭证注入方式：

| 协议 | 推荐方式 |
|---|---|
| SSH | 宿主 ssh-agent 签名代理，不暴露私钥 |
| HTTPS | 一次性 askpass/helper，只给当前进程 |
| Git Credential Manager | 由 Host Broker 调用，不把 token 返回给插件 |
| PAT | 存 Windows Credential Manager / DPAPI，不进入 Guest |

禁止：

```text
把 id_rsa 复制进 VM
把 token 写进环境变量后返回给 AI
把 .git-credentials 映射进 VM
把完整 GIT_ASKPASS 输出返回给 AI
```

---

### SSH 插件

SSH 默认不应该提供任意 shell。

不建议开放：

```bash
ssh user@host
```

而应该提供：

```text
rdsh.ssh.exec
rdsh.ssh.sftp_read
rdsh.ssh.sftp_write
```

并要求用户配置：

```json
{
  "ssh": {
    "enabled": true,
    "allowed_hosts": ["deploy.example.com"],
    "allowed_users": ["deploy"],
    "allowed_commands": [
      "systemctl status myapp",
      "ls /srv/myapp/releases"
    ],
    "allow_interactive_shell": false,
    "credential_ref": "cred:deploy-ssh"
  }
}
```

---

# 7. runiverse 兜底插件

`runiverse` 的定位必须非常清楚：

> runiverse 不是通用宿主 shell。  
> runiverse 是无专属 RDsh 插件时的受限兼容层。

默认：

```json
{
  "runiverse": {
    "enabled": false
  }
}
```

用户显式启用后：

```json
{
  "runiverse": {
    "enabled": true,
    "require_approval": true,
    "executor": "appcontainer",
    "allow_credentials": false,
    "allowed_dirs": ["cow_workspace"],
    "network": "deny_by_default"
  }
}
```

执行路径：

```text
AI -> rshell Session
   -> RDsh runiverse
   -> Responsible Host Broker
   -> Approval
   -> RvmEngine.CreateRestrictedProcess
   -> AppContainer / Restricted Token / Job Object
```

禁止：

```text
Node.js child_process.spawn 直接执行
使用当前用户 token 执行
注入宿主凭证
访问 C:\Users\RealUser
访问宿主 SSH / Git / Docker 配置
```

即使人工误批，底层仍然应该尽量做到：

```text
能执行，但看不到敏感文件；
能输出，但拿不到凭证；
能失败，但不能控制宿主机。
```

---

# 8. RShell Session 协议

Guest 内所有需要穿透的请求都封装为 Session。

```json
{
  "protocol": "rshell.session.v1",
  "session_id": "sess-123",
  "request_id": "req-456",
  "source": "guest",
  "tool": "git",
  "argv": ["push", "origin", "feature/fix"],
  "cwd": "Z:\\Workspace",
  "env": {
    "PATH": "...",
    "GIT_TERMINAL_PROMPT": "0"
  },
  "stdin_ref": null,
  "capability_token": "guest-token-..."
}
```

RvmEngine 收到后不直接执行，而是转给 Responsible Host Broker：

```json
{
  "type": "TOOL_SESSION_RECEIVED",
  "session_id": "sess-123",
  "request_id": "req-456",
  "tool": "git",
  "argv": ["push", "origin", "feature/fix"],
  "guest_cwd": "Z:\\Workspace"
}
```

Host Broker 再交给 RDsh 插件：

```text
RDsh Git Plugin:
  parse argv
  validate operation
  generate HostExecutionPlan
```

RDsh 输出：

```json
{
  "plan_version": "rdsh.host-plan.v1",
  "executor": "git",
  "operation": "push",
  "cwd": "X:\\RVM\\sess-123\\Workspace",
  "args": ["push", "origin", "feature/fix"],
  "credential_refs": ["cred:github-push"],
  "network_allowlist": ["github.com:443"],
  "risk_category": "remote_write",
  "requires_approval": true
}
```

Responsible.exe 审批后调用 RVM Engine 执行。

---

# 9. C++ / C# / Node.js 耦合接口

## 9.1 C# 启动 C++ 子进程

```csharp
var pipeName = $"Responsible.{Guid.NewGuid():N}.engine";
var fullPipeName = $@"\\.\pipe\{pipeName}";

// 创建带 DACL 的 NamedPipeServerStream
// 省略安全描述符细节：只允许当前用户 + 子进程连接

var enginePath = Path.Combine(appDir, "native", "RvmEngine.exe");

var psi = new ProcessStartInfo
{
    FileName = enginePath,
    Arguments = $"--parent-pid={Environment.ProcessId} --pipe={pipeName}",
    UseShellExecute = false,
    CreateNoWindow = true
};

var engineProcess = Process.Start(psi);

// await engine hello
// 校验 pid/version/capability
```

生产实现中建议：

- 使用 Job Object 绑定子进程，Responsible 退出时 Engine 自动退出。
- 使用心跳。
- Engine crash 时所有 RVM session fail-closed。
- 禁止 RDsh 继续返回“可用”。

---

## 9.2 C++ Engine IPC Handler

```cpp
struct EngineContext {
    DWORD parentPid;
    std::wstring pipeName;
    std::unique_ptr<IRvmBackend> backend;
};

int wmain(int argc, wchar_t** argv) {
    auto ctx = ParseArgs(argc, argv);

    if (!VerifyParentProcess(ctx.parentPid)) {
        return 1;
    }

    auto channel = ConnectParentPipe(ctx.pipeName);

    EngineHello hello;
    hello.protocol_version = "rvm-engine.v1";
    hello.capabilities = {
        "workspace.cow",
        "fs.mask",
        "guest.rshell",
        "process.appcontainer"
    };

    channel.Send(hello);

    EventLoop(channel, ctx);
}
```

---

## 9.3 RDsh 插件 Capability Gate

```ts
import type { DshPluginContext } from "@dsh/plugin-api";

export function activate(ctx: DshPluginContext) {
  const rvm = ctx.capabilities.get("responsible.rvm.v1");

  if (!rvm) {
    ctx.logger.info("[RDsh] RVM capability not found; plugin inactive.");
    return;
  }

  if (!rvm.sessionId) {
    ctx.logger.info("[RDsh] No active RVM session; plugin inactive.");
    return;
  }

  ctx.tools.register("rdsh.git", createGitTool(rvm));
  ctx.tools.register("rdsh.pip", createPipTool(rvm));
  ctx.tools.register("rdsh.docker", createDockerTool(rvm));

  if (rvm.policy.runiverse.enabled) {
    ctx.tools.register("rdsh.runiverse", createRuniverseTool(rvm));
  }
}
```

普通 Deepseek Harness 客户端没有：

```ts
ctx.capabilities.get("responsible.rvm.v1")
```

所以 RDsh 不生效。

---

# 10. 审批与审计闭环

所有高风险操作都进入统一状态机：

```text
RECEIVED
  -> PARSED
  -> POLICY_CHECKED
  -> PENDING_APPROVAL
  -> APPROVED / DENIED / EXPIRED
  -> DISPATCHED
  -> DONE / FAILED
```

返回给 AI 的 pending 结果必须是合法工具结果：

```json
{
  "status": "PENDING_APPROVAL",
  "request_id": "req-456",
  "tool": "git",
  "operation": "push",
  "preview": "git push origin feature/fix",
  "risk_category": "remote_write",
  "message": "This operation has not executed yet. Waiting for user approval.",
  "resume_protocol": "event"
}
```

AI 不能把 pending 理解成执行成功。

WPF UI 展示：

```text
工具：Git
操作：push
远端：origin
分支：feature/fix
工作区：D:\Projects\App
风险：写入远端仓库
凭证：GitHub push credential，不会暴露给 AI
[批准] [拒绝] [查看详情]
```

---

# 11. 提示词工程大纲

提示词只负责让 AI 正确理解环境，不负责安全本身。

安全必须由 RVM Engine 和 Host Broker 强制执行。

---

## 11.1 环境认知

```markdown
你运行在 Responsible 管理的 RVM 会话中。

你的文件系统不是完整宿主机。
你看到的工作区是 CoW 沙箱视图。
你的用户目录是虚拟用户目录，不包含宿主真实凭证。
宿主机的 SSH key、Git token、Docker 登录信息、pip 私有源 token 不会出现在你的环境中。
```

---

## 11.2 工具路由规则

```markdown
工具分为四类：

1. VM-local：
   普通开发命令在 RVM Guest 内执行，例如 python、node、pytest。

2. Policy passthrough：
   pip、npm、winget 等工具可能需要网络或私有源，由 Responsible 策略代理。

3. RDsh sensitive tools：
   Git、SSH、Docker 等敏感工具必须使用 rdsh.* 工具。
   你不能直接读取或要求凭证。

4. runiverse：
   仅在用户启用时可用，是受限兼容层，不是宿主 shell。
```

---

## 11.3 敏感文件语义

```markdown
如果你看到 .env、.ssh/id_rsa、.git-credentials、.npmrc、pip.conf 等文件存在，
但读取时返回 RVM_MASKED_FILE，这表示：

文件存在；
内容被 Responsible 策略隐藏；
这不是 FILE_NOT_FOUND；
不是权限 bug；
也不能通过换工具、换路径、编码、符号链接、压缩、复制来绕过。

正确行为：
说明你需要哪个非敏感字段，或请求用户通过安全方式提供。
```

---

## 11.4 凭证规则

```markdown
你不能要求用户把私钥、token、密码粘贴到对话中。
你不能尝试读取宿主凭证。
你不能把凭证写入代码、日志、命令行参数或测试文件。

当你需要认证操作时，使用对应 RDsh 插件。
RDsh 会通过 Responsible Host Broker 使用凭证引用完成操作。
你只会看到操作结果，不会看到凭证明文。
```

---

## 11.5 Pending 规则

```markdown
如果工具返回 PENDING_APPROVAL：

表示操作尚未执行。
你不能重试同一操作。
你不能换成 runiverse 或其他写法绕过审批。
你应告诉用户正在等待审批，并暂停当前操作分支。
```

---

## 11.6 非 RVM 客户端语义

这点专门给 RDsh 插件和提示词使用：

```markdown
如果当前客户端没有 responsible.rvm.v1 capability，
则 RDsh 工具不可用。

你不能假设自己处于 RVM。
你不能声称拥有宿主代理能力。
你不能尝试连接 Responsible 的本地 IPC。
你应使用当前客户端本身提供的普通工具能力。
```

---

# 12. 推荐目录结构

```text
responsible/
├── src/
│   └── Responsible.App/              # WPF
│       ├── App.xaml.cs
│       ├── Services/
│       │   ├── EngineSupervisor.cs
│       │   ├── HostBroker.cs
│       │   ├── ApprovalService.cs
│       │   ├── PolicyStore.cs
│       │   └── CredentialBroker.cs
│       ├── ViewModels/
│       └── Views/
│
├── native/
│   ├── rvm-core/                     # C++ library
│   │   ├── backend/
│   │   ├── workspace/
│   │   ├── fs_mask/
│   │   ├── rshell/
│   │   ├── sandbox/
│   │   └── audit/
│   │
│   └── rvm-engine/                   # C++ executable
│       ├── main.cpp
│       ├── ipc/
│       └── engine_service.cpp
│
├── packages/
│   ├── dsh-rdsh/                     # Dsh 插件主包
│   │   ├── src/
│   │   │   ├── index.ts
│   │   │   ├── capability.ts
│   │   │   ├── session.ts
│   │   │   └── tools/
│   │   └── package.json
│   │
│   ├── rdsh-git/
│   ├── rdsh-ssh/
│   ├── rdsh-pip/
│   ├── rdsh-docker/
│   └── rdsh-runiverse/
│
├── proto/
│   ├── rvm_engine.proto
│   ├── host_broker.proto
│   └── rshell_session.proto
│
└── policies/
    ├── default-policy.json
    └── schemas/
```

---

# 13. 分阶段落地路线

## Phase 0：契约冻结

产物：

- RVM Engine IPC schema
- RShell Session schema
- RDsh Capability API
- HostExecutionPlan schema
- 错误码表
- Policy schema

关键错误码：

```text
RVM_UNAVAILABLE
RVM_SESSION_REQUIRED
RVM_CAPABILITY_MISSING
RVM_MASKED_FILE
RVM_PATH_OUT_OF_SCOPE
RDsh_PLUGIN_MISSING
RDsh_TOOL_DENIED
RUNIVERSE_DISABLED
PENDING_APPROVAL
APPROVAL_DENIED
APPROVAL_EXPIRED
CREDENTIAL_UNAVAILABLE
NETWORK_DENIED
```

---

## Phase 1：Responsible.exe 启动 RvmEngine.exe 子进程

目标：

- WPF 能启动 Engine。
- Named Pipe 握手成功。
- Engine crash 能被 WPF 感知。
- WPF 退出时 Engine 自动退出。
- 不支持 RVM 的客户端中 RDsh 不生效。

---

## Phase 2：Workspace CoW + Masked FS

目标：

- Guest 看到工作区。
- 写入进入 diff。
- 敏感文件存在但不可读。
- Rollback 可丢弃 diff。
- Sync 前能生成审计 diff。

---

## Phase 3：RDsh Capability 插件闭环

目标：

- Responsible 内置 Dsh 加载 RDsh。
- RDsh 检测 `responsible.rvm.v1`。
- 注册 `rdsh.git` / `rdsh.pip` 等工具。
- 普通 Deepseek Harness 客户端加载同一插件时无作用。

---

## Phase 4：Git / pip 插件 MVP

优先做 Git 和 pip。

Git：

- status
- diff
- add
- commit
- push 需要审批
- 不暴露 SSH key / token

pip：

- VM 内安装
- index allowlist
- 私有源凭证代理
- mask pip.conf / pip.ini

---

## Phase 5：Docker / SSH / runiverse

Docker：

- 禁止危险 mount。
- 禁止 docker.sock。
- build context 只能是 CoW workspace。

SSH：

- 默认不允许交互式 shell。
- 只允许 host/user/command allowlist。

runiverse：

- 默认关闭。
- 启用后必须走 AppContainer。
- 不注入凭证。
- 不访问用户目录。

---

## Phase 6：审计、审批、提示词回归

目标：

- WPF 审批 UI。
- pending 状态不误判。
- masked file 不绕过。
- runiverse 不越权。
- RDsh 在非 RVM 客户端中完全 inert。
- 提示词注入红队测试。

---

# 14. 最重要的安全不变量

最后把这套架构的安全不变量固定下来：

```text
1. RDsh 不是权限主体，只是 Dsh 插件。
2. RDsh 只有拿到 responsible.rvm.v1 capability 才生效。
3. 非 RVM 客户端中，RDsh 不注册敏感工具、不改写执行路径。
4. Responsible.exe 是唯一策略与审批中心。
5. RvmEngine.exe 是 Responsible.exe 子进程。
6. Guest 永远没有宿主真实用户目录和凭证。
7. 工作区永远是 CoW 视图，Sync 必须经过审计。
8. 敏感文件可以存在，但内容不可读。
9. 敏感工具通过 RDsh 插件生成执行计划，再由 Host Broker 执行。
10. Node.js 插件不得直接 spawn 敏感宿主工具。
11. runiverse 默认关闭，启用后也必须无凭证、受限执行。
12. 所有拒绝必须返回明确错误码，不能伪装成 FILE_NOT_FOUND。
```

这样修正后，整个系统的边界会清晰很多：

- **WPF 是控制面。**
- **C++ Engine 是隔离与执行底座。**
- **RDsh 是 Dsh 生态中的工具适配插件。**
- **普通客户端没有 RVM Capability，因此 RDsh 不会变成宿主代理后门。**
- **凭证、审批、敏感文件、防绕过都由 Responsible/RVM 强制实现，而不是依赖提示词或插件自觉。**