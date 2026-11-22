namespace Responsible.Services;

public enum AclGrantState
{
    Pending,
    Authorized,
    Error,
}

// 一个工具目录的一次性授权。CapabilitySid 是稳定的 capability SID；
// ACL 只授予这个 SID，沙箱会话通过令牌能力获得访问，不再按会话圈地。
// IsValid 由启动检查刷新并持久化：路径或根 ACE 失效时保留记录并标记失效。
public sealed record AclToolGrant(
    string Id,
    string Path,
    string Name,
    string CapabilitySid,
    long AuthorizedAtUnixMs,
    AclGrantState State,
    string? LastError,
    bool IsValid = true);

public sealed record AclProgress(int Processed, int Total, string CurrentPath);

public sealed record AclResult(bool Success, string Message, int FailedObjects);

public sealed record AclValidationIssue(string Path, string Name, string Detail);

public interface IAclAuthorizationService
{
    IReadOnlyList<AclToolGrant> Grants { get; }

    string? LoadError { get; }

    (AclToolGrant? Grant, string? Error) Prepare(string path);

    Task<AclResult> GrantAsync(AclToolGrant grant, IProgress<AclProgress>? progress,
        CancellationToken cancellationToken = default);

    Task<AclResult> RevokeAsync(AclToolGrant grant, IProgress<AclProgress>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>重新校验全部授权并把 IsValid 写回清单；返回失效项。</summary>
    IReadOnlyList<AclValidationIssue> RefreshValidity();
}