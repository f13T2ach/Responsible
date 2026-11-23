using System.IO;
using System.Text.Json;

namespace Responsible.Services;

// 每轮的改动记录跟会话一起持久化：重启应用后，历史里那些“已编辑 n 个文件”不该消失。
// 语义对齐 DSH 的 turn-tail：一个 turn 只保留最新一次改动公告（workspace/changes 是覆盖语义），
// 所以同一 turn 的多条记录只留最后一条，不会在对话里长出两张一样的卡。
public sealed partial class ChatSessionService
{
    private static readonly JsonSerializerOptions ChangesJson = new(JsonSerializerDefaults.Web);

    private static string ChangesPath(string sessionId)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Responsible", "changes");
        Directory.CreateDirectory(directory);
        var safe = new string(sessionId.Where(char.IsLetterOrDigit).ToArray());
        return Path.Combine(directory, safe + ".jsonl");
    }

    private static void PersistChanges(string sessionId, ChatChanges changes)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        try
        {
            var path = ChangesPath(sessionId);
            // 同一 turn 覆盖而不是追加：DSH 的改动公告也是覆盖语义
            var kept = LoadPersistedChanges(sessionId)
                .Where(existing => existing.Turn != changes.Turn)
                .ToList();
            kept.Add(changes);
            File.WriteAllLines(path, kept.Select(item => JsonSerializer.Serialize(item, ChangesJson)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    private static IReadOnlyList<ChatChanges> LoadPersistedChanges(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return [];
        var path = ChangesPath(sessionId);
        if (!File.Exists(path)) return [];
        // turn 是 map 的键：同一个 turn 只留最后一条
        var byTurn = new Dictionary<int, ChatChanges>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parsed = JsonSerializer.Deserialize<ChatChanges>(line, ChangesJson);
                if (parsed is not null) byTurn[parsed.Turn] = parsed;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return byTurn.Values.OrderBy(item => item.Turn).ToArray();
        }
        return byTurn.Values.OrderBy(item => item.Turn).ToArray();
    }

    private static void DeletePersistedChanges(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        try
        {
            var path = ChangesPath(sessionId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

// 顶栏的“N 个工具正在运行”：只暴露只读快照，界面上不持有工具对象
public sealed partial class ChatSessionService
{
    public event Action<IReadOnlyList<RunningTool>>? RunningToolsChanged;

    public IReadOnlyList<RunningTool> RunningTools
    {
        get
        {
            lock (_gate) return _runningTools.ToArray();
        }
    }
}
