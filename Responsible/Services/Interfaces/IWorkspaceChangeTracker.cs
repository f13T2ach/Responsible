namespace Responsible.Services;

public sealed record ChangedFile(string Path, string Display, int Added, int Deleted, bool Binary, bool Oversized);

/// <summary>
/// 一轮里工作区的改动。<see cref="Diffs"/> 只收文本文件的 unified diff（二进制为 null 项），
/// 供界面点开某个文件时直接展示；不进 host.changes 的 wire 负载。
/// </summary>
public sealed record ChatChanges(int Turn, string Cwd, int Total, int Added, int Deleted,
    IReadOnlyList<ChangedFile> Files)
{
    public IReadOnlyDictionary<string, string> Diffs { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public interface IWorkspaceChangeTracker
{
    /// <summary>
    /// 记录本轮改动起点。<paramref name="useWorkspaceIndex"/> 为 true 时直接使用工作区仓库
    /// 自己的 index（RVM 会话盘专用副本），避免每次把整盘文件重新 hash。
    /// </summary>
    Task BeginAsync(string cwd, int turn, bool useWorkspaceIndex = false);
    Task<ChatChanges?> EndAsync();
}
