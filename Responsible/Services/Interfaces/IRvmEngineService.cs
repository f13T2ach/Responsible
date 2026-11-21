namespace Responsible.Services;

public sealed record RvmWorkspaceSession(ulong SessionId, string WorkspaceRoot, string VirtualRoot);

// RvmEngine.exe 的宿主进程与命名管道客户端；所有数据来自引擎真实响应，失败只报错不伪造
public interface IRvmEngineService : IDisposable
{
    bool IsRunning { get; }
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
    Task<RvmWorkspaceSession?> CreateWorkspaceSessionAsync(
        string workspaceRoot,
        string driveLetter,
        string storageKey,
        CancellationToken cancellationToken = default);
    Task CloseSessionAsync(ulong sessionId, CancellationToken cancellationToken = default);
    Task ShutdownAsync();
}