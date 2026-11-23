using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Responsible.Infrastructure;

namespace Responsible.Services;

// DSH 内核宿主：node 子进程跑 `dsh --profile sdk`，stdio 上说 NDJSON JSON-RPC；
// 预载插件通过运行时生成的 cordis patch 挂载（RDshPlugins/responsible-host.mjs）
public sealed class DshKernelService : IDshKernelService, IDisposable
{
    // DSH 自己的持久化根：会话、凭据、插件设置都在这里，与用户的全局 ~/.dsh 隔离
    private static readonly string HomePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Responsible", "dsh-home");

    // 端口每个进程现取现用：固定在 1107 时，上一个内核没退干净（或另一个实例还在跑）
    // 会让新内核绑定失败，宿主随后打到旧实例上，只读接口直接 401 unauthorized。
    private static int ReserveBridgePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static readonly string PatchPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Responsible", "dsh-patch.yml");

    // 无审批 UI 前的默认策略：只读工具放行一次，其余拒绝（fail closed）
    private static readonly HashSet<string> ReadOnlyTools = ["read", "ls", "grep", "glob", "present"];

    // 这些条目由宿主自己管理，插件页的覆盖层不得改写
    private static readonly HashSet<string> ReservedOverrideIds = new(StringComparer.Ordinal)
    {
        "responsible-host", "tool-plugin-manager", "plugin-manager", "include",
        "sdk-app-startup", "sdk-jsonrpc-server", "sandbox", "rvm-sandbox",
    };

    private readonly PluginOverridesStore _overrides;
    private readonly object _writeGate = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private Process? _process;
    private int _nextId;
    private string _status = "Not started";
    private CancellationTokenSource? _streamCts;
    private DshRvmBinding? _rvmBinding;
    private string? _rvmUnavailable;
    // 主动 ShutdownAsync 时置位：Exited 回调据此区分“计划内重启”和“真的挂了”，
    // 否则每次换模型/换工作区重启都会把状态刷成 “Runtime exited”，像是崩了。
    private bool _expectedExit;

    public bool IsAvailable { get; private set; }

    public string Status
    {
        get => _status;
        private set => _status = value;
    }

    public event Action<string, JsonElement>? SessionEvent;
    public event Action<string, bool>? SessionStatus;
    public event Action<string, JsonElement>? AssistantStream;
    public event Action<string, DshSessionStats>? SessionStats;
    public event Action<string, DshTurnFrame>? TurnUsage;
    public event Action<string, string>? PermissionChanged;
    public event Action<string, DshToolCallFrame>? ToolEvent;
    public event Action<DshApproval, bool>? ApprovalRequested;

    public DshKernelService(PluginOverridesStore overrides)
    {
        _overrides = overrides;
        Bridge = new DshBridgeInfo(
            $@"\\.\pipe\responsible-dsh-{Environment.ProcessId}-{Guid.NewGuid():N}",
            Guid.NewGuid().ToString("N"),
            ReserveBridgePort());
    }

    public DshBridgeInfo Bridge { get; }

