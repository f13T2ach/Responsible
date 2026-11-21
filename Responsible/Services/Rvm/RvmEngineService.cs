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
    private volatile string _status = "RVM 引擎未启动";
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

    public async Task<RvmWorkspaceSession?> CreateWorkspaceSessionAsync(
        string workspaceRoot,
        string driveLetter,
        string storageKey,
        RvmSettings? settings = null,
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
                RvmProto.AddBool(policy, 3, false); // network_access
            });
            // 直接工作区模式：WinFsp 挂载卷无法向 AppContainer 开放
            // （winfsp/winfsp#705），叠加层因此不可隔离；不传 overlay/drive/storage。
            _ = driveLetter;
            _ = storageKey;

            // 只传变量值，名称/工具目录已由用户在 RVM 设置里选定；引擎会再过滤保留名。
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
                foreach (var path in settings.Tools)
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    RvmProto.AddString(body, 13, Path.TrimEndingDirectorySeparator(path));
                }
            }

            var response = await RequestAsync(body, 20, cancellationToken).ConfigureAwait(false);
            if (response is null) return null;
            // RpcResponse.create_session(20) → CreateSessionResponse.session(1)
            var session = response.Message(20)?.Message(1);
            if (session is null)
            {
                _status = "RVM 引擎返回了空的会话记录";
                return null;
            }

            var sessionId = session.Varint(1);
            var virtualRoot = session.String(7);
            if (sessionId == 0 || string.IsNullOrEmpty(virtualRoot))
            {
                _status = "RVM 会话缺少 id 或虚拟工作区路径";
                return null;
            }
            _status = $"RVM 会话就绪：{virtualRoot}";
            return new RvmWorkspaceSession(sessionId, session.String(2), virtualRoot);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _status = $"RVM 会话创建失败：{ex.Message}";
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
            _status = $"RVM 会话关闭失败：{ex.Message}";
        }
        finally
        {
            _gate.Release();
        }
    }

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
            _status = "RVM 引擎已停止";
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
            _status = "RVM 引擎不可用：未找到 RvmEngine.exe";
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
            _status = $"RVM 引擎启动失败：{ex.Message}";
            IsAvailable = false;
            return false;
        }
        if (_process is null)
        {
            _status = "RVM 引擎启动失败";
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
            _status = "RVM 引擎启动后立即退出";
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
                        throw new InvalidOperationException("引擎进程已退出");
                    if (Environment.TickCount64 > deadline)
                        throw new TimeoutException("等待引擎命名管道超时");
                }
            }
            _pipe = pipe;
            _connected = true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            pipe.Dispose();
            _status = $"RVM 引擎连接失败：{ex.Message}{DrainDetail()}";
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
                _status = "RVM 握手响应缺少 HelloResponse";
                ResetConnection();
                IsAvailable = false;
                return false;
            }
            var engineVersion = helloResponse.String(2);
            IsAvailable = helloResponse.Varint(7) != 0;
            var reason = helloResponse.String(8);
            _status = IsAvailable
                ? $"RVM 引擎 {engineVersion} 已连接"
                : $"RVM 不可用：{(string.IsNullOrEmpty(reason) ? "可用性探测未通过" : reason)}";
            return IsAvailable;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _status = $"RVM 握手失败：{ex.Message}";
            ResetConnection();
            IsAvailable = false;
            return false;
        }
    }

    // 发送一个 RpcRequest（bodyField 为 oneof 中的消息字段号），返回 RpcResponse 解析结果；
    // 引擎错误码统一落到 Status，调用方拿 null 表示失败。
    private async Task<RvmProtoReader?> RequestAsync(List<byte> body, int bodyField, CancellationToken cancellationToken)
    {
        if (_pipe is null) throw new InvalidOperationException("管道未连接");
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
            throw new RvmProtocolException("响应 request id 不匹配");
        if (response.Message(3) is { } error)
        {
            var code = error.Varint(1);
            var message = error.String(2);
            var detail = error.String(4);
            _status = $"RVM 错误 {code}：{message}{(string.IsNullOrEmpty(detail) ? string.Empty : $"（{detail}）")}";
            return null;
        }
        return response;
    }

    private async Task WriteFrameAsync(List<byte> payload, ulong requestId, CancellationToken cancellationToken)
    {
        if (_pipe is null) throw new InvalidOperationException("管道未连接");
        if (!BitConverter.IsLittleEndian) throw new RvmProtocolException("仅支持小端主机");
        if (payload.Count > MaxPayloadBytes) throw new RvmProtocolException("请求负载超过上限");
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
        if (_pipe is null) throw new InvalidOperationException("管道未连接");
        var header = new byte[HeaderSize];
        await _pipe.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new RvmProtocolException("帧 magic 不正确");
        var version = BitConverter.ToUInt16(header, 4);
        var type = BitConverter.ToUInt16(header, 6);
        var size = BitConverter.ToUInt32(header, 8);
        if (version != FrameVersion) throw new RvmProtocolException($"不支持的帧版本 {version}");
        if (type != FrameResponse) throw new RvmProtocolException($"非预期帧类型 {type}");
        if (size > MaxPayloadBytes) throw new RvmProtocolException("响应负载超过协议上限");
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

    private static string? ResolveEnginePath()
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