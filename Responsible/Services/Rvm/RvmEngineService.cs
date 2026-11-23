using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
namespace Responsible.Services;

// 宿主侧 RVM 引擎客户端：启动 RvmEngine.exe，经命名管道完成握手与会话生命周期。
// 帧头固定 24 字节（RVM1、版本、类型、负载大小、flags、request id），负载为 rvm.v1 protobuf。
public sealed class RvmEngineService : IRvmEngineService
{
    private const int HeaderSize = 24;
    private const ushort FrameVersion = 1;
    private const ushort FrameRequest = 1;
    private const ushort FrameResponse = 2;
    private const uint MaxPayloadBytes = 8 * 1024 * 1024;
    // 串行化后最多只有一个在途请求，迟到回包数量有界；用它防止错帧时死等
    private const int MaxStaleResponses = 8;
    private const int ConnectTimeoutMs = 30000;
    private const int RequestTimeoutMs = 120000;

    // 引擎接受 shutdown 之后的收尾预算。正常收尾是毫秒级（实测 3-30ms），这个上限只处理一种外部无法区分的情况：
    // 引擎卡在收尾里时，“还在收尾”和“已经卡死”在宿主侧看到的是同一个状态（进程还活着、通道已关闭），
    // 所以只能给收尾一个上限；超过即认定引擎无法自愈，结束进程树。可经环境变量覆盖，0 表示不设上限。
    private static readonly TimeSpan EngineTeardownBudget = ReadTeardownBudget();

    private static TimeSpan ReadTeardownBudget()
    {
        var configured = Environment.GetEnvironmentVariable("RESPONSIBLE_RVM_TEARDOWN_BUDGET_MS");
        if (int.TryParse(configured, out var ms)) return TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return TimeSpan.FromSeconds(10);
    }

    private static readonly byte[] Magic = "RVM1"u8.ToArray();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycleLock = new();
    // 停止态：拒绝新请求，并取消在途/排队的 RPC，让关闭流程等的是状态迁移而不是时间
    private volatile bool _stopping;
    private readonly CancellationTokenSource _abortCts = new();
    private Task? _shutdownTask;
    private readonly StringBuilder _engineOutput = new();
    private NamedPipeClientStream? _pipe;
    private Process? _process;
    private ulong _nextRequestId = 1;
    private bool _connected;
    private volatile string _status = "RVM engine not started";
    private string? _enginePath;
    private string? _pipeName;
    private string? _token;

    public bool IsRunning => _process is { HasExited: false } && _connected;

    public RvmEngineService() => ReleaseStaleDriveMappings();

    /// <summary>
    /// 退出时再清一次：引擎正常 Stop 会自己 subst /D，但被强杀或超时收尾时会漏，
    /// 这里兜底，保证窗口关掉之后盘符不留在系统里。
    /// </summary>
    public void CleanUpDriveMappings() => ClearStaleMappings();

    public bool IsAvailable { get; private set; }

    public string Status => _status;

    // 壳转发所需的真实连接参数：仅在引擎已启动并完成握手后可供 patch 使用
    public string? EnginePath => _enginePath;
    public string? PipeName => _pipeName;
    public string? Token => _token;

