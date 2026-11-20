using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Responsible.Infrastructure;

namespace Responsible.Services;

public sealed record DshApproval(string SessionId, string ToolName, string? Reason);

public interface IDshKernelService
{
    bool IsAvailable { get; }
    string Status { get; }

    event Action<string, JsonElement>? SessionEvent;
    event Action<string, bool>? SessionStatus;

    Task<bool> StartAsync(string provider, string model, string cwd, string? apiKey);
    Task<string?> PromptAsync(string sessionId, string text);
    Task ShutdownAsync();
}

// DSH 内核宿主：node 子进程跑 `dsh --profile sdk`，stdio 上说 NDJSON JSON-RPC；
// 预载插件通过运行时生成的 cordis patch 挂载（RDshPlugins/responsible-host.mjs）
public sealed class DshKernelService : IDshKernelService, IDisposable
{
    private static readonly string HomePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Responsible", "DshHome");

    private static readonly string PatchPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Responsible", "dsh-patch.yml");

    // 无审批 UI 前的默认策略：只读工具放行一次，其余拒绝（fail closed）
    private static readonly HashSet<string> ReadOnlyTools = ["read", "ls", "grep", "glob", "present"];

    private readonly object _writeGate = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private Process? _process;
    private int _nextId;
    private string _status = "未启动";

    public bool IsAvailable { get; private set; }

    public string Status
    {
        get => _status;
        private set => _status = value;
    }

    public event Action<string, JsonElement>? SessionEvent;
    public event Action<string, bool>? SessionStatus;
    public event Action<DshApproval, bool>? ApprovalRequested;

    public async Task<bool> StartAsync(string provider, string model, string cwd, string? apiKey)
    {
        if (IsAvailable) return true;
        var bin = ResolveBin();
        if (bin is null)
        {
            Status = "RDshCore 未构建：先运行 scripts/Build-DshCore.ps1";
            return false;
        }

        WritePatch();
        var info = new ProcessStartInfo
        {
            FileName = "node",
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
        if (!string.IsNullOrEmpty(apiKey))
            info.Environment[EnvKeyFor(provider)] = apiKey;

        try
        {
            _process = Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = $"node 启动失败：{ex.Message}";
            return false;
        }
        if (_process is null)
        {
            Status = "node 启动失败";
            return false;
        }

        _ = ReadLoopAsync(_process);
        _ = DrainStderrAsync(_process);
        _process.Exited += (_, _) =>
        {
            IsAvailable = false;
            Status = "runtime 已退出";
            FailPending();
        };
        _process.EnableRaisingEvents = true;

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
                Status = $"握手身份不符：{name}";
                await ShutdownAsync().ConfigureAwait(false);
                return false;
            }
            IsAvailable = true;
            Status = $"runtime {result.GetProperty("serverInfo").GetProperty("version").GetString()}";
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or KeyNotFoundException or JsonException)
        {
            Status = $"握手失败：{ex.Message}";
            await ShutdownAsync().ConfigureAwait(false);
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
            Status = $"prompt 失败：{ex.Message}";
            return null;
        }
    }

    public async Task ShutdownAsync()
    {
        var process = _process;
        IsAvailable = false;
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
            tcs.TrySetException(new TimeoutException($"{method} 超时"));
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    private void Write(string line)
    {
        var stdin = _process?.StandardInput;
        if (stdin is null) throw new InvalidOperationException("runtime 未运行");
        lock (_writeGate)
        {
            stdin.WriteLine(line);
            stdin.Flush();
        }
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
                            tcs.TrySetException(new InvalidOperationException(error.ToString()));
                        else if (Prop(root, "result", out var result))
                            tcs.TrySetResult(result.Clone());
                        else
                            tcs.TrySetException(new InvalidOperationException("响应缺少 result"));
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
                Debug.WriteLine($"[dsh] {line}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
    }

    private void FailPending()
    {
        foreach (var (_, tcs) in _pending)
            tcs.TrySetException(new InvalidOperationException("runtime 连接中断"));
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

    private static void WritePatch()
    {
        var plugin = ResolvePlugin();
        var lines = new List<string>();
        if (plugin is not null)
        {
            lines.Add("- insert:");
            lines.Add("    - id: responsible-host");
            lines.Add($"      name: '{plugin.Replace("'", "''")}'");
        }
        lines.Add("- id: tool-plugin-manager");
        lines.Add("  disabled: true");
        Directory.CreateDirectory(Path.GetDirectoryName(PatchPath)!);
        File.WriteAllLines(PatchPath, lines);
    }

    private static string? ResolvePlugin()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "RDshPlugins", "responsible-host.mjs");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}