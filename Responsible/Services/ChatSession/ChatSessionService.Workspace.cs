using System.IO;

namespace Responsible.Services;

// RVM 会话生命周期：DSH 内核启动时按当前工作区准备 AppContainer 会话。
// shell 工具经 RvmEngine --shell-bridge 转入该会话执行；fs 工具由 rvm-sandbox
// 插件把解析目标限制在工作区内。
public sealed partial class ChatSessionService
{
    private readonly SemaphoreSlim _rvmWorkspaceGate = new(1, 1);
    private string? _rvmSettingsSignature;

    public bool IsRvmProtected => WorkspaceForCwd(CurrentCwd())?.RvmProtected == true;

    public WorkspaceDefinition? CurrentWorkspace => WorkspaceForCwd(CurrentCwd());

    // 工作区按文件夹归属：会话 cwd 等于或位于某个源文件夹内即视为该工作区
    private WorkspaceDefinition? WorkspaceForCwd(string cwd)
    {
        string full;
        try
        {
            full = Path.GetFullPath(cwd).TrimEnd('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return _workspaceStore.All
            .Where(definition => definition.Folders.Any(folder => IsWithin(folder, full)))
            .OrderByDescending(definition => definition.Folders.Max(folder => folder.Length))
            .FirstOrDefault();
    }

    private static bool IsWithin(string root, string path)
    {
        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        return string.Equals(fullRoot, path, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureRvmWorkspaceAsync(string workspaceCwd)
    {
        await _rvmWorkspaceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureRvmWorkspaceCoreAsync(workspaceCwd).ConfigureAwait(false);
        }
        finally
        {
            _rvmWorkspaceGate.Release();
        }
    }

    private async Task EnsureRvmWorkspaceCoreAsync(string workspaceCwd)
    {
        var definition = WorkspaceForCwd(workspaceCwd);
        if (definition is null || !definition.RvmProtected)
        {
            await CloseRvmSessionAsync().ConfigureAwait(false);
            SetWorkspaceBadge(string.Empty, null);
            return;
        }

        var key = definition.RvmKey;
        var selectedTools = ResolveSelectedTools(definition.Settings, out var toolError);
        if (selectedTools is null)
        {
            SetWorkspaceBadge("工具未授权", toolError);
            return;
        }

        var signature = SettingsSignature(definition.Settings, selectedTools);
        if (_rvmSessionId != 0 && _rvmVirtualRoot is not null
            && string.Equals(_rvmWorkspaceRoot, workspaceCwd, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_rvmKey, key, StringComparison.Ordinal)
            && string.Equals(_rvmSettingsSignature, signature, StringComparison.Ordinal))
            return;

        await CloseRvmSessionAsync().ConfigureAwait(false);

        if (!await _rvm.EnsureRunningAsync().ConfigureAwait(false))
        {
            SetWorkspaceBadge("RVM 不可用", _rvm.Status);
            return;
        }

        var allTools = _acl.Grants
            .Where(grant => grant.State == AclGrantState.Authorized)
            .Select(grant => new RvmToolAuthorization(grant.Path, grant.Name, grant.CapabilitySid))
            .ToArray();
        if (!await _rvm.SetToolAuthorizationsAsync(allTools).ConfigureAwait(false))
        {
            SetWorkspaceBadge("RVM 不可用", _rvm.Status);
            return;
        }

        var session = await _rvm.CreateWorkspaceSessionAsync(
                workspaceCwd, string.Empty, string.Empty, definition.Settings, selectedTools)
            .ConfigureAwait(false);
        if (session is null)
        {
            SetWorkspaceBadge("RVM 不可用", _rvm.Status);
            return;
        }

        _rvmSessionId = session.SessionId;
        _rvmWorkspaceRoot = workspaceCwd;
        _rvmVirtualRoot = session.VirtualRoot;
        _rvmKey = key;
        _rvmSettingsSignature = signature;
        var toolsNote = selectedTools.Count == 0
            ? "本次未启用额外工具。"
            : $"本次启用工具：{string.Join("、", selectedTools.Select(tool => tool.Name))}。";
        SetWorkspaceBadge(
            "RVM 就绪 · 工作区隔离",
            $"shell 在 AppContainer 令牌中执行，仅能读写工作区（{session.VirtualRoot.TrimEnd('\\')}）与专用 temp；宿主用户目录、系统 temp 与未授权工具目录被 ACL 拒绝。{toolsNote}fs 工具只能解析工作区内的路径（插件策略）。");
    }

    private IReadOnlyList<RvmToolAuthorization>? ResolveSelectedTools(RvmSettings settings,
        out string? error)
    {
        error = null;
        var authorized = _acl.Grants
            .Where(grant => grant.State == AclGrantState.Authorized)
            .GroupBy(grant => grant.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var selected = new List<RvmToolAuthorization>();
        foreach (var path in settings.Tools)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                           or PathTooLongException)
            {
                error = $"工具目录路径无效：{path}";
                return null;
            }
            if (!authorized.TryGetValue(full, out var grant))
            {
                error = $"工具目录“{full}”没有全局 ACL 授权。请到“设置 → 通用 → ACL 授权管理”添加或重新授权，或在工作区 RVM 设置中取消勾选。";
                return null;
            }
            if (!Directory.Exists(full))
            {
                error = $"已授权的工具目录不存在：{full}";
                return null;
            }
            selected.Add(new RvmToolAuthorization(grant.Path, grant.Name, grant.CapabilitySid));
        }
        return selected;
    }

    private async Task CloseRvmSessionAsync()
    {
        var sessionId = _rvmSessionId;
        _rvmSessionId = 0;
        _rvmWorkspaceRoot = null;
        _rvmVirtualRoot = null;
        _rvmKey = null;
        _rvmSettingsSignature = null;
        if (sessionId != 0)
            await _rvm.CloseSessionAsync(sessionId).ConfigureAwait(false);
    }

    // 环境变量/工具目录/授权时间变化都必须重建会话：PATH 与 capability 在创建时固化。
    private string SettingsSignature(RvmSettings settings,
        IReadOnlyList<RvmToolAuthorization> selectedTools)
    {
        var selected = new HashSet<string>(selectedTools.Select(tool => tool.Path),
            StringComparer.OrdinalIgnoreCase);
        var stamps = _acl.Grants
            .Where(grant => grant.State == AclGrantState.Authorized && selected.Contains(grant.Path))
            .OrderBy(grant => grant.Path, StringComparer.OrdinalIgnoreCase)
            .Select(grant => string.Join('|', grant.Path, grant.CapabilitySid,
                grant.AuthorizedAtUnixMs));
        return string.Join('\n', settings.Environment.Order(StringComparer.OrdinalIgnoreCase))
               + "\u0001"
               + string.Join('\n', selectedTools.Select(tool => tool.Path)
                   .Order(StringComparer.OrdinalIgnoreCase))
               + "\u0002"
               + string.Join('\n', stamps);
    }

    private async Task DisableRvmWorkspaceAsync()
    {
        var wasActive = _rvmSessionId != 0 || !string.IsNullOrEmpty(_workspaceBadge);
        await CloseRvmSessionAsync().ConfigureAwait(false);
        if (wasActive) SetWorkspaceBadge(string.Empty, null);
    }

    private void SetWorkspaceBadge(string badge, string? detail)
    {
        if (_workspaceBadge == badge && _workspaceBadgeDetail == detail) return;
        _workspaceBadge = badge;
        _workspaceBadgeDetail = detail;
        WorkspaceBadgeChanged?.Invoke();
    }
}