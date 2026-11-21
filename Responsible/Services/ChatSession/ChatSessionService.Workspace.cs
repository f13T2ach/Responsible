using System.IO;

namespace Responsible.Services;

// 工作区叠加盘生命周期：DSH 内核启动时按当前工作区准备 RVM 会话并挂载盘符。
// shell 工具经 RvmEngine --shell-bridge 转入该会话执行；fs 工具尚未接管，仍直连宿主。
public sealed partial class ChatSessionService
{
    private readonly SemaphoreSlim _rvmWorkspaceGate = new(1, 1);

    public bool IsRvmProtected => WorkspaceForCwd(CurrentCwd())?.RvmProtected == true;

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
        if (_rvmSessionId != 0 && _rvmVirtualRoot is not null
            && string.Equals(_rvmWorkspaceRoot, workspaceCwd, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_rvmKey, key, StringComparison.Ordinal))
            return;

        await CloseRvmSessionAsync().ConfigureAwait(false);

        var probe = _winFsp.Probe();
        if (!probe.Installed)
        {
            SetWorkspaceBadge("RVM 不可用", "未安装 WinFsp：shell 退回 DSH 沙箱，工作区未隔离。");
            return;
        }

        if (!await _rvm.EnsureRunningAsync().ConfigureAwait(false))
        {
            SetWorkspaceBadge("RVM 不可用", _rvm.Status);
            return;
        }

        var session = await _rvm.CreateWorkspaceSessionAsync(workspaceCwd, _winFsp.DriveLetter, key)
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
        SetWorkspaceBadge(
            $"RVM 就绪 · {session.VirtualRoot.TrimEnd('\\')}",
            $"工作区叠加盘已挂载：{session.VirtualRoot} ← {workspaceCwd}。存储目录：%LOCALAPPDATA%\\Responsible\\RVM\\{key}；shell 经 RVM 会话执行，fs 读写工具仍直接访问宿主目录。");
    }

    private async Task CloseRvmSessionAsync()
    {
        var sessionId = _rvmSessionId;
        _rvmSessionId = 0;
        _rvmWorkspaceRoot = null;
        _rvmVirtualRoot = null;
        _rvmKey = null;
        if (sessionId != 0)
            await _rvm.CloseSessionAsync(sessionId).ConfigureAwait(false);
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