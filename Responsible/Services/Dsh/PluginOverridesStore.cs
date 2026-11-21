using System.IO;
using System.Text.Json;

namespace Responsible.Services;

public sealed record PluginOverride(bool Enabled, bool Applied);

// 插件启用/停用的宿主侧持久化：只记录用户显式改过的条目，内核启动时生成覆盖层 patch。
public sealed class PluginOverridesStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PluginOverride> _items;
    private readonly string _path;

    public PluginOverridesStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Responsible");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "plugin-overrides.json");
        _items = Load();
    }

    public PluginOverride? Find(string entryId)
    {
        lock (_gate)
            return _items.GetValueOrDefault(entryId);
    }

    public IReadOnlyList<(string EntryId, bool Enabled)> Snapshot()
    {
        lock (_gate)
            return _items.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (pair.Key, pair.Value.Enabled))
                .ToArray();
    }

    public void Save(string entryId, bool enabled, bool applied)
    {
        lock (_gate)
        {
            _items[entryId] = new PluginOverride(enabled, applied);
            Persist();
        }
    }

    public void SetApplied(string entryId, bool applied)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(entryId, out var current) || current.Applied == applied)
                return;
            _items[entryId] = current with { Applied = applied };
            Persist();
        }
    }

    private void Persist()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_items));
        File.Move(temp, _path, overwrite: true);
    }

    private Dictionary<string, PluginOverride> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return [];
            return JsonSerializer.Deserialize<Dictionary<string, PluginOverride>>(
                       File.ReadAllText(_path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                   ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}