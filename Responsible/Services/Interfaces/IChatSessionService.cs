using Responsible.ViewModel;

namespace Responsible.Services;

public sealed record ChatUsageRoute(string Provider, string Model);

public sealed record ChatTurnUsage(
    long UncachedInputTokens,
    long OutputTokens,
    long TotalTokens,
    long? CacheReadTokens,
    long? CacheWriteTokens,
    long? ReasoningTokens,
    IReadOnlyList<ChatUsageRoute> Routes);

// 工具调用条目：variant 与 DSH 行模型同分类，summary 优先取命令/路径等有意义的首行
public sealed record ChatToolCall(
    string CallId,
    string Name,
    string Variant,
    string Summary,
    string State,
    string? Output,
    string? Input,
    long Ts,
    bool IsError = false);

// 一轮内的时序分段：推理、正文、工具调用按实际发生顺序排列
public sealed record ChatPart(string Kind, string Text, ChatToolCall? Tool = null);

public sealed record ChatMessage(
    string Id,
    string Role,
    string Text,
    string State,
    long Ts,
    string? Model = null,
    string? Reasoning = null,
    long? ReasoningMs = null,
    ChatTurnUsage? Usage = null,
    long? BranchSeq = null,
    bool BranchUnavailable = false,
    IReadOnlyList<ChatToolCall>? Tools = null,
    IReadOnlyList<ChatPart>? Parts = null);

public sealed record ChatStats(
    int Rounds,
    int Steps,
    int TokensPerSecond,
    long TotalTokens,
    double CacheHitRate,
    double ContextPercent);

public sealed record KernelRestartResult(bool Restarted, string Message);

public sealed record BranchResult(bool Ok, string Message);

public sealed record PermissionResult(bool Ok, string Message);

public interface IChatSessionService
{
    IReadOnlyList<ChatMessage> Messages { get; }
    ChatStats Stats { get; }
    IReadOnlyList<ChatChanges> Changes { get; }
    /// <summary>会话列表；内核就绪前可能是上次运行缓存的占位数据。</summary>
    IReadOnlyList<DshSessionRow> Conversations { get; }
    /// <summary>真实会话列表是否已从 DSH 读取成功；此前不允许新建会话/工作区。</summary>
    bool IsConversationsLoaded { get; }
    /// <summary>是否正在恢复/切换会话；历史尚未落定时内容区显示加载态而不是空态。</summary>
    bool IsSessionLoading { get; }
    string CurrentSessionId { get; }
    string WorkingDirectory { get; }
    /// <summary>工作区隔离徽标：RVM 叠加盘就绪或不可用；内核未启动时为空。</summary>
    string WorkspaceBadge { get; }
    /// <summary>徽标详情：盘符映射或 RVM 不可用的真实原因。</summary>
    string? WorkspaceBadgeDetail { get; }
    /// <summary>DSH 进程级权限目录；内核未就绪或未挂载权限服务时为 null（不显示控件）。</summary>
    DshPermissionCatalog? PermissionCatalog { get; }
    /// <summary>当前会话的权限值（permissions 投影）；会话尚未建立或读取失败时为 null。</summary>
    string? PermissionValue { get; }
    /// <summary>当前会话所在工作区是否启用了 RVM 隔离保护（工作区显式选项，不是默认值）。</summary>
    bool IsRvmProtected { get; }

    /// <summary>当前对话是否已挂载 RVM 会话盘。</summary>
    bool HasActiveRvmSession { get; }

    /// <summary>当前会话盘根目录（如 R:\）；无会话时为 null。</summary>
    string? ActiveRvmRoot { get; }

    Task<RvmWorkspaceDiff?> DiffRvmWorkspaceAsync(CancellationToken cancellationToken = default);

    Task<RvmWorkspaceApplyResult?> SyncRvmWorkspaceAsync(
        bool all,
        IReadOnlyList<string>? paths = null,
        CancellationToken cancellationToken = default);

    Task<bool?> RollbackRvmWorkspaceAsync(CancellationToken cancellationToken = default);
    /// <summary>当前 cwd 归属的工作区；未登记时为 null。</summary>
    WorkspaceDefinition? CurrentWorkspace { get; }

    event Action<ChatMessage>? Appended;
    event Action<ChatMessage>? MessageUpdated;
    event Action? Reloaded;
    event Action<string, string>? Delivered;
    event Action<string, string>? ReasoningDelivered;
    event Action<string>? Restarted;
    event Action<string, string>? Completed;
    event Action<ChatChanges>? ChangesReady;
    event Action<ChatStats>? StatsChanged;
    event Action<bool>? RunningChanged;
    /// <summary>会话恢复/切换开始与结束；界面据此在空历史时显示加载态。</summary>
    event Action<bool>? SessionLoadingChanged;
    /// <summary>权限目录或当前权限值变化；界面据此刷新输入框下方的审批等级。</summary>
    event Action? PermissionsChanged;
    /// <summary>工作区隔离徽标变化（RVM 就绪 / 不可用）。</summary>
    event Action? WorkspaceBadgeChanged;

    void Send(string prompt, ModelOptionViewModel? option);
    void Stop(string id);
    void NewConversation(string? workspaceDirectory = null);
    /// <summary>从该消息所在轮次的结束边界创建分支会话，并在成功后切换过去。</summary>
    Task<BranchResult> BranchAsync(string messageId);
    Task InitializeAsync();
    Task RefreshConversationsAsync();
    Task OpenConversationAsync(string sessionId);
    /// <summary>删除本地会话记录；返回是否实际删除了磁盘文件。</summary>
    Task<bool> DeleteConversationAsync(string sessionId);
    Task<KernelRestartResult> RestartKernelAsync();
    /// <summary>切换当前 live 会话的权限 preset（沙箱模式 + 审批策略）。</summary>
    Task<PermissionResult> SelectPermissionAsync(string preset);
    /// <summary>开启当前工作区的 RVM 隔离保护；settings 为 null 时沿用已保存设置。</summary>
    Task<PermissionResult> EnableRvmProtectionAsync(RvmSettings? settings);
    /// <summary>保存工作区的环境变量/工具目录；正在受保护时重建会话。</summary>
    Task<PermissionResult> UpdateRvmSettingsAsync(string workspaceId, RvmSettings settings);
}