    public async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        try
        {
            if (_stopping) return false;
            return await EnsureRunningCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 关闭流程取消在途/排队的启动请求：这是停止态的正常结果，不是错误
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SetToolAuthorizationsAsync(
        IReadOnlyList<RvmToolAuthorization> tools,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping) return false;
            if (!await EnsureRunningCoreAsync(cancellationToken).ConfigureAwait(false)) return false;

            var body = new List<byte>();
            foreach (var tool in tools)
            {
                if (string.IsNullOrWhiteSpace(tool.Path)
                    || string.IsNullOrWhiteSpace(tool.CapabilitySid)) continue;
                RvmProto.AddMessage(body, 1, item =>
                {
                    RvmProto.AddString(item, 1, Path.TrimEndingDirectorySeparator(tool.Path));
                    RvmProto.AddString(item, 2, tool.CapabilitySid);
                    RvmProto.AddString(item, 3, tool.Name);
                });
            }

            var response = await RequestAsync(body, 80, cancellationToken).ConfigureAwait(false);
            if (response is null) return false;
            _status = $"Synced {tools.Count} tool authorizations";
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            FailConnection("Failed to sync tool authorizations", ex);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RvmWorkspaceSession?> CreateWorkspaceSessionAsync(
        string workspaceRoot,
        string driveLetter,
        string storageKey,
        RvmSettings? settings = null,
        IReadOnlyList<RvmToolAuthorization>? toolAccess = null,
        bool networkEnabled = false,
        IReadOnlyList<string>? devices = null,
        bool jailbreakTest = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping) return null;
            if (!await EnsureRunningCoreAsync(cancellationToken).ConfigureAwait(false)) return null;

            var body = new List<byte>();
            RvmProto.AddVarint(body, 1, 1); // ProtocolVersion::PROTOCOL_VERSION_1
            RvmProto.AddString(body, 2, workspaceRoot);
            RvmProto.AddMessage(body, 4, policy =>
            {
                RvmProto.AddVarint(policy, 1, 2); // SANDBOX_MODE_WORKSPACE_WRITE
                RvmProto.AddVarint(policy, 4, 1); // APPROVAL_MODE_ASK
                RvmProto.AddBool(policy, 3, networkEnabled); // network_access
                // 沙箱强度测试开关（policy.jailbreak_test）：引擎记进 facts 与审计，不放宽隔离
                if (jailbreakTest) RvmProto.AddBool(policy, 5, true);
            });
            // 受保护会话在独立的会话盘上运行：引擎按 git 索引把工作区选择性快照到 RVM 存储，
            // 用 subst 把会话盘挂到空闲盘符，只把会话盘授权给 AppContainer。
            RvmProto.AddBool(body, 9, true); // overlay_workspace：会话盘模式
            if (!string.IsNullOrWhiteSpace(driveLetter))
                RvmProto.AddString(body, 10, driveLetter.TrimEnd(':'));
            if (!string.IsNullOrWhiteSpace(storageKey))
                RvmProto.AddString(body, 11, storageKey);
            RvmProto.AddBool(body, 7, networkEnabled); // network_access
            if (devices is not null)
            {
                foreach (var device in devices)
                {
                    if (!string.IsNullOrWhiteSpace(device))
                        RvmProto.AddString(body, 15, device);
                }
            }

            // 环境变量只传值；工具目录改用全局授权同步后的 tool_access，不再按会话路径授权。
            if (settings is not null)
            {
                foreach (var name in settings.Environment)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var value = Environment.GetEnvironmentVariable(name);
                    if (value is null) continue;
                    RvmProto.AddMessage(body, 12, item =>
                    {
                        RvmProto.AddString(item, 1, name);
                        RvmProto.AddString(item, 2, value);
                    });
                }
            }
            if (toolAccess is not null)
            {
                foreach (var tool in toolAccess)
                {
                    if (string.IsNullOrWhiteSpace(tool.Path)
                        || string.IsNullOrWhiteSpace(tool.CapabilitySid)) continue;
                    RvmProto.AddMessage(body, 14, item =>
                    {
                        RvmProto.AddString(item, 1, Path.TrimEndingDirectorySeparator(tool.Path));
                        RvmProto.AddString(item, 2, tool.CapabilitySid);
                        RvmProto.AddString(item, 3, tool.Name);
                    });
                }
            }

            var response = await RequestAsync(body, 20, cancellationToken).ConfigureAwait(false);
            if (response is null) return null;
            // RpcResponse.create_session(20) → CreateSessionResponse.session(1)
            var session = response.Message(20)?.Message(1);
            if (session is null)
            {
                _status = "RVM engine returned an empty session record";
                return null;
            }

