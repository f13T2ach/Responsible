namespace Responsible.Services;

// 权限（沙箱模式 + 审批策略）全部镜像 DSH 的 permission-presets：目录来自进程级 catalog，
// 当前值来自会话的 permissions 投影，切换走插件桥接到 permissionPresets.set 的同一写入路径。
public sealed partial class ChatSessionService
{
    // 下拉菜单中的合成项：不是 DSH preset，而是当前工作区的 RVM 隔离保护开关
    public const string RvmPresetValue = "rvm";

    private DshPermissionCatalog? _permissionCatalog;
    private string? _permissionValue;

    public DshPermissionCatalog? PermissionCatalog
    {
        get { lock (_gate) return _permissionCatalog; }
    }

    public string? PermissionValue
    {
        get { lock (_gate) return _permissionValue; }
    }

    public event Action? PermissionsChanged;

    // 目录是进程级的：桥接刚挂载时短暂重试，内核重启后重新读取
    private async Task LoadPermissionCatalogAsync()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var catalog = await _kernel.GetPermissionCatalogAsync().ConfigureAwait(false);
            if (catalog is { Options.Count: > 0 })
            {
                lock (_gate) _permissionCatalog = catalog;
                PermissionsChanged?.Invoke();
                return;
            }
            if (!_kernel.IsAvailable) return;
            await Task.Delay(800).ConfigureAwait(false);
        }
    }

    private async Task RefreshPermissionAsync(string sessionId)
    {
        var selection = await _kernel.GetPermissionAsync(sessionId).ConfigureAwait(false);
        if (selection is null) return;
        ApplyPermissionValue(sessionId, selection.CurrentValue);
    }

    // 会话刚在 prompt 时建立：投影已由 permission-presets 播种，补拉一次保证首条消息后就有等级
    private async Task RefreshPermissionIfMissingAsync(string sessionId)
    {
        lock (_gate)
        {
            if (sessionId != _sessionId || _permissionValue is not null) return;
        }
        await RefreshPermissionAsync(sessionId).ConfigureAwait(false);
    }

    private void ApplyPermissionValue(string sessionId, string? value)
    {
        lock (_gate)
        {
            if (sessionId != _sessionId || _permissionValue == value) return;
            _permissionValue = value;
        }
        PermissionsChanged?.Invoke();
    }

    // 新建会话与内核下线时清空当前值；目录随内核进程失效时一并清空
    private void ResetPermissions(bool keepCatalog)
    {
        lock (_gate)
        {
            if (!keepCatalog) _permissionCatalog = null;
            _permissionValue = null;
        }
        PermissionsChanged?.Invoke();
    }

    private void OnKernelPermission(string sessionId, string value) => ApplyPermissionValue(sessionId, value);

    public async Task<PermissionResult> SelectPermissionAsync(string preset)
    {
        if (string.IsNullOrWhiteSpace(preset)) return new PermissionResult(false, "未知的权限等级。");
        if (preset == RvmPresetValue) return await SetRvmProtectionAsync(true).ConfigureAwait(false);

        // 选中其它审批等级时关闭 RVM 隔离保护；切换会重启内核，因此以重启后的会话为准
        if (IsRvmProtected)
        {
            var off = await SetRvmProtectionAsync(false).ConfigureAwait(false);
            if (!off.Ok) return off;
        }

        string sessionId;
        bool persisted;
        lock (_gate)
        {
            sessionId = _sessionId;
            persisted = _sessionPersisted;
        }
        if (!persisted)
            return new PermissionResult(false, "会话尚未建立，发出第一条消息后再切换权限。");

        var value = await _kernel.SetPermissionPresetAsync(sessionId, preset).ConfigureAwait(false);
        if (value is null)
            return new PermissionResult(false,
                string.IsNullOrWhiteSpace(_kernel.Status) ? "切换权限失败。" : _kernel.Status);
        ApplyPermissionValue(sessionId, value);
        return new PermissionResult(true, string.Empty);
    }

    // 开启当前工作区的 RVM 隔离保护；settings 为 null 表示沿用已保存的选择。
    public Task<PermissionResult> EnableRvmProtectionAsync(RvmSettings? settings) =>
        SetRvmProtectionAsync(true, settings);

    // 修改已保存的 RVM 设置；若该工作区正受保护，重启内核让新 PATH/ACL 生效。
    public async Task<PermissionResult> UpdateRvmSettingsAsync(string workspaceId, RvmSettings settings)
    {
        lock (_gate)
        {
            if (_kernelAssistantId is not null)
                return new PermissionResult(false, "当前回复尚未结束，无法修改 RVM 设置。");
        }

        var previous = _workspaceStore.All.FirstOrDefault(item => item.Id == workspaceId);
        if (previous is null) return new PermissionResult(false, "找不到该工作区。");

        var updated = previous with
        {
            RvmEnvironment = settings.Environment,
            RvmTools = settings.Tools,
            RvmConfigured = true,
        };
        _workspaceStore.Add(updated);
        if (!updated.RvmProtected || WorkspaceForCwd(CurrentCwd())?.Id != workspaceId)
        {
            PermissionsChanged?.Invoke();
            return new PermissionResult(true, string.Empty);
        }

        ResetPermissions(keepCatalog: true);
        var restart = await RestartKernelAsync().ConfigureAwait(false);
        if (!restart.Restarted)
        {
            _workspaceStore.Add(previous);
            PermissionsChanged?.Invoke();
            return new PermissionResult(false, $"保存 RVM 设置失败：{restart.Message}");
        }
        PermissionsChanged?.Invoke();
        return new PermissionResult(true, string.Empty);
    }

    // 开启/关闭当前工作区的 RVM 隔离保护，持久化到工作区清单并重启内核以重建绑定。
    private async Task<PermissionResult> SetRvmProtectionAsync(bool enabled, RvmSettings? settings = null)
    {
        lock (_gate)
        {
            if (_kernelAssistantId is not null)
                return new PermissionResult(false, "当前回复尚未结束，无法切换 RVM 隔离保护。");
        }

        var cwd = CurrentCwd();
        var definition = WorkspaceForCwd(cwd);
        if (definition is null)
        {
            if (!enabled) return new PermissionResult(true, string.Empty);
            var name = WorkspaceNames.DisplayNameFor(cwd);
            if (WorkspaceNames.Validate(name) is not null) name = "工作区";
            definition = new WorkspaceDefinition(
                Guid.NewGuid().ToString("n"), name, cwd, [cwd], false);
        }

        if (definition.RvmProtected == enabled && settings is null)
        {
            PermissionsChanged?.Invoke();
            return new PermissionResult(true, string.Empty);
        }

        var previous = definition;
        if (settings is not null)
        {
            definition = definition with
            {
                RvmEnvironment = settings.Environment,
                RvmTools = settings.Tools,
                RvmConfigured = true,
            };
        }

        _workspaceStore.Add(definition with { RvmProtected = enabled });
        ResetPermissions(keepCatalog: true);
        var restart = await RestartKernelAsync().ConfigureAwait(false);
        if (!restart.Restarted)
        {
            // 启动失败时恢复原状态，避免清单与实际防护不一致
            _workspaceStore.Add(previous);
            PermissionsChanged?.Invoke();
            return new PermissionResult(false, $"切换 RVM 隔离保护失败：{restart.Message}");
        }

        PermissionsChanged?.Invoke();
        return new PermissionResult(true, string.Empty);
    }
}