    public async Task<bool> StartAsync(string provider, string model, string cwd, string? apiKey,
        DshRvmBinding? rvm = null, string? rvmUnavailable = null)
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _rvmBinding = rvm;
            // 工作区要求了 RVM 但会话没建起来：内核仍然启动，但沙箱服务换成
            // “永远拒绝”的 RVM 提供者，理由直接给到模型，不再回退到 DSH 的 ACL 沙箱。
            _rvmUnavailable = rvm is null ? rvmUnavailable : null;
            return await StartCoreAsync(provider, model, cwd, apiKey).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<bool> StartCoreAsync(string provider, string model, string cwd, string? apiKey)
    {
        if (IsAvailable) return true;
        var bin = ResolveBin();
        if (bin is null)
        {
            Status = "RDshCore is not built. Run scripts/Build-DshCore.ps1 first.";
            return false;
        }

        var patchError = WritePatch();
        if (patchError is not null)
        {
            Status = patchError;
            return false;
        }
        var info = new ProcessStartInfo
        {
            FileName = ResolveNodeExecutable(),
            Arguments = $"\"{bin}\" --profile sdk --patch \"{PatchPath}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // node 侧为 UTF-8；重定向流默认随系统代码页（本机 GBK）会吞掉中文行的引号
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.Environment["DSH_HOME"] = HomePath;
        info.Environment["DSH_BRIDGE_PORT"] = Bridge.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // 插件在绑定内核时读取令牌并随 --token 传给壳桥接；沙箱子进程不会继承进程环境
        if (_rvmBinding is { } rvm)
            info.Environment["RESPONSIBLE_RVM_TOKEN"] = rvm.Token;
        if (!string.IsNullOrEmpty(apiKey))
            info.Environment[EnvKeyFor(provider)] = apiKey;

        try
        {
            _process = Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = $"Failed to start node: {ex.Message}";
            return false;
        }
        if (_process is null)
        {
            Status = "Failed to start node";
            return false;
        }

        _ = ReadLoopAsync(_process);
        _ = DrainStderrAsync(_process);
        // 新进程还没退出，把上一次计划内关停的标记清掉
        _expectedExit = false;
        _process.Exited += (_, _) =>
        {
            IsAvailable = false;
            if (_expectedExit)
            {
                // 计划内重启：保留已有状态文案，不要谎报成崩溃
                _streamCts?.Cancel();
                FailPending();
                return;
            }
            Status = "Runtime exited";
            _streamCts?.Cancel();
            FailPending();
        };
        _process.EnableRaisingEvents = true;
        StartStreamReader();

        try
        {
            var result = await SendAsync("initialize", new
            {
                cwd,
                provider,
                model,
                approvalRequests = true,
            }, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            var name = result.GetProperty("serverInfo").GetProperty("name").GetString();
            if (name != "deepseek-harness-sdk-runtime")
            {
                Status = $"Handshake identity mismatch: {name}";
                await ShutdownAsync().ConfigureAwait(false);
                return false;
            }
            IsAvailable = true;
            Status = $"runtime {result.GetProperty("serverInfo").GetProperty("version").GetString()}";
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or KeyNotFoundException or JsonException)
        {
            Status = $"Handshake failed: {ex.Message}";
            await ShutdownAsync().ConfigureAwait(false);
            return false;
        }
    }

    public async Task<bool> ResumeAsync(string sessionId)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(sessionId)) return false;
        try
        {
            await SendAsync("session/resume", new { sessionId }, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already open", StringComparison.OrdinalIgnoreCase))
        {
            // 会话仍由当前 runtime 持有：目标状态已达成
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or KeyNotFoundException or JsonException)
        {
            Status = $"Resume failed: {ex.Message}";
            return false;
        }
    }

    public async Task<IReadOnlyList<DshSessionRow>?> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return null;
        try
        {
            return await DshBridgeHttpClient.GetSessionsAsync(Bridge, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or JsonException)
        {
            Status = $"Conversation list unavailable: {ex.Message}";
            return null;
        }
    }

    public async Task<IReadOnlyList<DshHistoryMessage>> ReadHistoryAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(sessionId)) return [];
        try
        {
            return await DshBridgeHttpClient.GetHistoryAsync(Bridge, sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or JsonException)
        {
            Status = $"Failed to read history: {ex.Message}";
            return [];
        }
    }

    public async Task<DshSessionStats?> ReadStatsAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(sessionId)) return null;
        try
        {
            return await DshBridgeHttpClient.GetStatsAsync(Bridge, sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or JsonException)
        {
            Status = $"Failed to read stats: {ex.Message}";
            return null;
        }
    }

    public async Task<IReadOnlyList<DshModelRow>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return [];
        try
        {
            return await DshBridgeHttpClient.GetModelsAsync(Bridge, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or JsonException)
        {
            Status = $"Model list unavailable: {ex.Message}";
            return [];
        }
    }

    public async Task<DshPermissionCatalog?> GetPermissionCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return null;
        try
        {
            return await DshBridgeHttpClient.GetPermissionCatalogAsync(Bridge, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or JsonException)
        {
            Status = $"Permission catalog unavailable: {ex.Message}";
            return null;
        }
    }

