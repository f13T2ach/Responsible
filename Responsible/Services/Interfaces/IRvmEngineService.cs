namespace Responsible.Services;

public sealed record RvmWorkspaceSession(ulong SessionId, string WorkspaceRoot, string VirtualRoot);

// 全局 ACL 授权在执行前同步给引擎；路径与 capability SID 必须成对出现。
public sealed record RvmToolAuthorization(string Path, string Name, string CapabilitySid);

public enum RvmChangeKind
{
    Unspecified = 0,
    Added = 1,
    Modified = 2,
    Deleted = 3,
}

public sealed record RvmFileChange(
    string Path,
    RvmChangeKind Kind,
    ulong BeforeBytes,
    ulong AfterBytes);

public sealed record RvmSyncConflict(string Path, string Reason);

public sealed record RvmWorkspaceDiff(
    IReadOnlyList<RvmFileChange> Changes,
    IReadOnlyList<RvmSyncConflict> Conflicts,
    bool Truncated);

public sealed record RvmWorkspaceApplyResult(
    IReadOnlyList<RvmFileChange> Applied,
    IReadOnlyList<RvmSyncConflict> Conflicts);

// RvmEngine.exe 的宿主进程与命名管道客户端；所有数据来自引擎真实响应，失败只报错不伪造
public interface IRvmEngineService : IDisposable
{
    bool IsRunning { get; }
    /// <summary>清掉指向 RVM 存储的遗留 subst 映射（退出兜底）。</summary>
    void CleanUpDriveMappings();
    /// <summary>引擎可用（握手成功且可用性报告为真）；未启动或不可用时为 false。</summary>
    bool IsAvailable { get; }
    /// <summary>最近一次交互的状态文本（含失败原因），供界面与日志展示。</summary>
    string Status { get; }
    /// <summary>引擎可执行文件路径；未启动时为 null。</summary>
    string? EnginePath { get; }
    /// <summary>本次引擎进程的命名管道名；未启动时为 null。</summary>
    string? PipeName { get; }
    /// <summary>引擎握手令牌；仅注入内核环境，供壳桥接进程使用。</summary>
    string? Token { get; }

    Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default);
    Task<bool> SetToolAuthorizationsAsync(
        IReadOnlyList<RvmToolAuthorization> tools,
        CancellationToken cancellationToken = default);
    Task<RvmWorkspaceSession?> CreateWorkspaceSessionAsync(
        string workspaceRoot,
        string driveLetter,
        string storageKey,
        RvmSettings? settings = null,
        IReadOnlyList<RvmToolAuthorization>? toolAccess = null,
        bool networkEnabled = false,
        IReadOnlyList<string>? devices = null,
        CancellationToken cancellationToken = default);
    Task CloseSessionAsync(ulong sessionId, CancellationToken cancellationToken = default);

    /// <summary>读取会话盘相对基线的变更与宿主冲突；失败返回 null 并写入 Status。</summary>
    Task<RvmWorkspaceDiff?> DiffWorkspaceAsync(ulong sessionId, CancellationToken cancellationToken = default);

    /// <summary>把会话盘变更同步回宿主工作区；冲突项跳过并在结果中报告。</summary>
    Task<RvmWorkspaceApplyResult?> SyncWorkspaceAsync(
        ulong sessionId,
        bool all,
        IReadOnlyList<string>? paths = null,
        CancellationToken cancellationToken = default);

    /// <summary>丢弃会话盘变更，按宿主工作区恢复；失败返回 null。</summary>
    Task<bool?> RollbackWorkspaceAsync(ulong sessionId, CancellationToken cancellationToken = default);

    Task ShutdownAsync();
}