using System.IO;
using Responsible.Localization;

namespace Responsible.Services;

// RVM 会话生命周期：DSH 内核启动时按当前工作区准备 AppContainer 会话。
// shell 工具经 RvmEngine --shell-bridge 转入该会话执行；fs 工具由 rvm-sandbox
// 插件把解析目标限制在工作区内。
public sealed partial class ChatSessionService
{
    private readonly SemaphoreSlim _rvmWorkspaceGate = new(1, 1);
    private string? _rvmSettingsSignature;

    public bool IsRvmProtected => WorkspaceForCwd(CurrentCwd())?.RvmProtected == true;

    public bool HasActiveRvmSession => _rvmSessionId != 0;

    public string? ActiveRvmRoot => _rvmVirtualRoot;

    public WorkspaceDefinition? CurrentWorkspace => WorkspaceForCwd(CurrentCwd());

    public Task<RvmWorkspaceDiff?> DiffRvmWorkspaceAsync(CancellationToken cancellationToken = default) =>
        _rvmSessionId == 0
            ? Task.FromResult<RvmWorkspaceDiff?>(null)
            : _rvm.DiffWorkspaceAsync(_rvmSessionId, cancellationToken);

    public Task<RvmWorkspaceApplyResult?> SyncRvmWorkspaceAsync(
        bool all,
        IReadOnlyList<string>? paths = null,
        CancellationToken cancellationToken = default) =>
        _rvmSessionId == 0
            ? Task.FromResult<RvmWorkspaceApplyResult?>(null)
            : _rvm.SyncWorkspaceAsync(_rvmSessionId, all, paths, cancellationToken);

    public Task<bool?> RollbackRvmWorkspaceAsync(CancellationToken cancellationToken = default) =>
        _rvmSessionId == 0
            ? Task.FromResult<bool?>(null)
            : _rvm.RollbackWorkspaceAsync(_rvmSessionId, cancellationToken);

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

        // 会话盘的范围来自 Git 索引；没有索引就无法确定哪些文件进入快照，
        // 这里直接要求用户先完成索引，而不是退回扫描整个目录。
        var index = await RvmWorkspaceIndex.InspectAsync(workspaceCwd).ConfigureAwait(false);
        if (!index.IsReady)
        {
            await CloseRvmSessionAsync().ConfigureAwait(false);
            SetWorkspaceBadge(Loc.T("L.Session.NeedsGitIndex"),
                index.State == RvmIndexState.GitUnavailable
                    ? $"{index.Message} Install Git, then create an index while editing the workspace."
                    : $"{index.Message} Open the workspace editor, enable RVM isolation, run Full index or Standard index, then reopen the conversation.");
            return;
        }

        var key = definition.RvmKey;
        var selectedTools = ResolveSelectedTools(definition.Settings, out var toolError);
        if (selectedTools is null)
        {
            SetWorkspaceBadge(Loc.T("L.Session.ToolsNotGranted"), toolError);
            return;
        }

        var policy = _rvmPolicy.Current;
        var signature = SettingsSignature(definition.Settings, selectedTools)
                        + "\u0003" + policy.NetworkEnabled
                        + "\u0004" + string.Join('\n', policy.Devices.Order(StringComparer.OrdinalIgnoreCase));
        if (_rvmSessionId != 0 && _rvmVirtualRoot is not null
            && string.Equals(_rvmWorkspaceRoot, workspaceCwd, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_rvmKey, key, StringComparison.Ordinal)
            && string.Equals(_rvmSettingsSignature, signature, StringComparison.Ordinal))
            return;

        await CloseRvmSessionAsync().ConfigureAwait(false);

        if (!await _rvm.EnsureRunningAsync().ConfigureAwait(false))
        {
            SetWorkspaceBadge(Loc.T("L.Session.RvmUnavailable"), _rvm.Status);
            return;
        }

        var allTools = _acl.Grants
            .Where(grant => grant.State == AclGrantState.Authorized && grant.IsValid)
            .Select(grant => new RvmToolAuthorization(grant.Path, grant.Name, grant.CapabilitySid))
            .ToArray();
        if (!await _rvm.SetToolAuthorizationsAsync(allTools).ConfigureAwait(false))
        {
            SetWorkspaceBadge(Loc.T("L.Session.RvmUnavailable"), _rvm.Status);
            return;
        }

        var session = await _rvm.CreateWorkspaceSessionAsync(
                workspaceCwd, _winFsp.DriveLetter, key, definition.Settings, selectedTools,
                policy.NetworkEnabled, policy.DevicesEnabled ? policy.Devices : [])
            .ConfigureAwait(false);
        if (session is null)
        {
            SetWorkspaceBadge(Loc.T("L.Session.RvmUnavailable"), _rvm.Status);
            return;
        }

        _rvmSessionId = session.SessionId;
        _rvmWorkspaceRoot = workspaceCwd;
        _rvmVirtualRoot = session.VirtualRoot;
        _rvmKey = key;
        _rvmSettingsSignature = signature;
        var toolsNote = selectedTools.Count == 0
            ? Loc.T("L.Session.NoToolsNote")
            : Loc.F("L.Session.ToolsNote", string.Join(Loc.IsChinese ? "、" : ", ", selectedTools.Select(tool => tool.Name)));
        var networkNote = policy.NetworkEnabled
            ? Loc.T("L.Session.NetworkOpenNote")
            : Loc.T("L.Session.NetworkClosedNote");
        var deviceNote = policy.DevicesEnabled && policy.Devices.Count > 0
            ? Loc.F("L.Session.DevicesNote", string.Join(Loc.IsChinese ? "、" : ", ", policy.Devices))
            : Loc.T("L.Session.NoDevicesNote");
        SetWorkspaceBadge(
            Loc.T("L.Session.RvmReady"),
            Loc.F("L.Session.RvmReadyDetail", session.VirtualRoot.TrimEnd('\\'), workspaceCwd, toolsNote, networkNote, deviceNote));
    }

    private IReadOnlyList<RvmToolAuthorization>? ResolveSelectedTools(RvmSettings settings,
        out string? error)
    {
        error = null;
        var authorized = _acl.Grants
            .Where(grant => grant.State == AclGrantState.Authorized && grant.IsValid)
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
                error = $"Invalid tool directory path: {path}";
                return null;
            }
            if (!authorized.TryGetValue(full, out var grant))
            {
                error = $"Tool directory \"{full}\" has no valid global ACL grant. Add or regrant it in Settings → RVM → ACL grants, or clear it in the workspace RVM settings.";
                return null;
            }
            if (!Directory.Exists(full))
            {
                error = $"The granted tool directory does not exist: {full}";
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
            .Where(grant => grant.State == AclGrantState.Authorized && grant.IsValid
                            && selected.Contains(grant.Path))
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