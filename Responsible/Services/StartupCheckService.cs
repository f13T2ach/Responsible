namespace Responsible.Services;

// 启动检查队列：每次启动按注册顺序执行全部检查，聚合失效项。
public sealed class StartupCheckService(IEnumerable<IStartupCheck> checks) : IStartupCheckService
{
    public async Task<IReadOnlyList<StartupCheckItem>> RunAsync(CancellationToken cancellationToken = default)
    {
        var issues = new List<StartupCheckItem>();
        foreach (var check in checks)
            issues.AddRange(await check.RunAsync(cancellationToken).ConfigureAwait(false));
        return issues;
    }
}

// ACL 授权清单的持久化有效性：目录消失、根 ACE 丢失或 SID 不匹配都算失效，
// 记录保留并标记 IsValid=false，由 RVM 实现页面以删除线展示。
public sealed class AclValidityStartupCheck(IAclAuthorizationService acl) : IStartupCheck
{
    public Task<IReadOnlyList<StartupCheckItem>> RunAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<StartupCheckItem>>(() =>
        {
            var issues = new List<StartupCheckItem>();
            if (acl.LoadError is { Length: > 0 } loadError)
                issues.Add(new StartupCheckItem("ACL grants", "Failed to read the grant manifest", loadError));
            foreach (var issue in acl.RefreshValidity())
                issues.Add(new StartupCheckItem("ACL grants", issue.Name, $"{issue.Path}: {issue.Detail}"));
            return issues;
        }, cancellationToken);
}

public sealed class RvmEngineStartupCheck : IStartupCheck
{
    public Task<IReadOnlyList<StartupCheckItem>> RunAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StartupCheckItem> issues = RvmEngineService.ResolveEnginePath() is null
            ? [new StartupCheckItem("RVM engine", "RvmEngine.exe was not found",
                "Protected workspaces cannot start. Build the RVM engine, or point RESPONSIBLE_RVM_ENGINE at the engine path.")]
            : [];
        return Task.FromResult(issues);
    }
}