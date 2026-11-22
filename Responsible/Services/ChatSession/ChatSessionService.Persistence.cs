using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Responsible.Messages;

namespace Responsible.Services;

public sealed partial class ChatSessionService
{
    // 上次成功读取的会话列表：启动时先铺开，避免内核启动的几秒里侧栏是空的
    private static readonly string ConversationsCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Responsible", "conversations.json");

    private static readonly JsonSerializerOptions CacheJson = new(JsonSerializerDefaults.Web);

    // 启动时恢复：拉起内核 → 会话列表与历史并行加载，列表先到先显示
    public async Task InitializeAsync()
    {
        if (Interlocked.Exchange(ref _initializeStarted, 1) == 1) return;
        // 先确定要恢复的会话目录：否则 EnsureKernelAsync 会按“目录未知”去判断工作区，
        // 受保护工作区的 RVM 会话会被直接跳过，而内核一旦就绪，后续调用又会因为
        // 绑定相同而一直早退——这就是“明明在 RVM 里却一直是普通沙箱”的来源。
        lock (_gate)
        {
            if (_sessionCwd is null)
                _sessionCwd = NormalizeCwd(
                    _conversations.FirstOrDefault(row => row.SessionId == _restoredSessionId)?.Cwd);
        }
        // 启动阶段不等 RVM 会话：先把内核与会话列表拉起来，RVM 在末尾预热
        var initWatch = System.Diagnostics.Stopwatch.StartNew();
        RvmTrace("init: begin");
        if (!await EnsureKernelAsync(null, deferRvm: true).ConfigureAwait(false))
        {
            // 内核不可用时恢复无从发生，别让加载态一直挂着
            SetSessionLoading(false);
            await RefreshConversationsAsync().ConfigureAwait(false);
            return;
        }

        RvmTrace($"init: kernel up {initWatch.ElapsedMilliseconds}ms");
        var conversations = RefreshConversationsWithRetryAsync();
        var permissions = LoadPermissionCatalogAsync();
        if (!string.IsNullOrEmpty(_restoredSessionId))
            await ReloadSessionAsync(_restoredSessionId, resume: true).ConfigureAwait(false);
        await conversations.ConfigureAwait(false);
        RvmTrace($"init: conversations {initWatch.ElapsedMilliseconds}ms");
        await permissions.ConfigureAwait(false);
        RvmTrace($"init: permissions {initWatch.ElapsedMilliseconds}ms");

        // 恢复的会话没有经过 OpenConversation：补上它的工作区，后续新建对话沿用同一目录
        lock (_gate)
        {
            if (_sessionCwd is null)
                _sessionCwd = NormalizeCwd(
                    _conversations.FirstOrDefault(row => row.SessionId == _sessionId)?.Cwd);
        }
        // 目录定下来之后再补一次：受保护工作区要在这里把会话盘挂上。
        // 启动不等它——它会因绑定变化触发一次内核重启，那属于后台动作。
        _ = WarmRvmSessionAsync();
    }

    public async Task RefreshConversationsAsync()
    {
        if (await TryRefreshConversationsAsync().ConfigureAwait(false)) return;
        CompleteConversationLoad();
    }

