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
        var signature = SettingsSignature(definition.Settings);
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

        var session = await _rvm.CreateWorkspaceSessionAsync(
                workspaceCwd, string.Empty, string.Empty, definition.Settings)
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
        SetWorkspaceBadge(
            "RVM 就绪 · 工作区隔离",
            $"shell 在 AppContainer 令牌中执行，仅能读写工作区（{session.VirtualRoot.TrimEnd('\\')}）与专用 temp；宿主用户目录、系统 temp 与未授权工具目录被 ACL 拒绝。fs 工具只能解析工作区内的路径（插件策略）。");
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

    // 环境变量/工具目录变化也必须重建会话：PATH 与 ACL 授权在会话创建时固定。
    private static string SettingsSignature(RvmSettings settings) =>
        string.Join('\n', settings.Environment.Order(StringComparer.OrdinalIgnoreCase))
        + "\u0001"
        + string.Join('\n', settings.Tools.Order(StringComparer.OrdinalIgnoreCase));

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