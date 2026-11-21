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
        if (!await EnsureKernelAsync(null).ConfigureAwait(false))
        {
            // 内核不可用时恢复无从发生，别让加载态一直挂着
            SetSessionLoading(false);
            await RefreshConversationsAsync().ConfigureAwait(false);
            return;
        }

        var conversations = RefreshConversationsWithRetryAsync();
        var permissions = LoadPermissionCatalogAsync();
        if (!string.IsNullOrEmpty(_restoredSessionId))
            await ReloadSessionAsync(_restoredSessionId, resume: true).ConfigureAwait(false);
        await conversations.ConfigureAwait(false);
        await permissions.ConfigureAwait(false);

        // 恢复的会话没有经过 OpenConversation：补上它的工作区，后续新建对话沿用同一目录
        lock (_gate)
        {
            if (_sessionCwd is null)
                _sessionCwd = NormalizeCwd(
                    _conversations.FirstOrDefault(row => row.SessionId == _sessionId)?.Cwd);
        }
    }

    public async Task RefreshConversationsAsync()
    {
        await TryRefreshConversationsAsync().ConfigureAwait(false);
    }

    // 内核刚就绪时插件桥接可能仍在挂载：失败后短暂重试，列表不能因一次竞态一直空着
    private async Task RefreshConversationsWithRetryAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (await TryRefreshConversationsAsync().ConfigureAwait(false)) return;
            await Task.Delay(800).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryRefreshConversationsAsync()
    {
        if (!_kernel.IsAvailable) return false;
        var rows = await _kernel.ListSessionsAsync().ConfigureAwait(false);
        // 读取失败时保留已有列表，不用空结果覆盖
        if (rows is null) return false;
        lock (_gate)
        {
            _conversations = rows;
            _conversationsLoaded = true;
        }
        SaveConversationsCache(rows);
        _messenger.Send(new ConversationsChangedMessage(rows));
        return true;
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
        lock (_gate)
        {
            if (_kernelAssistantId is not null) return;
            if (sessionId == _sessionId) return;
            _sessionCwd = NormalizeCwd(
                _conversations.FirstOrDefault(row => row.SessionId == sessionId)?.Cwd);
            _newConversationCwd = false;
        }

        if (!await EnsureKernelAsync(null).ConfigureAwait(false)) return;
        await ReloadSessionAsync(sessionId, resume: true).ConfigureAwait(false);
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
        await Task.WhenAll(resumeTask, historyTask, statsTask, permissionTask).ConfigureAwait(false);
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
            _changes.Clear();
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
