using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Responsible.Messages;
using Responsible.ViewModel;

namespace Responsible.Services;

// 会话单一数据源：Web 历史与 WPF 状态栏都从这里取数。
// 消息全部来自 DSH（agent loop 在 RDshCore 子进程里）；token 流经插件的进程内事件管道，订阅方自行marshal回 UI 线程
public sealed partial class ChatSessionService : IChatSessionService
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Responsible", "session-state.json");

    private readonly object _gate = new();
    private readonly List<ChatMessage> _messages = [];
    private readonly List<ChatChanges> _changes = [];
    private readonly List<RunningTool> _runningTools = [];
    // DSH 的 session/resume 对已打开会话会报错；记住当前进程里已经打开的会话
    private readonly HashSet<string> _openSessions = [];
    private readonly IMessenger _messenger;
    private readonly IDshKernelService _kernel;
    private readonly IModelCatalogService _catalog;
    private readonly IWorkspaceChangeTracker _changeTracker;
    private readonly IRvmEngineService _rvm;
    private readonly IWinFspService _winFsp;
    private readonly IWorkspaceStore _workspaceStore;
    private readonly IAclAuthorizationService _acl;
    private readonly IRvmPolicyService _rvmPolicy;
    private ulong _rvmSessionId;
    private string? _rvmWorkspaceRoot;
    private string? _rvmVirtualRoot;
    private string? _rvmKey;
    private string? _startedRvmKey;
    private string _workspaceBadge = string.Empty;
    private string? _workspaceBadgeDetail;
    private readonly string? _restoredSessionId;
    // 先放上次的列表占位，真实列表由内核就绪后的刷新替换
    private IReadOnlyList<DshSessionRow> _conversations = LoadConversationsCache();
    private volatile bool _conversationsLoaded;
    // 列表读取最终失败时的诊断（英文原文）；成功刷新后清空
    private volatile string? _conversationsError;
    private string _sessionId = Guid.NewGuid().ToString("n");
    // 新建会话要落在的工作区目录；为空时用应用默认根目录
    private string? _sessionCwd;
    // 只有“在新工作区开会话”才强制 runtime cwd 与目标目录一致，切换历史会话不为此重启
    private bool _newConversationCwd;
    private string? _activeCwd;
    private int _initializeStarted;
    private int _messageVersion;
    // 启动时恢复会话：内核就绪前历史为空，但内容区应显示加载态而不是空态
    private volatile bool _sessionLoading;
    private bool _sessionPersisted;
    // SDK 的 provider/model 在 initialize 时固定；记住当前 runtime 实际使用的路由
    private string? _activeProvider;
    private string? _activeModel;
    private ChatStats _stats = new(0, 0, 0, 0, 0, 0);
    private string? _kernelAssistantId;
    // 本轮 turn/end 收盘的气泡 id：用量帧可能先于/晚于 stdio 的 turn/end 到达，用它锁定归属
    private string? _lastTurnAssistantId;
    // 已提交段的文本与当前 step 的流式段；两者拼接才是气泡内容
    private string _committedText = string.Empty;
    private string _committedReasoning = string.Empty;
    private string _stepText = string.Empty;
    private string _stepReasoning = string.Empty;
    private bool _streamOpen;
    private DateTime? _reasoningStarted;
    private int _turnSeq;

    public ChatSessionService(
        IMessenger messenger,
        IDshKernelService kernel,
        IModelCatalogService catalog,
        IWorkspaceChangeTracker changeTracker,
        IRvmEngineService rvm,
        IWinFspService winFsp,
        IWorkspaceStore workspaceStore,
        IAclAuthorizationService acl,
        IRvmPolicyService rvmPolicy)
    {
        _messenger = messenger;
        _kernel = kernel;
        _catalog = catalog;
        _changeTracker = changeTracker;
        _rvm = rvm;
        _winFsp = winFsp;
        _workspaceStore = workspaceStore;
        _acl = acl;
        _rvmPolicy = rvmPolicy;
        _restoredSessionId = LoadState();
        if (!string.IsNullOrEmpty(_restoredSessionId))
        {
            _sessionId = _restoredSessionId;
            _sessionLoading = true;
        }
        _kernel.SessionEvent += OnKernelEvent;
        _kernel.SessionStatus += OnKernelStatus;
        _kernel.AssistantStream += OnAssistantStream;
        _kernel.SessionStats += OnKernelStats;
        _kernel.TurnUsage += OnKernelTurn;
        _kernel.ToolEvent += OnKernelTool;
        _kernel.PermissionChanged += OnKernelPermission;
    }

    public IReadOnlyList<ChatMessage> Messages
    {
        get { lock (_gate) return _messages.ToArray(); }
    }

    public ChatStats Stats
    {
        get { lock (_gate) return _stats; }
    }

    public IReadOnlyList<ChatChanges> Changes
    {
        get { lock (_gate) return _changes.ToArray(); }
    }

    public IReadOnlyList<DshSessionRow> Conversations
    {
        get { lock (_gate) return _conversations; }
    }

    public bool IsConversationsLoaded => _conversationsLoaded;

    public string? ConversationsError => _conversationsError;

    public bool IsSessionLoading => _sessionLoading;

    public string CurrentSessionId
    {
        get { lock (_gate) return _sessionId; }
    }

    public string WorkingDirectory => CurrentCwd();

    public string WorkspaceBadge => _workspaceBadge;

    public string? WorkspaceBadgeDetail => _workspaceBadgeDetail;

    public event Action<ChatMessage>? Appended;
    public event Action<ChatMessage>? MessageUpdated;
    public event Action? Reloaded;
    public event Action<string, string>? Delivered;
    public event Action<string, string>? ReasoningDelivered;
    public event Action<string>? Restarted;
    public event Action<string, string>? Completed;
    public event Action<ChatChanges>? ChangesReady;
    public event Action<ChatStats>? StatsChanged;
    public event Action<bool>? RunningChanged;
    public event Action<bool>? SessionLoadingChanged;
    public event Action? WorkspaceBadgeChanged;

    private void SetSessionLoading(bool loading)
    {
        if (_sessionLoading == loading) return;
        _sessionLoading = loading;
        SessionLoadingChanged?.Invoke(loading);
    }

    public void Send(string prompt, ModelOptionViewModel? option)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        SaveState();
        var user = NewMessage("user", prompt.Trim(), "done");
        var assistant = NewMessage("assistant", string.Empty, "streaming", option?.DisplayName);
        lock (_gate)
        {
            _messages.Add(user);
            _messages.Add(assistant);
            _messageVersion++;
        }
        Appended?.Invoke(user);
        Appended?.Invoke(assistant);
        _ = KernelSendAsync(assistant.Id, prompt.Trim(), option);
    }

    public void NewConversation(string? workspaceDirectory = null)
    {
        lock (_gate)
        {
            if (_kernelAssistantId is not null) return;
            _sessionId = Guid.NewGuid().ToString("n");
            // 未显式指定工作区时沿用当前上下文所在的工作区，避免新会话被塞进无关目录
            _sessionCwd = NormalizeCwd(workspaceDirectory) ?? _sessionCwd;
            _newConversationCwd = true;
            _sessionPersisted = false;
            _messages.Clear();
            _changes.Clear();
            _messageVersion++;
            _stats = new ChatStats(0, 0, 0, 0, 0, 0);
            _kernelAssistantId = null;
            _lastTurnAssistantId = null;
            _committedText = string.Empty;
            _committedReasoning = string.Empty;
            _stepText = string.Empty;
            _stepReasoning = string.Empty;
            _streamOpen = false;
        }
        SaveState();
        // 新会话不需要恢复历史，空态应立即生效
        SetSessionLoading(false);
        ResetPermissions(keepCatalog: true);
        RunningChanged?.Invoke(false);
        Reloaded?.Invoke();
        StatsChanged?.Invoke(Stats);
        _messenger.Send(new ChatStatsMessage(Stats));
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
                _streamOpen = false;
                _stepText = string.Empty;
                _stepReasoning = string.Empty;
                var index = _messages.FindIndex(m => m.Id == id);
                if (index >= 0) _messages[index] = _messages[index] with { State = "done" };
            }
            // SDK 协议没有单 turn cancel：停掉 runtime，下次发送时重启
            _activeProvider = null;
            _activeModel = null;
            _activeCwd = null;
            lock (_gate) _openSessions.Clear();
            ResetPermissions(keepCatalog: false);
            _ = _kernel.ShutdownAsync();
            RunningChanged?.Invoke(false);
            Completed?.Invoke(id, "done");
            return;
        }

        bool streaming;
        lock (_gate)
        {
            var index = _messages.FindIndex(m => m.Id == id);
            streaming = index >= 0 && _messages[index].State == "streaming";
            if (streaming)
            {
                _messages[index] = _messages[index] with { State = "done" };
            }
        }
        if (!streaming) return;
        RunningChanged?.Invoke(false);
        Completed?.Invoke(id, "done");
    }

    // 调用方须持有 _gate：统计只镜像 DSH 投影帧，这里只负责在同一锁内发布
    private void PublishStatsLocked()
    {
        StatsChanged?.Invoke(_stats);
        _messenger.Send(new ChatStatsMessage(_stats));
    }

    private static ChatMessage NewMessage(string role, string text, string state, string? model = null) =>
        new(Guid.NewGuid().ToString("n"), role, text, state, Now(), model);

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
