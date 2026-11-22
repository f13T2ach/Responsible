using System.Text.Json;

namespace Responsible.Services;

public sealed record DshApproval(string SessionId, string ToolName, string? Reason);

public sealed record DshBridgeInfo(string PipeName, string Token, int Port);

// 内核启动时附带的 RVM 会话：shell 工具改成经 RvmEngine --shell-bridge 在隔离会话里执行。
// WorkspaceRoot 是会话盘上的挂载路径，HostWorkspaceRoot 是它对应的工作区宿主目录。
public sealed record DshRvmBinding(
    string EnginePath,
    string PipeName,
    string Token,
    ulong SessionId,
    string WorkspaceRoot,
    string HostWorkspaceRoot);

public sealed record DshSessionRow(
    string SessionId,
    string Title,
    string? Cwd,
    long CreatedAt,
    long UpdatedAt,
    bool Live);

public sealed record DshModelRow(
    string ProviderId,
    string ProviderName,
    string ModelId,
    string ModelName);

// DSH 进程级权限目录（permissionPresets.catalog）：选项与默认值全部来自内核配置
public sealed record DshPermissionOption(string Value, string Name, string? Description);

public sealed record DshPermissionCatalog(IReadOnlyList<DshPermissionOption> Options, string DefaultPreset);

public sealed record DshPermissionSelection(string SessionId, string? CurrentValue);

public sealed record DshUsageRoute(string Provider, string Model);

// 一轮的权威账单：与 DSH token-meter 的 deriveTurnTokenUsage 同口径，缺桶或生命周期不完整即整轮不可披露
public sealed record DshTurnUsage(
    long UncachedInputTokens,
    long OutputTokens,
    long TotalTokens,
    long? CacheReadTokens,
    long? CacheWriteTokens,
    long? ReasoningTokens,
    IReadOnlyList<DshUsageRoute> Routes);

public sealed record DshTurnFrame(string SessionId, int Turn, long Seq, DshTurnUsage Usage);

// 持久化工具调用：分类/摘要/入参来自 tool/call，状态与输出来自 tool/result
public sealed record DshToolCall(
    string CallId,
    string Name,
    string Variant,
    string Summary,
    string State,
    string? Output,
    string? Input,
    long Ts,
    bool IsError);

public sealed record DshHistoryPart(string Kind, string Text, DshToolCall? Tool);

public sealed record DshHistoryMessage(
    string Role,
    string Text,
    string? Reasoning,
    long Ts,
    string? Model,
    DshTurnUsage? Usage,
    long? BranchSeq,
    bool BranchUnavailable,
    IReadOnlyList<DshToolCall> Tools,
    IReadOnlyList<DshHistoryPart> Parts);

// 工具调用实时帧：phase=call 开始（带分类与摘要），phase=result 落定（带输出/错误）
public sealed record DshToolCallFrame(
    string Phase,
    string CallId,
    string Name,
    string Variant,
    string Summary,
    string Input,
    long Ts,
    string? Output,
    bool IsError,
    string? Error);

// DSH 投影统计（sessionStats + tokenUsage + contextPressure），宿主不做任何本地累计或估算
public sealed record DshSessionStats(
    int Turns,
    int Steps,
    long LlmMs,
    long ToolMs,
    long TtftMs,
    long DecodeMs,
    long DecodeTokens,
    double TokensPerSecond,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long TotalTokens,
    double CacheHitRate,
    long? PressureTokens,
    long? ContextWindow);

public interface IDshKernelService
{
    bool IsAvailable { get; }
    string Status { get; }
    DshBridgeInfo Bridge { get; }

    event Action<string, JsonElement>? SessionEvent;
    event Action<string, bool>? SessionStatus;
    // DSH 进程内的 assistant 流帧（text/reasoning delta），经插件事件管道推送
    event Action<string, JsonElement>? AssistantStream;
    // DSH 投影统计帧：轮次/步数/tok/s 与 token 桶全部来自内核的 sessionStats/tokenUsage/contextPressure
    event Action<string, DshSessionStats>? SessionStats;
    // 内核在 turn/end 处算出的本轮用量（token-meter 同口径），宿主只负责挂到收盘回复上
    event Action<string, DshTurnFrame>? TurnUsage;
    // 会话权限值（permissions 投影）变更；值与 DSH Web 端读取的同一份投影一致
    event Action<string, string>? PermissionChanged;
    // 工具调用生命周期帧（tool/call、tool/result），宿主挂到本轮回复上展示
    event Action<string, DshToolCallFrame>? ToolEvent;

    Task<bool> StartAsync(string provider, string model, string cwd, string? apiKey,
        DshRvmBinding? rvm = null, string? rvmUnavailable = null);
    Task<bool> ResumeAsync(string sessionId);
    Task<string?> PromptAsync(string sessionId, string text);
    /// <summary>在指定轮次边界创建分支会话；成功返回子会话 id，失败返回 null 并写入 Status。</summary>
    Task<string?> ForkSessionAsync(string sessionId, long atSeq);
    /// <summary>读取会话列表；读取失败返回 null（调用方不得据此清空已有列表）。</summary>
    Task<IReadOnlyList<DshSessionRow>?> ListSessionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DshHistoryMessage>> ReadHistoryAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<DshSessionStats?> ReadStatsAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DshModelRow>> ListModelsAsync(CancellationToken cancellationToken = default);
    Task<bool> SetCredentialAsync(string reference, string value, CancellationToken cancellationToken = default);
    /// <summary>读取进程级权限目录；内核未就绪或该组合未挂载权限服务时返回 null。</summary>
    Task<DshPermissionCatalog?> GetPermissionCatalogAsync(CancellationToken cancellationToken = default);
    /// <summary>读取某个会话的当前权限值；读取失败返回 null。</summary>
    Task<DshPermissionSelection?> GetPermissionAsync(string sessionId, CancellationToken cancellationToken = default);
    /// <summary>切换 live 会话的权限 preset；成功返回切换后的值，失败返回 null 并写入 Status。</summary>
    Task<string?> SetPermissionPresetAsync(string sessionId, string preset, CancellationToken cancellationToken = default);
    /// <summary>关闭 runtime 并删除该会话的持久化目录与投影缓存。</summary>
    Task<bool> DeleteSessionAsync(string sessionId);
    Task ShutdownAsync();
}
