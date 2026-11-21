namespace Responsible.MoeViews.Plugins;

public enum PluginOrigin
{
    User,
    BuiltIn,
}

public enum PluginRuntimeStatus
{
    Active,
    Stopped,
    Failed,
}

// 插件页快照：Enabled 是“期望状态”（已保存的覆盖值优先），Status 是当前内核里的实际状态
// ToggleId 是宿主覆盖层 patch 的目标行 id（DSH patchId），可能与运行时 entryId 不同
public sealed record MoePluginInfo(
    string EntryId,
    string ToggleId,
    string ModuleName,
    string Title,
    string? Description,
    bool Enabled,
    bool PendingRestart,
    bool CanToggle,
    string? ReadOnlyReason,
    PluginOrigin Origin,
    PluginRuntimeStatus Status,
    string? Diagnostic);

public sealed record MoePluginInventory(
    IReadOnlyList<MoePluginInfo> Plugins,
    bool IsLive,
    DateTimeOffset? CapturedAt,
    string StatusText,
    int PendingCount);

public sealed record PluginRestartResult(bool Succeeded, string Message);

public interface IDshPluginService
{
    Task<MoePluginInventory> GetInventoryAsync(CancellationToken cancellationToken = default);

    Task<MoePluginInventory> SetEnabledAsync(string toggleId, bool enabled, CancellationToken cancellationToken = default);

    Task<PluginRestartResult> RestartKernelAsync(CancellationToken cancellationToken = default);
}