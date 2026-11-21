namespace Responsible.Services;

public sealed record ChangedFile(string Path, string Display, int Added, int Deleted, bool Binary, bool Oversized);

public sealed record ChatChanges(int Turn, string Cwd, int Total, int Added, int Deleted, IReadOnlyList<ChangedFile> Files);

public interface IWorkspaceChangeTracker
{
    Task BeginAsync(string cwd, int turn);
    Task<ChatChanges?> EndAsync();
}
