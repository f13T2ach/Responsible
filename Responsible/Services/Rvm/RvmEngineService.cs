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
    private const int ConnectTimeoutMs = 30000;
    private const int RequestTimeoutMs = 120000;

    private static readonly byte[] Magic = "RVM1"u8.ToArray();

    private readonly SemaphoreSlim _gate = new(1, 1);
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

    public bool IsAvailable { get; private set; }

    public string Status => _status;

    // 壳转发所需的真实连接参数：仅在引擎已启动并完成握手后可供 patch 使用
    public string? EnginePath => _enginePath;
    public string? PipeName => _pipeName;
    public string? Token => _token;

    public async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsureRunningCoreAsync(cancellationToken).ConfigureAwait(false);
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
            _status = $"Failed to sync tool authorizations: {ex.Message}";
            ResetConnection();
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
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await EnsureRunningCoreAsync(cancellationToken).ConfigureAwait(false)) return null;

            var body = new List<byte>();
            RvmProto.AddVarint(body, 1, 1); // ProtocolVersion::PROTOCOL_VERSION_1
            RvmProto.AddString(body, 2, workspaceRoot);
            RvmProto.AddMessage(body, 4, policy =>
            {
                RvmProto.AddVarint(policy, 1, 2); // SANDBOX_MODE_WORKSPACE_WRITE
                RvmProto.AddVarint(policy, 4, 1); // APPROVAL_MODE_ASK
                RvmProto.AddBool(policy, 3, networkEnabled); // network_access
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
            _status = $"Failed to create the RVM session: {ex.Message}";
            ResetConnection();
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
            if (!_connected) return;
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
            _status = $"Failed to read workspace changes: {ex.Message}";
            ResetConnection();
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
            _status = $"Failed to sync the workspace: {ex.Message}";
            ResetConnection();
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
            _status = $"Failed to roll back the workspace: {ex.Message}";
            ResetConnection();
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

    public async Task ShutdownAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connected)
            {
                try
                {
                    var body = new List<byte>();
                    RvmProto.AddString(body, 1, "host shutdown");
                    await RequestAsync(body, 12, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                }
            }
            ResetConnection();
            if (_process is { HasExited: false })
            {
                try
                {
                    _process.WaitForExit(5000);
                }
                catch (InvalidOperationException)
                {
                }
            }
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
            _process?.Dispose();
            _process = null;
            IsAvailable = false;
            _status = "RVM engine stopped";
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => ShutdownAsync().GetAwaiter().GetResult();

    private async Task<bool> EnsureRunningCoreAsync(CancellationToken cancellationToken)
    {
        if (IsRunning) return IsAvailable;

        ResetConnection();
        if (_process is { HasExited: false })
        {
            // 上一次管道中断但引擎仍活着：先收尸，避免重复启动进程
            try
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
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
    private async Task<RvmProtoReader?> RequestAsync(List<byte> body, int bodyField, CancellationToken cancellationToken)
    {
        if (_pipe is null) throw new InvalidOperationException("The pipe is not connected.");
        var requestId = _nextRequestId++;
        var request = new List<byte>();
        RvmProto.AddVarint(request, 1, 1); // protocol
        RvmProto.AddVarint(request, 2, requestId);
        RvmProto.AddMessage(request, bodyField, nested => nested.AddRange(body));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeoutMs);
        await WriteFrameAsync(request, requestId, timeout.Token).ConfigureAwait(false);
        var payload = await ReadFrameAsync(timeout.Token).ConfigureAwait(false);
        var response = RvmProtoReader.Parse(payload);
        if (response.Varint(2) != requestId)
            throw new RvmProtocolException("Response request id does not match.");
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
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
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
}