using System.IO;
using System.Text.Json.Serialization;
using Responsible.Localization;

namespace Responsible.Services;

// 用户在 RVM 设置里显式选择的环境变量名与工具目录。只持久化名称/路径，
// 环境变量的值在每次建会话时从宿主进程读取，避免把机密写进清单。
public sealed record RvmSettings(
    IReadOnlyList<string> Environment,
    IReadOnlyList<string> Tools)
{
    public static RvmSettings Empty { get; } = new([], []);
}

// 本地工作区定义：名称、主目录（新会话的 cwd）与可读写源文件夹。
// RvmProtected 只有在用户显式开启 RVM 隔离保护后才为 true。
public sealed record WorkspaceDefinition(
    string Id,
    string Name,
    string Directory,
    IReadOnlyList<string> Folders,
    bool RvmProtected = false,
    IReadOnlyList<string>? RvmEnvironment = null,
    IReadOnlyList<string>? RvmTools = null,
    bool RvmConfigured = false)
{
    [JsonIgnore]
    public string RvmKey => WorkspaceNames.RvmKey(Name, Id);

    [JsonIgnore]
    public IReadOnlyList<string> ExposedEnvironment => RvmEnvironment ?? [];

    [JsonIgnore]
    public IReadOnlyList<string> ExposedTools => RvmTools ?? [];

    [JsonIgnore]
    public RvmSettings Settings => new(ExposedEnvironment, ExposedTools);
}

// 工作区名同时用于界面与 RVM 存储目录（RVM/<名称>_<id>），因此在这里统一约束。
public static class WorkspaceNames
{
    public const int MaxLength = 32;
    public const string ReservedPrefix = ".rvm_";

    public static string? Validate(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "Enter a workspace name.";
        if (trimmed.Length > MaxLength) return $"A workspace name can contain at most {MaxLength} characters.";
        if (trimmed.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase))
            return "A workspace name cannot start with .rvm_ (reserved for RVM internal directories).";
        if (trimmed.StartsWith('.')) return "A workspace name cannot start with a dot.";
        if (trimmed.Any(ch => ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                             || char.IsControl(ch)))
            return "A workspace name cannot contain < > : \" / \\ | ? * or control characters.";
        if (trimmed.EndsWith('.')) return "A workspace name cannot end with a dot.";
        if (IsDeviceName(trimmed)) return "A workspace name cannot use a reserved system name such as CON, NUL, or COM1.";
        return null;
    }

    // 目录名的安全形式：非法字符换成下划线，截断后与工作区 id 前 6 位拼成稳定 key
    public static string RvmKey(string name, string id)
    {
        var trimmed = (name ?? string.Empty).Trim();
        var sanitized = new string(trimmed
            .Select(ch => ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                          || char.IsControl(ch) ? '_' : ch)
            .ToArray()).Trim().Trim('.');
        if (sanitized.Length == 0) sanitized = "workspace";
        if (sanitized.Length > 24) sanitized = sanitized[..24];
        var suffixSource = id ?? string.Empty;
        var suffix = suffixSource.Length >= 6 ? suffixSource[..6] : suffixSource.PadRight(6, '0');
        return $"{sanitized}_{suffix}";
    }

    public static string DisplayNameFor(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return Loc.T("L.Moe.UnnamedWorkspace");
        var trimmed = directory.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        if (name == "default-workspace") return Loc.T("L.Moe.DefaultWorkspace");
        return string.IsNullOrEmpty(name) ? directory : name;
    }

    private static bool IsDeviceName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL") return true;
        return stem.Length == 4
               && (stem.StartsWith("COM", StringComparison.Ordinal)
                   || stem.StartsWith("LPT", StringComparison.Ordinal))
               && stem[3] is >= '1' and <= '9';
    }
}

public interface IWorkspaceStore
{
    IReadOnlyList<WorkspaceDefinition> All { get; }

    void Add(WorkspaceDefinition workspace);

    void Remove(string id);
}
