using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Messaging;
using Responsible.Messages;

namespace Responsible.Services;

public sealed record ChatMessage(string Id, string Role, string Text, string State, long Ts, string? Model = null);

public sealed record ChatStats(
    int Rounds,
    int Steps,
    int TokensPerSecond,
    long TotalTokens,
    double CacheHitRate,
    double ContextPercent);

public interface IChatSessionService
{
    IReadOnlyList<ChatMessage> Messages { get; }
    ChatStats Stats { get; }

    event Action<ChatMessage>? Appended;
    event Action<string, string>? Delivered;
    event Action<string>? Restarted;
    event Action<string, string>? Completed;
    event Action<ChatStats>? StatsChanged;
    event Action<bool>? RunningChanged;

    void Send(string prompt, string model);
    void Retry(string id);
    void Stop(string id);
}

// 会话单一数据源：Web 历史与 WPF 状态栏都从这里取数。
// 内核可用时走 DSH（agent loop 在 RDshCore 子进程里），否则降级为本地模拟流；订阅方自行marshal回 UI 线程
public sealed class ChatSessionService : IChatSessionService
{
    private readonly object _gate = new();
    private readonly List<ChatMessage> _messages = [];
    private readonly Dictionary<string, CancellationTokenSource> _streams = new();
    private readonly IMessenger _messenger;
    private readonly IDshKernelService _kernel;
    private readonly IModelCatalogService _catalog;
    private readonly string _kernelSessionId = Guid.NewGuid().ToString("n");
    private ChatStats _stats = new(0, 0, 0, 0, 0, 0);
    private string? _kernelAssistantId;
    private string _kernelText = string.Empty;
    private bool _kernelStartAttempted;
    private Stopwatch _stepWatch = new();

    public ChatSessionService(IMessenger messenger, IDshKernelService kernel, IModelCatalogService catalog)
    {
        _messenger = messenger;
        _kernel = kernel;
        _catalog = catalog;
        _kernel.SessionEvent += OnKernelEvent;
        _kernel.SessionStatus += OnKernelStatus;
    }

    public IReadOnlyList<ChatMessage> Messages
    {
        get { lock (_gate) return _messages.ToArray(); }
    }

    public ChatStats Stats
    {
        get { lock (_gate) return _stats; }
    }

    public event Action<ChatMessage>? Appended;
    public event Action<string, string>? Delivered;
    public event Action<string>? Restarted;
    public event Action<string, string>? Completed;
    public event Action<ChatStats>? StatsChanged;
    public event Action<bool>? RunningChanged;

