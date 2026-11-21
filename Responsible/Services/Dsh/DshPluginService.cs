using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Responsible.MoeViews.Plugins;

namespace Responsible.Services;

public sealed class DshPluginService : IDshPluginService
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Responsible", "plugin-cache.json");

    // 宿主策略：模型侧的插件管理工具由 Responsible 关闭，不在页面中暴露
    private static readonly HashSet<string> HiddenEntryIds = new(StringComparer.Ordinal)
    {
        "tool-plugin-manager",
    };

    private static readonly HashSet<string> HostProtectedEntryIds = new(StringComparer.Ordinal)
    {
        "responsible-host", "plugin-manager", "include", "sdk-app-startup", "sdk-jsonrpc-server",
    };

    private static readonly HashSet<string> HostProtectedModules = new(StringComparer.Ordinal)
    {
        "@deepseek-ai/dsh-plugin-manager",
        "@deepseek-ai/dsh-plugin-manager/tools",
        "@deepseek-ai/dsh-sdk-app",
        "@deepseek-ai/dsh-sdk-jsonrpc-server",
        "@deepseek-ai/cordis-plugin-loader",
        "@deepseek-ai/cordis-plugin-include",
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDshKernelService _kernel;
    private readonly IChatSessionService _session;
    private readonly PluginOverridesStore _overrides;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PluginCache? _cache;

    public DshPluginService(
        IDshKernelService kernel, IChatSessionService session, PluginOverridesStore overrides)
    {
        _kernel = kernel;
        _session = session;
        _overrides = overrides;
        _cache = LoadCache();
    }

    public async Task<MoePluginInventory> GetInventoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MoePluginInventory> SetEnabledAsync(
        string toggleId, bool enabled, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _overrides.Save(toggleId, enabled, applied: false);
            return await ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PluginRestartResult> RestartKernelAsync(CancellationToken cancellationToken = default)
    {
        var result = await _session.RestartKernelAsync().ConfigureAwait(false);
        return new PluginRestartResult(result.Restarted, result.Message);
    }

    private async Task<MoePluginInventory> ReadInventoryAsync(CancellationToken cancellationToken)
    {
        BridgePluginRow[]? live = null;
        string? liveError = null;
        if (_kernel.IsAvailable)
        {
            try
            {
                live = (await DshBridgeClient.ListAsync(_kernel.Bridge, cancellationToken)
                    .ConfigureAwait(false)).ToArray();
                _cache = new PluginCache(DateTimeOffset.Now, live);
                SaveCache(_cache);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or JsonException)
            {
                liveError = ex.Message;
            }
        }

        var rows = live ?? _cache?.Plugins ?? [];
        ReconcileApplied(rows);
        return Build(rows, live is not null, liveError);
    }

    private MoePluginInventory Build(BridgePluginRow[] rows, bool isLive, string? liveError)
    {
        var plugins = new List<MoePluginInfo>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.EntryId) || string.IsNullOrWhiteSpace(row.ModuleName))
                continue;
            if (HiddenEntryIds.Contains(row.EntryId))
                continue;

            var hostProtected = HostProtectedEntryIds.Contains(row.EntryId)
                                || HostProtectedModules.Contains(row.ModuleName);
            var canToggle = !hostProtected && row.ReadOnlyReason is null && !string.IsNullOrEmpty(row.PatchId);
            var readOnlyReason = canToggle
                ? null
                : hostProtected
                    ? "Responsible 宿主必需，无法停用。"
                    : row.ReadOnlyReason == "management-required"
                        ? "DSH 运行必需，无法停用。"
                        : "该插件不由 DSH patch 管理，无法在此停用。";

            var toggleId = row.PatchId ?? row.EntryId;
            var over = canToggle ? _overrides.Find(toggleId) : null;
            var desiredEnabled = over?.Enabled ?? row.Enabled;
            var pending = over is { Applied: false };
            var origin = row.ModuleName.StartsWith("@deepseek-ai/", StringComparison.Ordinal)
                ? PluginOrigin.BuiltIn
                : PluginOrigin.User;
            var runtimeStatus = row.FiberPhase == "failed"
                ? PluginRuntimeStatus.Failed
                : row.Enabled
                    ? PluginRuntimeStatus.Active
                    : PluginRuntimeStatus.Stopped;

            var isHostPlugin = row.EntryId == "responsible-host";
            plugins.Add(new MoePluginInfo(
                row.EntryId,
                toggleId,
                row.ModuleName,
                isHostPlugin ? "Responsible 宿主集成" : DisplayName(row.Title, row.ModuleName),
                isHostPlugin ? "向 DSH 注入 Responsible 的人设与插件桥接。" : row.Description,
                desiredEnabled,
                pending,
                canToggle,
                readOnlyReason,
                origin,
                runtimeStatus,
                row.Diagnostic));
        }

        var status = isLive
            ? $"内核运行中 · {plugins.Count} 个插件"
            : _cache is not null
                ? $"{(liveError is null ? "内核未启动" : "内核未响应")} · 上次读取 {_cache.CapturedAt.ToLocalTime():MM-dd HH:mm}"
                : "内核未启动 · 暂无插件缓存";
        var pendingCount = plugins.Count(plugin => plugin.PendingRestart);
        return new MoePluginInventory(plugins, isLive, _cache?.CapturedAt, status, pendingCount);
    }

    private void ReconcileApplied(BridgePluginRow[] rows)
    {
        foreach (var row in rows)
        {
            if (row.PatchId is not { } patchId)
                continue;
            var current = _overrides.Find(patchId);
            if (current is { Applied: false } && current.Enabled == row.Enabled)
                _overrides.SetApplied(patchId, applied: true);
        }
    }

    private static string DisplayName(string? title, string moduleName)
    {
        if (!string.IsNullOrWhiteSpace(title))
            return title;

        var name = moduleName;
        var slash = name.LastIndexOf('/');
        if (slash >= 0 && slash < name.Length - 1)
            name = name[(slash + 1)..];
        return name.Replace('-', ' ');
    }

    private void SaveCache(PluginCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var temp = CachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cache, JsonOptions));
            File.Move(temp, CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[plugins] 缓存写入失败：{ex.Message}");
        }
    }

    private static PluginCache? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
                return null;
            return JsonSerializer.Deserialize<PluginCache>(File.ReadAllText(CachePath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private sealed record PluginCache(DateTimeOffset CapturedAt, BridgePluginRow[] Plugins);
}