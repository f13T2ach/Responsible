using System.IO;
using System.Text.Json;

namespace Responsible.Services;

// 工作区是宿主侧概念（DSH 只持久化会话及其 cwd），因此单独存一份本地清单
public sealed class WorkspaceStore : IWorkspaceStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Responsible", "workspaces.json");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly List<WorkspaceDefinition> _items = Load();

    public IReadOnlyList<WorkspaceDefinition> All
    {
        get { lock (_gate) return _items.ToArray(); }
    }

    public void Add(WorkspaceDefinition workspace)
    {
        lock (_gate)
        {
            _items.RemoveAll(item => item.Id == workspace.Id);
            _items.Add(workspace);
            SaveLocked();
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_items.RemoveAll(item => item.Id == id) == 0) return;
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_items, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static List<WorkspaceDefinition> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return [];
            var rows = JsonSerializer.Deserialize<List<WorkspaceRow>>(File.ReadAllText(StorePath), JsonOptions);
            return (rows ?? [])
                .Where(row => !string.IsNullOrWhiteSpace(row.Id)
                              && !string.IsNullOrWhiteSpace(row.Name)
                              && !string.IsNullOrWhiteSpace(row.Directory))
                .Select(row => new WorkspaceDefinition(
                    row.Id!,
                    row.Name!,
                    row.Directory!,
                    (row.Folders ?? [])
                        .Where(folder => !string.IsNullOrWhiteSpace(folder))
                        .Select(folder => folder!)
                        .ToArray(),
                    row.RvmProtected,
                    Clean(row.RvmEnvironment),
                    Clean(row.RvmTools),
                    row.RvmConfigured))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // 旧清单没有 RVM 设置字段：缺省即“从未配置”，首次开启保护时会提示选择。
    private static IReadOnlyList<string> Clean(IReadOnlyList<string?>? values) =>
        (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();

    private sealed record WorkspaceRow(
        string? Id,
        string? Name,
        string? Directory,
        IReadOnlyList<string?>? Folders,
        bool RvmProtected = false,
        IReadOnlyList<string?>? RvmEnvironment = null,
        IReadOnlyList<string?>? RvmTools = null,
        bool RvmConfigured = false);
}