    public void Send(string prompt, string model)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        var user = NewMessage("user", prompt.Trim(), "done");
        var assistant = NewMessage("assistant", string.Empty, "streaming", model);
        lock (_gate)
        {
            _messages.Add(user);
            _messages.Add(assistant);
        }
        Appended?.Invoke(user);
        Appended?.Invoke(assistant);
        if (_kernel.IsAvailable || !_kernelStartAttempted)
            _ = KernelSendAsync(assistant.Id, prompt.Trim(), model);
        else
            _ = StreamAsync(assistant.Id);
    }

    public void Retry(string id)
    {
        string? prompt = null;
        lock (_gate)
        {
            var index = _messages.FindIndex(m => m.Id == id && m.Role == "assistant");
            if (index < 0 || _messages[index].State == "streaming") return;
            _messages[index] = _messages[index] with { Text = string.Empty, State = "streaming", Ts = Now() };
            CancelStream(id);
            for (var i = index - 1; i >= 0; i--)
                if (_messages[i].Role == "user")
                {
                    prompt = _messages[i].Text;
                    break;
                }
        }
        Restarted?.Invoke(id);
        if (_kernel.IsAvailable && prompt is not null)
        {
            lock (_gate)
            {
                _kernelAssistantId = id;
                _kernelText = string.Empty;
            }
            _ = KernelPromptAsync(id, prompt);
        }
        else
            _ = StreamAsync(id);
    }

    public void Stop(string id)
    {
        bool kernelTurn;
        lock (_gate) kernelTurn = _kernelAssistantId == id;
        if (kernelTurn)
        {
            lock (_gate)
            {
                _kernelAssistantId = null;
                var index = _messages.FindIndex(m => m.Id == id);
                if (index >= 0) _messages[index] = _messages[index] with { State = "done" };
                _stats = _stats with { TokensPerSecond = 0 };
                PublishStatsLocked();
            }
            // SDK 协议没有单 turn cancel：停掉 runtime，下次发送时重启
            _kernelStartAttempted = false;
            _ = _kernel.ShutdownAsync();
            RunningChanged?.Invoke(false);
            Completed?.Invoke(id, "done");
            return;
        }

        bool stopped;
        lock (_gate)
        {
            stopped = CancelStream(id);
            var index = _messages.FindIndex(m => m.Id == id);
            if (stopped && index >= 0)
                _messages[index] = _messages[index] with { State = "done" };
            if (stopped)
            {
                _stats = _stats with { TokensPerSecond = 0 };
                PublishStatsLocked();
            }
        }
        if (!stopped) return;
        RunningChanged?.Invoke(false);
        Completed?.Invoke(id, "done");
    }

    private async Task KernelSendAsync(string id, string prompt, string model)
    {
        if (!_kernel.IsAvailable)
        {
            _kernelStartAttempted = true;
            var (provider, apiKey) = RouteFor(model);
            if (!await _kernel.StartAsync(provider, model, WorkspaceRoot(), apiKey).ConfigureAwait(false))
            {
                _ = StreamAsync(id);
                return;
            }
        }
        await KernelPromptAsync(id, prompt);
    }

    private async Task KernelPromptAsync(string id, string prompt)
    {
        lock (_gate)
        {
            _kernelAssistantId = id;
            _kernelText = string.Empty;
        }
        _stepWatch = Stopwatch.StartNew();
        RunningChanged?.Invoke(true);
        if (await _kernel.PromptAsync(_kernelSessionId, prompt).ConfigureAwait(false) is null)
        {
            lock (_gate) _kernelAssistantId = null;
            RunningChanged?.Invoke(false);
            _ = StreamAsync(id);
        }
    }

    private void OnKernelStatus(string sessionId, bool running)
    {
        if (sessionId != _kernelSessionId) return;
        RunningChanged?.Invoke(running);
    }

    private void OnKernelEvent(string sessionId, JsonElement ev)
    {
        if (sessionId != _kernelSessionId) return;
        var type = ev.ValueKind == JsonValueKind.Object && ev.TryGetProperty("type", out var t) ? t.GetString() : null;
        string? id;
        lock (_gate) id = _kernelAssistantId;
        if (id is null) return;

        switch (type)
        {
            case "step/start":
                lock (_gate)
                {
                    _stats = _stats with { Steps = _stats.Steps + 1 };
                    PublishStatsLocked();
                }
                _stepWatch = Stopwatch.StartNew();
                break;
            case "assistant/message":
                var text = ExtractText(ev);
                if (text.Length == 0) break;
                string previous;
                lock (_gate)
                {
                    var index = _messages.FindIndex(m => m.Id == id);
                    if (index < 0 || _messages[index].State != "streaming") return;
                    previous = _kernelText;
                    _kernelText += (_kernelText.Length > 0 ? "\n\n" : "") + text;
                    _messages[index] = _messages[index] with { Text = _kernelText };
                    ApplyUsageLocked(ev);
                    PublishStatsLocked();
                }
                if (_kernelText.StartsWith(previous, StringComparison.Ordinal))
                    Delivered?.Invoke(id, _kernelText[previous.Length..]);
                else
                {
                    Restarted?.Invoke(id);
                    Delivered?.Invoke(id, _kernelText);
                }
                break;
            case "turn/end":
                lock (_gate)
                {
                    if (_kernelAssistantId != id) return;
                    _kernelAssistantId = null;
                    var index = _messages.FindIndex(m => m.Id == id);
                    if (index >= 0) _messages[index] = _messages[index] with { State = "done" };
                    _stats = _stats with { TokensPerSecond = 0, Rounds = _stats.Rounds + 1 };
                    PublishStatsLocked();
                }
                RunningChanged?.Invoke(false);
                Completed?.Invoke(id, "done");
                break;
        }
    }

    // 调用方须持有 _gate
    private void ApplyUsageLocked(JsonElement ev)
    {
        if (!ev.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return;
        var input = usage.TryGetProperty("inputTokens", out var i) ? i.GetInt64() : 0;
        var output = usage.TryGetProperty("outputTokens", out var o) ? o.GetInt64() : 0;
        var total = usage.TryGetProperty("totalTokens", out var tt) ? tt.GetInt64() : input + output;
        var cacheRead = usage.TryGetProperty("cacheReadTokens", out var cr) ? cr.GetInt64() : 0;
        var seconds = Math.Max(0.2, _stepWatch.Elapsed.TotalSeconds);
        _stats = _stats with
        {
            TotalTokens = _stats.TotalTokens + total,
            TokensPerSecond = (int)Math.Round(output / seconds),
            CacheHitRate = input + cacheRead > 0 ? cacheRead * 100.0 / (input + cacheRead) : _stats.CacheHitRate,
        };
    }

    private static string ExtractText(JsonElement ev)
    {
        if (!ev.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return string.Empty;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;
        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                && block.TryGetProperty("text", out var text))
                parts.Add(text.GetString() ?? "");
        return string.Concat(parts);
    }

    private (string Provider, string? ApiKey) RouteFor(string model)
    {
        var option = _catalog.ModelOptions.FirstOrDefault(o => o.DisplayName == model);
        var provider = option is null ? null : _catalog.Providers.FirstOrDefault(p => p.Name == option.ProviderName);
        var id = provider?.Id ?? "deepseek";
        return (id == "deepseek" ? "deepseek-official" : id, provider?.ApiKey);
    }

    private static string WorkspaceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Responsible.slnx")))
                return dir.FullName;
        return Environment.CurrentDirectory;
    }

    private async Task StreamAsync(string id)
    {
        var cts = new CancellationTokenSource();
        lock (_gate) _streams[id] = cts;
        RunningChanged?.Invoke(true);
        var chunks = ReplyChunks();
        try
        {
            foreach (var chunk in chunks)
            {
                await Task.Delay(42, cts.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    // Stop / Retry 已移除流：在途 chunk 不得再写消息或统计
                    if (!_streams.ContainsKey(id)) return;
                    var index = _messages.FindIndex(m => m.Id == id);
                    if (index < 0) return;
                    _messages[index] = _messages[index] with { Text = _messages[index].Text + chunk };
                    _stats = _stats with
                    {
                        Steps = _stats.Steps + 1,
                        TokensPerSecond = 150 + Random.Shared.Next(70),
                        TotalTokens = _stats.TotalTokens + chunk.Length,
                        ContextPercent = Math.Min(97, _stats.ContextPercent + 0.2),
                    };
                    PublishStatsLocked();
                }
                Delivered?.Invoke(id, chunk);
            }
            lock (_gate)
            {
                if (!_streams.Remove(id)) return;
                var index = _messages.FindIndex(m => m.Id == id);
                if (index >= 0) _messages[index] = _messages[index] with { State = "done" };
                _stats = _stats with
                {
                    TokensPerSecond = 0,
                    Rounds = _stats.Rounds + 1,
                    CacheHitRate = 96.4,
                };
                PublishStatsLocked();
            }
            RunningChanged?.Invoke(false);
            Completed?.Invoke(id, "done");
        }
        catch (OperationCanceledException)
        {
            lock (_gate) _streams.Remove(id);
        }
    }

    private bool CancelStream(string id)
    {
        if (!_streams.Remove(id, out var cts)) return false;
        cts.Cancel();
        cts.Dispose();
        return true;
    }

    // 调用方须持有 _gate：统计发布与 Stop 的清零在同一锁内排序，避免在途 chunk 覆盖终态
    private void PublishStatsLocked()
    {
        StatsChanged?.Invoke(_stats);
        _messenger.Send(new ChatStatsMessage(_stats));
    }

    private static ChatMessage NewMessage(string role, string text, string state, string? model = null) =>
        new(Guid.NewGuid().ToString("n"), role, text, state, Now(), model);

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string[] ReplyChunks() => Regex.Split(Reply, @"(?<=\s)");

    private const string Reply =
        "收到。这条回复由会话服务流式产出：C# 维护消息与统计这一份数据源，WebView2 只负责渲染。\n\n" +
        "### 当前链路\n" +
        "- 输入区发送 → `ChatSessionService.Send` → user / assistant 两条消息入列；\n" +
        "- 流式增量以 `message.delta` 推给 Web 层，滚动吸附与光标都在 Web 内完成；\n" +
        "- 统计同步走 `stats` 通道，底部状态栏与 Web 统计条同源同值。\n\n" +
        "```csharp\n" +
        "public interface IChatSessionService\n" +
        "{\n" +
        "    IReadOnlyList<ChatMessage> Messages { get; }\n" +
        "    ChatStats Stats { get; }\n" +
        "    void Send(string prompt, string model);\n" +
        "}\n" +
        "```\n\n" +
        "> 重试与停止由 Web 消息动作回传 C#：retry 重跑流式循环，stop 保留已生成的部分文本。\n\n" +
        "背景色取自 App 资源字典的主题刷，导航前写入 `DefaultBackgroundColor`，深浅主题切换无闪白。";
}