    public async Task<DshPermissionSelection?> GetPermissionAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(sessionId)) return null;
        try
        {
            var value = await DshBridgeHttpClient
                .GetPermissionAsync(Bridge, sessionId, cancellationToken).ConfigureAwait(false);
            return new DshPermissionSelection(sessionId, value);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or JsonException)
        {
            Status = $"Failed to read permissions: {ex.Message}";
            return null;
        }
    }

    public async Task<string?> SetPermissionPresetAsync(
        string sessionId, string preset, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(preset))
            return null;
        try
        {
            return await DshBridgeHttpClient
                .SetPermissionAsync(Bridge, sessionId, preset, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or JsonException)
        {
            Status = $"Failed to switch permission: {ex.Message}";
            return null;
        }
    }

    public async Task<string?> ForkSessionAsync(string sessionId, long atSeq)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(sessionId)) return null;
        try
        {
            var childId = await DshBridgeHttpClient
                .ForkSessionAsync(Bridge, sessionId, atSeq, CancellationToken.None)
                .ConfigureAwait(false);
            if (childId is null) Status = "Branch failed: the kernel did not return a child session";
            return childId;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or JsonException)
        {
            Status = $"Branch failed: {ex.Message}";
            return null;
        }
    }

    public async Task<bool> SetCredentialAsync(
        string reference, string value, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(reference) || string.IsNullOrEmpty(value)) return false;
        try
        {
            return await DshBridgeHttpClient.SetCredentialAsync(Bridge, reference, value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or JsonException)
        {
            Status = $"Failed to write credentials: {ex.Message}";
            return false;
        }
    }

    public async Task<string?> PromptAsync(string sessionId, string text)
    {
        if (!IsAvailable) return null;
        try
        {
            var result = await SendAsync("session/prompt", new
            {
                sessionId,
                contentBlocks = new[] { new { type = "text", text } },
            }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            return result.GetProperty("messageId").GetString();
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or KeyNotFoundException or JsonException)
        {
            Status = $"Prompt failed: {ex.Message}";
            return null;
        }
    }

    public async Task ShutdownAsync()
    {
        _streamCts?.Cancel();
        var process = _process;
        IsAvailable = false;
        // 关停是计划内的：Exited 回调不该把它报成 "Runtime exited"
        _expectedExit = true;
        if (process is null || process.HasExited) return;
        try
        {
            await SendAsync("shutdown", new { }, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
        }
        try
        {
            if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        _process = null;
        FailPending();
    }

    // SDK 没有 session/delete：先关 runtime 释放文件句柄，再删 DSH 自己的会话目录与投影缓存
    public async Task<bool> DeleteSessionAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !sessionId.All(char.IsAsciiLetterOrDigit))
            return false;

        await ShutdownAsync().ConfigureAwait(false);
        try
        {
            var removed = false;
            var sessionsRoot = Path.Combine(HomePath, "sessions");
            if (Directory.Exists(sessionsRoot))
            {
                foreach (var cwdDir in Directory.EnumerateDirectories(sessionsRoot))
                {
                    var sessionDir = Path.Combine(cwdDir, sessionId);
                    if (!Directory.Exists(sessionDir)) continue;
                    Directory.Delete(sessionDir, recursive: true);
                    removed = true;
                }
            }

            var cacheDir = Path.Combine(HomePath, "storages", "session_projcache", "sessions");
            if (Directory.Exists(cacheDir))
            {
                foreach (var cacheFile in Directory.EnumerateFiles(cacheDir, $"{sessionId}.*"))
                    File.Delete(cacheFile);
            }
            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Failed to delete the conversation: {ex.Message}";
            return false;
        }
    }

    // 内核异步全程不捕获 UI 上下文：OnExit 在 UI 线程同步等待 Dispose，捕获上下文会死锁
    public void Dispose() => ShutdownAsync().GetAwaiter().GetResult();

    private async Task<JsonElement> SendAsync(string method, object parameters, TimeSpan timeout)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        Write(DshProtocol.Request(id, method, parameters));
        using var cts = new CancellationTokenSource(timeout);
        using var reg = cts.Token.Register(() =>
        {
            _pending.TryRemove(id, out _);
            tcs.TrySetException(new TimeoutException($"{method} timed out"));
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    private void Write(string line)
    {
        var stdin = _process?.StandardInput;
        if (stdin is null) throw new InvalidOperationException("Runtime is not running");
        lock (_writeGate)
        {
            stdin.WriteLine(line);
            stdin.Flush();
        }
    }

    private void StartStreamReader()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        var cts = new CancellationTokenSource();
        _streamCts = cts;
        _ = StreamEventsLoopAsync(cts.Token);
    }

    // 插件事件管道在内核进程里监听，宿主只读；内核还没起来时短暂退避重连
    private async Task StreamEventsLoopAsync(CancellationToken cancellationToken)
    {
        var pipeName = Bridge.PipeName.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)
            ? Bridge.PipeName[9..]
            : Bridge.PipeName;
        pipeName += "-events";

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In);
                using var registration = cancellationToken.Register(
                    static state => ((NamedPipeClientStream)state!).Dispose(), pipe);
                await pipe.ConnectAsync(500, cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null) break;
                    DispatchStreamFrame(line);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException
                or InvalidOperationException or UnauthorizedAccessException)
            {
                // 插件未监听或内核退出：交给下一次循环重连
            }

            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void DispatchStreamFrame(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!Prop(root, "sessionId", out var sessionId) || sessionId.ValueKind != JsonValueKind.String)
                return;
            var id = sessionId.GetString()!;
            if (Prop(root, "type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() == "stats")
            {
                if (Prop(root, "stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
                    SessionStats?.Invoke(id, ParseSessionStats(stats));
                return;
            }
            if (Prop(root, "type", out var permissionType) && permissionType.ValueKind == JsonValueKind.String
                && permissionType.GetString() == "permissions")
            {
                if (Prop(root, "currentValue", out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } current)
                    PermissionChanged?.Invoke(id, current);
                return;
            }
            if (Prop(root, "type", out var turnType) && turnType.ValueKind == JsonValueKind.String
                && turnType.GetString() == "turn")
            {
                if (Prop(root, "usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                    && ParseTurnUsage(usage) is { } parsed)
                {
                    var turn = Prop(root, "turn", out var turnProp) && turnProp.TryGetInt32(out var turnNumber)
                        ? turnNumber
                        : 0;
                    var seq = Prop(root, "seq", out var seqProp) && seqProp.TryGetInt64(out var seqNumber)
                        ? seqNumber
                        : 0;
                    TurnUsage?.Invoke(id, new DshTurnFrame(id, turn, seq, parsed));
                }
                return;
            }
            if (Prop(root, "type", out var toolType) && toolType.ValueKind == JsonValueKind.String
                && toolType.GetString() == "tool")
            {
                if (ParseToolFrame(root) is { } tool) ToolEvent?.Invoke(id, tool);
                return;
            }
            AssistantStream?.Invoke(id, root.Clone());
        }
    }

    private static DshToolCallFrame? ParseToolFrame(JsonElement root)
    {
        static string Text(JsonElement element, string name) =>
            Prop(element, name, out var prop) && prop.ValueKind == JsonValueKind.String
                ? prop.GetString() ?? string.Empty
                : string.Empty;

        var phase = Text(root, "phase");
        var callId = Text(root, "callId");
        if (phase.Length == 0 || callId.Length == 0) return null;
        var ts = Prop(root, "ts", out var tsProp) && tsProp.TryGetInt64(out var tsNumber) ? tsNumber : 0;
        var output = Text(root, "output");
        var error = Text(root, "error");
        var isError = Prop(root, "isError", out var errorProp) && errorProp.ValueKind == JsonValueKind.True;
        return new DshToolCallFrame(
            phase,
            callId,
            Text(root, "name"),
            Text(root, "variant"),
            Text(root, "summary"),
            Text(root, "input"),
            ts,
            output.Length == 0 ? null : output,
            isError,
            error.Length == 0 ? null : error);
    }

    private static DshTurnUsage? ParseTurnUsage(JsonElement usage)
    {
        if (!usage.TryGetProperty("uncachedInputTokens", out var input) || !input.TryGetInt64(out var uncachedInput))
            return null;
        if (!usage.TryGetProperty("outputTokens", out var output) || !output.TryGetInt64(out var outputTokens))
            return null;
        if (!usage.TryGetProperty("totalTokens", out var total) || !total.TryGetInt64(out var totalTokens))
            return null;
        static long? OptionalLong(JsonElement value, string name) =>
            value.TryGetProperty(name, out var prop) && prop.TryGetInt64(out var number) ? number : null;

        var routes = new List<DshUsageRoute>();
        if (usage.TryGetProperty("routes", out var routeArray) && routeArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var route in routeArray.EnumerateArray())
            {
                if (route.ValueKind != JsonValueKind.Object) continue;
                var provider = route.TryGetProperty("provider", out var p) ? p.GetString() : null;
                var model = route.TryGetProperty("model", out var m) ? m.GetString() : null;
                if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(model))
                    routes.Add(new DshUsageRoute(provider!, model!));
            }
        }

        return new DshTurnUsage(
            uncachedInput,
            outputTokens,
            totalTokens,
            OptionalLong(usage, "cacheReadTokens"),
            OptionalLong(usage, "cacheWriteTokens"),
            OptionalLong(usage, "reasoningTokens"),
            routes);
    }

    private static DshSessionStats ParseSessionStats(JsonElement stats)
    {
        static long Long(JsonElement value, string name) =>
            value.TryGetProperty(name, out var prop) && prop.TryGetInt64(out var number) ? number : 0;
        static double Double(JsonElement value, string name) =>
            value.TryGetProperty(name, out var prop) && prop.TryGetDouble(out var number) ? number : 0;
        static long? OptionalLong(JsonElement value, string name) =>
            value.TryGetProperty(name, out var prop) && prop.TryGetInt64(out var number) ? number : null;

        return new DshSessionStats(
            (int)Long(stats, "turns"),
            (int)Long(stats, "steps"),
            Long(stats, "llmMs"),
            Long(stats, "toolMs"),
            Long(stats, "ttftMs"),
            Long(stats, "decodeMs"),
            Long(stats, "decodeTokens"),
            Double(stats, "tokensPerSecond"),
            Long(stats, "inputTokens"),
            Long(stats, "outputTokens"),
            Long(stats, "cacheReadTokens"),
            Long(stats, "cacheWriteTokens"),
            Long(stats, "totalTokens"),
            Double(stats, "cacheHitRate"),
            OptionalLong(stats, "pressureTokens"),
            OptionalLong(stats, "contextWindow"));
    }

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                using var frame = DshProtocol.Parse(line);
                if (frame is null) continue;
                var root = frame.RootElement;
                var hasId = root.TryGetProperty("id", out var idElement);
                var hasMethod = root.TryGetProperty("method", out var methodElement);
                if (hasId && !hasMethod)
                {
                    if (_pending.TryRemove(idElement.GetInt32(), out var tcs))
                    {
                        if (root.TryGetProperty("error", out var error))
                        {
                            var message = error.ValueKind == JsonValueKind.Object
                                && error.TryGetProperty("message", out var errorMessage)
                                ? errorMessage.GetString()
                                : error.ToString();
                            tcs.TrySetException(new InvalidOperationException(message ?? "Kernel request failed"));
                        }
                        else if (Prop(root, "result", out var result))
                            tcs.TrySetResult(result.Clone());
                        else
                            tcs.TrySetException(new InvalidOperationException("Response is missing result"));
                    }
                }
                else if (hasId && hasMethod)
                {
                    AnswerRequest(idElement.GetInt32(), methodElement.GetString()!, root);
                }
                else if (hasMethod)
                {
                    DispatchNotification(methodElement.GetString()!, root);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
        IsAvailable = false;
        FailPending();
    }

    private void AnswerRequest(int id, string method, JsonElement root)
    {
        var parameters = ParamsOf(root);
        switch (method)
        {
            case "approval/request":
                var tool = Prop(parameters, "toolName", out var t) ? t.GetString() ?? "" : "";
                var session = Prop(parameters, "sessionId", out var s) ? s.GetString() ?? "" : "";
                var reason = Prop(parameters, "reason", out var r) ? r.GetString() : null;
                var allow = ReadOnlyTools.Contains(tool);
                ApprovalRequested?.Invoke(new DshApproval(session, tool, reason), allow);
                Write(DshProtocol.Response(id, new { outcome = allow ? "allowed-once" : "rejected" }));
                break;
            case "user-question/ask":
                // 宿主暂无提问 UI：明确取消，不冒充回答
                Write(DshProtocol.Response(id, new { cancelled = true }));
                break;
            default:
                Write(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32601, message = $"unsupported host request: {method}" },
                }, DshProtocol.Json));
                break;
        }
    }

    private void DispatchNotification(string method, JsonElement root)
    {
        var parameters = ParamsOf(root);
        switch (method)
        {
            case "session.event":
                if (Prop(parameters, "sessionId", out var sid) && Prop(parameters, "event", out var ev))
                    SessionEvent?.Invoke(sid.GetString()!, ev.Clone());
                break;
            case "session.status":
                if (Prop(parameters, "sessionId", out var sid2) && Prop(parameters, "status", out var st))
                    SessionStatus?.Invoke(sid2.GetString()!, st.GetString() == "running");
                break;
        }
    }

    private static JsonElement ParamsOf(JsonElement root) =>
        Prop(root, "params", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;

    private static bool Prop(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private async Task DrainStderrAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                Debug.WriteLine($"[dsh] {line}");
                // 插件的 stderr 是审批/沙箱链路上唯一的可观测线索，落一份文件
                if (line.Contains("[approval]", StringComparison.Ordinal) ||
                    line.Contains("[questions]", StringComparison.Ordinal) ||
                    line.Contains("[rvm-sandbox]", StringComparison.Ordinal))
                    AppendKernelLog(line);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
    }

    private static void AppendKernelLog(string line)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Responsible", "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "kernel.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTimeOffset.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void FailPending()
    {
        foreach (var (_, tcs) in _pending)
            tcs.TrySetException(new InvalidOperationException("Runtime connection was interrupted"));
        _pending.Clear();
    }

    private static string EnvKeyFor(string provider) => provider switch
    {
        "deepseek-official" => "DEEPSEEK_API_KEY",
        "openai" => "OPENAI_API_KEY",
        "anthropic" => "ANTHROPIC_API_KEY",
        _ => "DSH_API_KEY",
    };

    private static string? ResolveBin()
    {
        var overrideBin = Environment.GetEnvironmentVariable("RESPONSIBLE_DSH_BIN");
        if (!string.IsNullOrEmpty(overrideBin))
            return File.Exists(overrideBin) ? overrideBin : null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "RDshCore", "apps", "cli", "lib", "bin.js");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // 返回非 null 表示 patch 无法生成：RVM 会话已经建立、却装不上接管插件时宁可失败，
    // 也不能让内核悄悄退回 DSH 自带的 ACL 沙箱（那会在工作区授权上直接炸掉）。
    //
    // 注意“会话没建立”（_rvmUnavailable）是另一回事：那不是配置错误，而是这个工作区
    // 暂时建不出会话盘（例如没有 Git 索引）。此时保留 DSH 自带的 sandbox，让工具照常可用，
    // 只把原因挂在 badge 上——否则整条命令通道会变成“什么都不能跑”。
    // 发行包里带便携 node：优先用同目录下的 node\node.exe，开发期回落到 PATH
    private static string ResolveNodeExecutable()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "node", "node.exe");
        return File.Exists(local) ? local : "node";
    }
    private string? WritePatch()
    {
        var plugin = ResolvePlugin();
        var statsPlugin = ResolveStatsPlugin();
        var rvmActive = _rvmBinding is not null;
        var rvmPlugin = rvmActive ? ResolveRvmPlugin() : null;
        var sandboxModule = rvmActive ? ResolveSandboxModule() : null;
        if (rvmActive && (rvmPlugin is null || sandboxModule is null))
        {
            return "RVM takeover is unavailable: "
                   + (rvmPlugin is null ? "RDshPlugins/rvm-sandbox.mjs" : "the DSH sandbox module")
                   + " was not found; the kernel was not started with a different sandbox.";
        }
        var insert = new List<string>();
        if (statsPlugin is not null)
        {
            insert.Add("    - id: session-stats");
            insert.Add($"      name: {YamlString(statsPlugin)}");
        }
        if (plugin is not null)
        {
            insert.Add("    - id: responsible-host");
            insert.Add($"      name: {YamlString(plugin)}");
            insert.Add("      config:");
            insert.Add($"        bridgePipe: {YamlString(Bridge.PipeName)}");
            insert.Add($"        bridgeToken: {YamlString(Bridge.Token)}");
            insert.Add($"        bridgePort: {Bridge.Port}");
        }
        if (rvmActive && rvmPlugin is not null && sandboxModule is not null)
        {
            insert.Add("    - id: rvm-sandbox");
            insert.Add($"      name: {YamlString(rvmPlugin)}");
            insert.Add("      config:");
            insert.Add($"        sandboxModule: {YamlString(sandboxModule)}");
            var rvm = _rvmBinding!;
            insert.Add($"        enginePath: {YamlString(rvm.EnginePath)}");
            insert.Add($"        pipeName: {YamlString(rvm.PipeName)}");
            insert.Add($"        sessionId: {YamlString(rvm.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
            insert.Add($"        workspaceRoot: {YamlString(rvm.WorkspaceRoot)}");
            insert.Add($"        hostWorkspaceRoot: {YamlString(rvm.HostWorkspaceRoot)}");
            // 沙箱强度测试开关：RVM 策略里打开时才换成 jailbreak 提示词（策略不放宽）
            if (rvm.JailbreakTest) insert.Add("        sandboxProbe: true");
        }

        var lines = new List<string>();
        if (insert.Count > 0)
        {
            lines.Add("- insert:");
            lines.AddRange(insert);
        }
        // 只有真正接管时才能替换 DSH 自带的 sandbox；未就绪时保留它，工具才有后端可用。
        if (rvmActive && rvmPlugin is not null && sandboxModule is not null)
        {
            lines.Add("- id: sandbox");
            lines.Add("  disabled: true");
        }
        lines.Add("- id: tool-plugin-manager");
        lines.Add("  disabled: true");

        foreach (var (entryId, enabled) in _overrides.Snapshot())
        {
            if (ReservedOverrideIds.Contains(entryId))
                continue;
            lines.Add($"- id: {YamlString(entryId)}");
            lines.Add($"  disabled: {(enabled ? "false" : "true")}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(PatchPath)!);
        File.WriteAllLines(PatchPath, lines);
        return null;
    }

    // JSON 字符串是合法的 YAML 双引号标量，能安全承载路径与任意条目 id
    private static string YamlString(string value) => JsonSerializer.Serialize(value);

    // 统计投影由 DSH 自己的插件提供；sdk 剖面只挂了注册表，需按绝对路径补挂 session-stats
    private static string? ResolveStatsPlugin()
    {
        var bin = ResolveBin();
        if (bin is null) return null;
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(bin)!, "..", "..", ".."));
        return ResolveKernelPackageFile(root, "dsh-session-stats", Path.Combine("session", "session-stats"));
    }

    // 内核包在开发树里是 packages/<group>/<pkg>/lib/index.js，在发行包里只有
    // node_modules/@deepseek-ai/<pkg>/lib/index.js（发行树不带 packages/ 源码）。
    // 两个位置都要找，否则打包版一旦启用 RVM 就会因为“找不到沙箱模块”启动失败。
    private static string? ResolveKernelPackageFile(string root, string packageName, string repoTail)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(root, "node_modules", "@deepseek-ai", packageName, "lib", "index.js"),
                     Path.Combine(root, "packages", repoTail, "lib", "index.js"),
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? ResolvePlugin()
    {
        var overridePlugin = Environment.GetEnvironmentVariable("RESPONSIBLE_DSH_PLUGIN");
        if (!string.IsNullOrEmpty(overridePlugin))
            return File.Exists(overridePlugin) ? overridePlugin : null;
        return ResolveRepoFile("RDshPlugins", "responsible-host.mjs");
    }

    // RVM 沙箱提供者插件：与 responsible-host 同一份 RDshPlugins 目录
    private static string? ResolveRvmPlugin()
    {
        var overridePlugin = Environment.GetEnvironmentVariable("RESPONSIBLE_RVM_PLUGIN");
        if (!string.IsNullOrEmpty(overridePlugin))
            return File.Exists(overridePlugin) ? overridePlugin : null;
        return ResolveRepoFile("RDshPlugins", "rvm-sandbox.mjs");
    }

    // 被替换的 sandbox 服务定义（SandboxProvider/SandboxUnavailableError 的导出方）
    private static string? ResolveSandboxModule()
    {
        var overrideModule = Environment.GetEnvironmentVariable("RESPONSIBLE_SANDBOX_MODULE");
        if (!string.IsNullOrEmpty(overrideModule))
            return File.Exists(overrideModule) ? overrideModule : null;
        var bin = ResolveBin();
        if (bin is null) return null;
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(bin)!, "..", "..", ".."));
        return ResolveKernelPackageFile(root, "dsh-sandbox", Path.Combine("sandbox", "sandbox"));
    }

    private static string? ResolveRepoFile(string folder, string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, folder, file);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}