    // 内核刚就绪时插件桥接可能仍在挂载：失败后短暂重试，列表不能因一次竞态一直空着
    private async Task RefreshConversationsWithRetryAsync()
    {
        // 整体限时：单次桥接请求 15s 超时，5 次重试不能把加载态拖成一分多钟
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (await TryRefreshConversationsAsync(deadline.Token).ConfigureAwait(false)) return;
            if (deadline.IsCancellationRequested) break;
            try
            {
                await Task.Delay(800, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // 重试耗尽也要结束加载态，否则侧栏会一直停在“正在加载会话”
        CompleteConversationLoad();
    }

    private async Task<bool> TryRefreshConversationsAsync(CancellationToken cancellationToken = default)
    {
        if (!_kernel.IsAvailable)
        {
            if (!_conversationsLoaded) _conversationsError = DescribeConversationError();
            return false;
        }
        IReadOnlyList<DshSessionRow>? rows;
        try
        {
            rows = await _kernel.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!_conversationsLoaded) _conversationsError = "Timed out reading the conversation list.";
            return false;
        }
        // 读取失败时保留已有列表，不用空结果覆盖
        if (rows is null)
        {
            if (!_conversationsLoaded) _conversationsError = DescribeConversationError();
            return false;
        }
        lock (_gate)
        {
            _conversations = rows;
            _conversationsLoaded = true;
        }
        _conversationsError = null;
        SaveConversationsCache(rows);
        _messenger.Send(new ConversationsChangedMessage(rows));
        return true;
    }

    private void CompleteConversationLoad()
    {
        bool changed;
        lock (_gate)
        {
            changed = !_conversationsLoaded;
            _conversationsLoaded = true;
        }
        if (changed) _messenger.Send(new ConversationsChangedMessage(Conversations));
    }

    private string DescribeConversationError()
    {
        const string prefix = "Conversation list unavailable: ";
        var status = _kernel.Status;
        if (status.StartsWith(prefix, StringComparison.Ordinal))
            status = status[prefix.Length..];
        return string.IsNullOrWhiteSpace(status) ? "The DSH kernel is not available." : status;
    }

    private static IReadOnlyList<DshSessionRow> LoadConversationsCache()
    {
        try
        {
            if (!File.Exists(ConversationsCachePath)) return [];
            return JsonSerializer.Deserialize<DshSessionRow[]>(
                       File.ReadAllText(ConversationsCachePath), CacheJson)
                   ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void SaveConversationsCache(IReadOnlyList<DshSessionRow> rows)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConversationsCachePath)!);
            File.WriteAllText(ConversationsCachePath, JsonSerializer.Serialize(rows, CacheJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async Task OpenConversationAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        var alreadyCurrent = false;
        lock (_gate)
        {
            if (_kernelAssistantId is not null) return;
            if (sessionId == _sessionId)
            {
                alreadyCurrent = true;
            }
            else
            {
                _sessionCwd = NormalizeCwd(
                    _conversations.FirstOrDefault(row => row.SessionId == sessionId)?.Cwd);
                _newConversationCwd = false;
            }
        }

        if (alreadyCurrent)
        {
            // 点的就是当前会话：内核可能早就起来了，但这不代表 RVM 会话建过。
            // 预热在后台跑，打开会话不该等它（建会话盘可能要十几秒）。
            _ = WarmRvmSessionAsync();
            return;
        }

        // 同样不在打开路径上等 RVM：先用 defer 把内核与会话拉出来
        var openWatch = System.Diagnostics.Stopwatch.StartNew();
        RvmTrace($"open: begin session={sessionId} cwd={_sessionCwd}");
        if (!await EnsureKernelAsync(null, deferRvm: true).ConfigureAwait(false)) return;
        RvmTrace($"open: kernel ready {openWatch.ElapsedMilliseconds}ms");
        await ReloadSessionAsync(sessionId, resume: true).ConfigureAwait(false);
        RvmTrace($"open: history rendered {openWatch.ElapsedMilliseconds}ms");
        _ = WarmRvmSessionAsync();
    }

    public async Task<bool> DeleteConversationAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (_gate)
        {
            if (_kernelAssistantId is not null) return false;
        }

        var deleted = await _kernel.DeleteSessionAsync(sessionId).ConfigureAwait(false);
        if (!deleted) return false;
        DeletePersistedChanges(sessionId);

        bool wasCurrent;
        string? cwd;
        lock (_gate)
        {
            wasCurrent = sessionId == _sessionId;
            cwd = _sessionCwd;
            _conversations = _conversations.Where(row => row.SessionId != sessionId).ToArray();
            _openSessions.Clear();
            // 删除会关闭 runtime，路由与会话目录状态一并失效
            _activeProvider = null;
            _activeModel = null;
            _activeCwd = null;
            _newConversationCwd = false;
        }

        // 删掉的是当前会话：留在同一工作区开一个空白会话
        if (wasCurrent)
            NewConversation(cwd);
        var remaining = Conversations;
        SaveConversationsCache(remaining);
        _messenger.Send(new ConversationsChangedMessage(remaining));
        return true;
    }

    private async Task ReloadSessionAsync(string sessionId, bool resume)
    {
        SetSessionLoading(true);
        try
        {
            await ReloadSessionCoreAsync(sessionId, resume).ConfigureAwait(false);
        }
        finally
        {
            SetSessionLoading(false);
        }
    }

    private async Task ReloadSessionCoreAsync(string sessionId, bool resume)
    {
        int version;
        bool alreadyOpen;
        lock (_gate)
        {
            version = _messageVersion;
            alreadyOpen = _openSessions.Contains(sessionId);
            // 切换会话时旧权限不得挂到新会话上；读取结果落定后再显示
            _permissionValue = null;
        }
        PermissionsChanged?.Invoke();
        // resume、历史、统计与权限互不依赖，并发执行以缩短切换会话的等待
        var resumeTask = resume && !alreadyOpen
            ? ResumeSessionAsync(sessionId)
            : Task.FromResult(false);
        var historyTask = _kernel.ReadHistoryAsync(sessionId);
        var statsTask = _kernel.ReadStatsAsync(sessionId);
        var permissionTask = _kernel.GetPermissionAsync(sessionId);
        var readWatch = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(resumeTask, historyTask, statsTask, permissionTask).ConfigureAwait(false);
        RvmTrace($"open: bridge reads done {readWatch.ElapsedMilliseconds}ms (resume={resume} alreadyOpen={alreadyOpen})");
        var history = await historyTask.ConfigureAwait(false);
        var stats = await statsTask.ConfigureAwait(false);
        var permission = await permissionTask.ConfigureAwait(false);

        var restored = new List<ChatMessage>(history.Count);
        foreach (var (message, index) in history.Select((message, index) => (message, index)))
        {
            var role = message.Role == "user" ? "user" : "assistant";
            var tools = message.Tools.Count == 0 ? null : message.Tools.Select(ToChatTool).ToArray();
            var parts = message.Parts.Count == 0
                ? null
                : message.Parts
                    .Select(part => new ChatPart(
                        part.Kind,
                        part.Text,
                        part.Tool is null ? null : ToChatTool(part.Tool)))
                    .ToArray();
            restored.Add(new ChatMessage(
                $"{sessionId}-{index}",
                role,
                message.Text,
                "done",
                message.Ts,
                message.Model,
                message.Reasoning,
                Usage: message.Usage is { } usage ? ToChatUsage(usage) : null,
                BranchSeq: message.BranchSeq,
                BranchUnavailable: message.BranchUnavailable,
                Tools: tools,
                Parts: parts));
        }

        lock (_gate)
        {
            // 历史加载期间若用户已经发送或新建，宁可放弃这次恢复也不覆盖新消息
            if (_messageVersion != version) return;
            _sessionId = sessionId;
            _sessionPersisted = true;
            _messageVersion++;
            _messages.Clear();
            _messages.AddRange(restored);
            // 打开会话时把这一轮轮改动读回来，别让历史卡片凭空消失
            _changes.Clear();
            var restoredChanges = LoadPersistedChanges(sessionId);
            _changes.AddRange(restoredChanges);
            // 轮次编号从已恢复的记录继续，否则重启后第一轮又变成 turn 1（卡片和 diff 会对错）
            _turnSeq = Math.Max(_turnSeq, restoredChanges.Count);
            RvmTrace($"reload: restored {restoredChanges.Count} change row(s) for {sessionId}");
            _kernelAssistantId = null;
            _lastTurnAssistantId = null;
            _committedText = string.Empty;
            _committedReasoning = string.Empty;
            _stepText = string.Empty;
            _stepReasoning = string.Empty;
            _streamOpen = false;
            _stats = stats is null ? new ChatStats(0, 0, 0, 0, 0, 0) : ToChatStats(stats);
            _permissionValue = permission?.CurrentValue;
        }
        SaveState();
        RunningChanged?.Invoke(false);
        Reloaded?.Invoke();
        StatsChanged?.Invoke(Stats);
        _messenger.Send(new ChatStatsMessage(Stats));
        PermissionsChanged?.Invoke();
    }

    private async Task<bool> ResumeSessionAsync(string sessionId)
    {
        if (!await _kernel.ResumeAsync(sessionId).ConfigureAwait(false)) return false;
        lock (_gate) _openSessions.Add(sessionId);
        return true;
    }

    private static ChatToolCall ToChatTool(DshToolCall tool) => new(
        tool.CallId,
        tool.Name,
        tool.Variant,
        tool.Summary,
        tool.State,
        tool.Output,
        tool.Input,
        tool.Ts,
        tool.IsError);

    private static string? LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            return doc.RootElement.TryGetProperty("sessionId", out var id) ? id.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new { sessionId = CurrentSessionId }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string CurrentCwd()
    {
        lock (_gate) return _sessionCwd ?? DefaultWorkspaceDirectory();
    }

    private static string? NormalizeCwd(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        try
        {
            var full = Path.GetFullPath(directory);
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // 未指定工作区时的会话目录：与 DSH 自身的首次使用工作区一致（<Documents>\deepseek-harness\default-workspace），
    // 不再从应用安装目录反推“Responsible”工作区
    private static string DefaultWorkspaceDirectory()
    {
        var over = Environment.GetEnvironmentVariable("RESPONSIBLE_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(over) && Directory.Exists(over))
            return Path.GetFullPath(over);

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents) || !Directory.Exists(documents))
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(documents) || !Directory.Exists(documents))
            return Path.GetTempPath();

        var root = Path.Combine(documents, "deepseek-harness", "default-workspace");
        try
        {
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);
            return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return documents;
        }
    }
}
