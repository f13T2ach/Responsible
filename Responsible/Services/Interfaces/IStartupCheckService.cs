namespace Responsible.Services;

public sealed record StartupCheckItem(string Category, string Title, string Detail);

// 一项启动检查；实现者注册到 DI 后自动进入启动检查队列。
public interface IStartupCheck
{
    Task<IReadOnlyList<StartupCheckItem>> RunAsync(CancellationToken cancellationToken = default);
}

public interface IStartupCheckService
{
    Task<IReadOnlyList<StartupCheckItem>> RunAsync(CancellationToken cancellationToken = default);
}