            var sessionId = session.Varint(1);
            var virtualRoot = session.String(7);
            if (sessionId == 0 || string.IsNullOrEmpty(virtualRoot))
            {
                _status = "RVM session is missing its id or workspace path";
                return null;
            }
            _status = $"RVM session ready: {virtualRoot}";
            return new RvmWorkspaceSession(sessionId, session.String(2), virtualRoot);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            FailConnection("Failed to create the RVM session", ex);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseSessionAsync(ulong sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == 0) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping || !_connected) return;
            var body = new List<byte>();
            RvmProto.AddVarint(body, 1, sessionId);
            RvmProto.AddBool(body, 2, true); // keep_diff：diff 按工作区保留，会话只卸载叠加盘
            RvmProto.AddString(body, 3, "workspace switch");
            await RequestAsync(body, 21, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _status = $"Failed to close the RVM session: {ex.Message}";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RvmWorkspaceDiff?> DiffWorkspaceAsync(
        ulong sessionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping) return null;
            if (!_connected)
            {
                _status = "RVM engine is not connected; cannot read workspace changes";
                return null;
            }
            var body = new List<byte>();
            RvmProto.AddVarint(body, 1, sessionId);
            var response = await RequestAsync(body, 60, cancellationToken).ConfigureAwait(false);
            if (response is null) return null;
            var diff = response.Message(60);
            if (diff is null)
            {
                _status = "RVM engine returned an empty change list";
                return null;
            }
            var changes = diff.Messages(1).Select(ParseChange).ToList();
            var conflicts = diff.Messages(2).Select(ParseConflict).ToList();
            _status = $"Workspace changes: {changes.Count}, conflicts: {conflicts.Count}";
            return new RvmWorkspaceDiff(changes, conflicts, diff.Varint(3) != 0);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            FailConnection("Failed to read workspace changes", ex);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RvmWorkspaceApplyResult?> SyncWorkspaceAsync(
        ulong sessionId,
        bool all,
        IReadOnlyList<string>? paths = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping) return null;
            if (!_connected)
            {
                _status = "RVM engine is not connected; cannot sync the workspace";
                return null;
            }
            var body = new List<byte>();
            RvmProto.AddVarint(body, 1, sessionId);
            if (paths is not null)
            {
                foreach (var path in paths)
                {
                    if (!string.IsNullOrWhiteSpace(path)) RvmProto.AddString(body, 2, path);
                }
            }
            RvmProto.AddBool(body, 3, all);
            var response = await RequestAsync(body, 61, cancellationToken).ConfigureAwait(false);
            if (response is null) return null;
            var applied = response.Message(61);
            if (applied is null)
            {
                _status = "RVM engine returned an empty sync result";
                return null;
            }
            var changes = applied.Messages(1).Select(ParseChange).ToList();
            var conflicts = applied.Messages(2).Select(ParseConflict).ToList();
            _status = $"Synced {changes.Count} changes, {conflicts.Count} conflicts";
            return new RvmWorkspaceApplyResult(changes, conflicts);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            FailConnection("Failed to sync the workspace", ex);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool?> RollbackWorkspaceAsync(
        ulong sessionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping) return null;
            if (!_connected)
            {
                _status = "RVM engine is not connected; cannot roll back the workspace";
                return null;
            }
            var body = new List<byte>();
            RvmProto.AddVarint(body, 1, sessionId);
            var response = await RequestAsync(body, 62, cancellationToken).ConfigureAwait(false);
            if (response is null) return null;
            var rolledBack = response.Message(62)?.Varint(1) != 0;
            _status = rolledBack ? "Session drive rolled back to the host workspace" : "RVM engine did not perform a rollback";
            return rolledBack;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            FailConnection("Failed to roll back the workspace", ex);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static RvmFileChange ParseChange(RvmProtoReader change)
    {
        var kind = change.Varint(2) switch
        {
            1 => RvmChangeKind.Added,
            2 => RvmChangeKind.Modified,
            3 => RvmChangeKind.Deleted,
            _ => RvmChangeKind.Unspecified,
        };
        return new RvmFileChange(change.String(1), kind, change.Varint(6), change.Varint(5));
    }

    private static RvmSyncConflict ParseConflict(RvmProtoReader conflict) =>
        new(conflict.String(1), conflict.String(2));

    public Task ShutdownAsync()
    {
        lock (_lifecycleLock)
            return _shutdownTask ??= ShutdownCoreAsync();
    }

    private async Task ShutdownCoreAsync()
    {
        // 停止态：新请求一律被拒绝，在途请求的取消令牌立刻作废
        _stopping = true;
        _abortCts.Cancel();

        // 关键状态判断：协议通道是否空闲。命名管道的挂起读取不响应取消令牌（实测：60s 的会话快照
        // 会让读取一直等到引擎回包），所以“等在途请求自己收尾”不是可靠的状态迁移。
        // 抢到锁 = 通道空闲，可以协商；抢不到 = 引擎正被一条请求占用，单连接下它听不到 shutdown。
        var process = _process;
        var channelBusy = !_gate.Wait(0);
        if (channelBusy)
        {
            // 通道被占用时没有第二条通道可协商：结束进程树，并丢弃通道来打断挂起的读取
            _status = "RVM engine is busy with an in-flight request; stopping the process tree";
            KillProcess(process);
            ResetConnection();
            // 通道已丢弃，在途操作会立刻失败并释放 _gate；这里等的是它退出的状态
            await _gate.WaitAsync().ConfigureAwait(false);
        }

        try
        {
            if (channelBusy)
            {
                if (await WaitForExitAsync(process, EngineTeardownBudget).ConfigureAwait(false))
                    _status = "RVM engine stopped (in-flight request abandoned)";
            }
            else if (process is null)
            {
                // 从未启动过：没有进程也没有通道，停止态直接成立
                _status = "RVM engine stopped";
            }
            else
            {
                // 通道空闲：请求优雅退出。引擎接受后自己收尾；不接受说明无法协作退出，这是确定状态
                var accepted = await TrySendShutdownAsync().ConfigureAwait(false);
                if (!accepted) KillProcess(process);
                if (await WaitForExitAsync(process, accepted ? EngineTeardownBudget : TimeSpan.Zero)
                        .ConfigureAwait(false))
                {
                    _status = accepted
                        ? "RVM engine stopped"
                        : "RVM engine stopped (shutdown request refused)";
                }
            }

            process?.Dispose();
            _process = null;
            ResetConnection();
            IsAvailable = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> TrySendShutdownAsync()
    {
        if (!_connected || _pipe is null) return false;
        var body = new List<byte>();
        RvmProto.AddString(body, 1, "host shutdown");
        try
        {
            // 读回引擎的 shutdown 接受状态；此请求在停止态下仍必须发出
            var response = await RequestAsync(body, 12, CancellationToken.None, ignoreAbort: true)
                .ConfigureAwait(false);
            return response is not null;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _status = $"Failed to request RVM engine shutdown: {ex.Message}";
            return false;
        }
    }

    private static void KillProcess(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    // 等到“进程退出”这个终态。budget 只覆盖宿主观测不到的卡死（引擎停在收尾里），
    // 用完就结束进程树；连强杀之后都不退，就交回进程生命周期，绝不把宿主挂在退出路径上。
    private async Task<bool> WaitForExitAsync(Process? process, TimeSpan budget)
    {
        if (process is null) return true;
        try
        {
            if (process.HasExited) return true;
            var exited = process.WaitForExitAsync();
            if (budget <= TimeSpan.Zero)
            {
                await exited.ConfigureAwait(false);
                return true;
            }
            if (ReferenceEquals(await Task.WhenAny(exited, Task.Delay(budget)).ConfigureAwait(false), exited))
                return true;
            KillProcess(process);
            if (ReferenceEquals(await Task.WhenAny(exited, Task.Delay(budget)).ConfigureAwait(false), exited))
                return true;
            _status = "RVM engine did not exit after being terminated";
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        // App.OnExit 在 UI 线程上同步调用：这里只等停止态的终局，不能把异常抛到退出路径上
        try
        {
            ShutdownAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _status = $"RVM engine shutdown failed: {ex.Message}";
        }
    }

    private async Task<bool> EnsureRunningCoreAsync(CancellationToken cancellationToken)
    {
        if (_stopping) return false;
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _abortCts.Token);
        cancellationToken = abort.Token;
        if (IsRunning) return IsAvailable;

        ResetConnection();
        if (_process is { HasExited: false })
        {
            // 上一次管道中断但引擎仍活着：先结束进程，再等它的退出事件，不按秒数猜
            KillProcess(_process);
            await WaitForExitAsync(_process, EngineTeardownBudget).ConfigureAwait(false);
        }
        _process?.Dispose();
        _process = null;
        var enginePath = ResolveEnginePath();
        if (enginePath is null)
        {
            _status = "RVM engine unavailable: RvmEngine.exe was not found";
            IsAvailable = false;
            return false;
        }

        var pipeName = "responsible-rvm-" + Guid.NewGuid().ToString("n");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _enginePath = enginePath;
        _pipeName = pipeName;
        _token = token;
        // 引擎被强杀/崩溃时 subst 映射会留在系统里（subst 不会随进程消失），
        // 启动时先把指向 RVM 存储的遗留盘符清掉。
        ReleaseStaleDriveMappings();
        var start = new ProcessStartInfo(enginePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add("--token-stdin");
        start.ArgumentList.Add("--log-level");
        start.ArgumentList.Add("info");

        try
        {
            _process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _status = $"Failed to start the RVM engine: {ex.Message}";
            IsAvailable = false;
            return false;
        }
        if (_process is null)
        {
            _status = "Failed to start the RVM engine";
            IsAvailable = false;
            return false;
        }

        _ = DrainAsync(_process.StandardOutput, "out");
        _ = DrainAsync(_process.StandardError, "err");
        try
        {
            await _process.StandardInput.WriteLineAsync(token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            _status = "RVM engine exited immediately after starting";
            IsAvailable = false;
            return false;
        }

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var deadline = Environment.TickCount64 + ConnectTimeoutMs;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await pipe.ConnectAsync(500, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (TimeoutException)
                {
                    if (_process.HasExited)
                        throw new InvalidOperationException("The engine process has exited.");
                    if (Environment.TickCount64 > deadline)
                        throw new TimeoutException("Timed out waiting for the engine named pipe.");
                }
            }
            _pipe = pipe;
            _connected = true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            pipe.Dispose();
            _status = $"Failed to connect to the RVM engine: {ex.Message}{DrainDetail()}";
            IsAvailable = false;
            ResetConnection();
            return false;
        }

        var hello = new List<byte>();
        RvmProto.AddVarint(hello, 1, 1); // requested protocol
        RvmProto.AddString(hello, 2, "responsible-wpf");
        RvmProto.AddVarint(hello, 3, (ulong)Environment.ProcessId);
        RvmProto.AddString(hello, 4, token);
        try
        {
            var response = await RequestAsync(hello, 10, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                ResetConnection();
                IsAvailable = false;
                return false;
            }
            var helloResponse = response.Message(10);
            if (helloResponse is null)
            {
                _status = "RVM handshake response is missing HelloResponse";
                ResetConnection();
                IsAvailable = false;
                return false;
            }
            var engineVersion = helloResponse.String(2);
            IsAvailable = helloResponse.Varint(7) != 0;
            var reason = helloResponse.String(8);
            _status = IsAvailable
                ? $"RVM engine {engineVersion} connected"
                : $"RVM unavailable: {(string.IsNullOrEmpty(reason) ? "availability probe failed" : reason)}";
            return IsAvailable;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _status = $"RVM handshake failed: {ex.Message}";
            ResetConnection();
            IsAvailable = false;
            return false;
        }
    }

    // 发送一个 RpcRequest（bodyField 为 oneof 中的消息字段号），返回 RpcResponse 解析结果；
    // 引擎错误码统一落到 Status，调用方拿 null 表示失败。
    private async Task<RvmProtoReader?> RequestAsync(
        List<byte> body,
        int bodyField,
        CancellationToken cancellationToken,
        bool ignoreAbort = false)
    {
        if (_pipe is null) throw new InvalidOperationException("The pipe is not connected.");
        var requestId = _nextRequestId++;
        var request = new List<byte>();
        RvmProto.AddVarint(request, 1, 1); // protocol
        RvmProto.AddVarint(request, 2, requestId);
        RvmProto.AddMessage(request, bodyField, nested => nested.AddRange(body));

        using var timeout = ignoreAbort
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _abortCts.Token);
        timeout.CancelAfter(RequestTimeoutMs);
        await WriteFrameAsync(request, requestId, timeout.Token).ConfigureAwait(false);
        var response = await ReadResponseAsync(requestId, timeout.Token).ConfigureAwait(false);
        if (response.Message(3) is { } error)
        {
            var code = error.Varint(1);
            var message = error.String(2);
            var detail = error.String(4);
            _status = $"RVM error {code}: {message}{(string.IsNullOrEmpty(detail) ? string.Empty : $" ({detail})")}";
            return null;
        }
        return response;
    }

    // 被取消的在途请求仍可能收到引擎的迟到回包：跳过不属于本次请求的帧，
    // 否则关闭流程里那条 shutdown 请求会拿错回包，把“引擎已接受”误判成协议错误。
    private async Task<RvmProtoReader> ReadResponseAsync(ulong requestId, CancellationToken cancellationToken)
    {
        for (var skipped = 0; skipped < MaxStaleResponses; skipped++)
        {
            var payload = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            var response = RvmProtoReader.Parse(payload);
            if (response.Varint(2) == requestId) return response;
        }
        throw new RvmProtocolException("Response request id does not match.");
    }

    private async Task WriteFrameAsync(List<byte> payload, ulong requestId, CancellationToken cancellationToken)
    {
        if (_pipe is null) throw new InvalidOperationException("The pipe is not connected.");
        if (!BitConverter.IsLittleEndian) throw new RvmProtocolException("Only little-endian hosts are supported.");
        if (payload.Count > MaxPayloadBytes) throw new RvmProtocolException("Request payload exceeds the limit.");
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BitConverter.TryWriteBytes(header.AsSpan(4, 2), FrameVersion);
        BitConverter.TryWriteBytes(header.AsSpan(6, 2), FrameRequest);
        BitConverter.TryWriteBytes(header.AsSpan(8, 4), (uint)payload.Count);
        BitConverter.TryWriteBytes(header.AsSpan(12, 4), 0u);
        BitConverter.TryWriteBytes(header.AsSpan(16, 8), requestId);
        await _pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await _pipe.WriteAsync(payload.ToArray(), cancellationToken).ConfigureAwait(false);
        await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken cancellationToken)
    {
        if (_pipe is null) throw new InvalidOperationException("The pipe is not connected.");
        var header = new byte[HeaderSize];
        await _pipe.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new RvmProtocolException("Invalid frame magic.");
        var version = BitConverter.ToUInt16(header, 4);
        var type = BitConverter.ToUInt16(header, 6);
        var size = BitConverter.ToUInt32(header, 8);
        if (version != FrameVersion) throw new RvmProtocolException($"Unsupported frame version {version}.");
        if (type != FrameResponse) throw new RvmProtocolException($"Unexpected frame type {type}.");
        if (size > MaxPayloadBytes) throw new RvmProtocolException("Response payload exceeds the protocol limit.");
        var payload = new byte[size];
        await _pipe.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    // 失败收尾：停止态下不要动协议通道——关闭流程正要用它请求引擎优雅退出
    private void FailConnection(string context, Exception ex)
    {
        _status = $"{context}: {ex.Message}";
        if (_stopping) return;
        ResetConnection();
    }

    private void ResetConnection()
    {
        _connected = false;
        IsAvailable = false;
        try
        {
            _pipe?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        _pipe = null;
    }

    private async Task DrainAsync(StreamReader reader, string channel)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_engineOutput)
                {
                    _engineOutput.Append('[').Append(channel).Append("] ").AppendLine(line);
                    if (_engineOutput.Length > 4096)
                        _engineOutput.Remove(0, _engineOutput.Length - 4096);
                }
                AppendEngineLog(channel, line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    // 引擎把每一步的耗时打在 stderr 上（seed / probe / isolation），落一份文件方便事后查。
    private static void AppendEngineLog(string channel, string line)
    {
        if (channel != "err") return;
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Responsible", "logs");
            var path = Path.Combine(directory, "rvm-engine.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Delete(path);
            Directory.CreateDirectory(directory);
            File.AppendAllText(path,
                $"{DateTimeOffset.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string DrainDetail()
    {
        lock (_engineOutput)
        {
            return _engineOutput.Length == 0 ? string.Empty : $"\n{_engineOutput}";
        }
    }

    private static string NormalizeDrive(string value)
    {
        var trimmed = (value ?? string.Empty).Trim().TrimEnd(':').ToUpperInvariant();
        return trimmed.Length == 1 && trimmed[0] is >= 'A' and <= 'Z' ? trimmed + ":" : "R:";
    }

    internal static string? ResolveEnginePath()
    {
        var configured = Environment.GetEnvironmentVariable("RESPONSIBLE_RVM_ENGINE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "RvmEngine.exe"),
            Path.Combine(baseDir, "RVM", "RvmEngine.exe"),
            Path.Combine(baseDir, "rvm", "RvmEngine.exe"),
        };
        var directory = new DirectoryInfo(baseDir);
        for (var depth = 0; depth < 6 && directory is not null; depth++, directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "RVM", "build", "bin", "RvmEngine.exe"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool IsExpected(Exception ex) =>
        ex is IOException or TimeoutException or InvalidOperationException or RvmProtocolException
            or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException;

    // subst 建立的盘符属于系统 DOS devices，进程退出不会自动消失：引擎被强杀、
    // 或宿主崩溃时，R: 会一直指向会话盘。这里在每次启动引擎前清掉指向 RVM 存储的映射，
    // 只动我们自己创建的盘符，不碰用户自己的 subst。
    private static readonly object StaleMappingLock = new();
    private static bool _staleMappingsCleared;

    private static void ReleaseStaleDriveMappings()
    {
        lock (StaleMappingLock)
        {
            if (_staleMappingsCleared) return;
            _staleMappingsCleared = true;
        }
        ClearStaleMappings();
    }

    private static void ClearStaleMappings()
    {
        string root;
        try
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Responsible", "RVM");
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        foreach (var drive in EnumerateSubstDrives())
        {
            var target = QueryDriveTarget(drive);
            if (target is null) continue;
            // QueryDosDevice 返回的是 NT 路径（\??\C:\...），比较前先剥掉前缀
            if (target.StartsWith(@"\??\", StringComparison.Ordinal)) target = target[4..];
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            RunSubst($"{drive} /D");
        }
    }

    private static IEnumerable<string> EnumerateSubstDrives()
    {
        for (var letter = 'A'; letter <= 'Z'; letter++)
            yield return $"{letter}:";
    }

    private static string? QueryDriveTarget(string drive)
    {
        var buffer = new StringBuilder(1024);
        var length = QueryDosDevice(drive, buffer, buffer.Capacity);
        if (length == 0) return null;
        // 返回值是写入的字符数；字符串里可能带 MULTI_SZ 的终止符，按第一个 NUL 截断
        var text = buffer.ToString();
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    private static void RunSubst(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("subst")
            {
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, int max);
}
