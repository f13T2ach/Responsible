using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Responsible.Services;

namespace Responsible.ViewModel;

public partial class RvmEnvironmentItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public RvmEnvironmentItem(string name, bool available)
    {
        Name = name;
        Available = available;
    }

    public string Name { get; }
    public bool Available { get; }
    public string DisplayName => Available ? Name : $"{Name}（当前不存在）";
}

public sealed class RvmToolItem(string path)
{
    public string Path { get; } = path;
    public string Name => new DirectoryInfo(Path).Name;
}

// 选择器只保存名称/目录；环境变量的值在每次启动沙箱会话时由宿主读取。
public partial class RvmSettingsViewModel : ObservableObject
{
    private static readonly HashSet<string> ManagedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "TEMP", "TMP", "TMPDIR", "SYSTEMROOT", "WINDIR", "SYSTEMDRIVE",
        "COMSPEC", "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA",
        "OS", "PROCESSOR_ARCHITECTURE", "USERNAME", "NUMBER_OF_PROCESSORS", "PSMODULEPATH",
    };

    private bool _loading;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private bool _touched;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoTools))]
    private bool _hasTools;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolError))]
    private string _toolError = string.Empty;

    public bool HasToolError => !string.IsNullOrEmpty(ToolError);

    public bool HasNoTools => !HasTools;

    public bool Configured { get; private set; }

    public ObservableCollection<RvmEnvironmentItem> EnvironmentVariables { get; } = [];
    public ObservableCollection<RvmEnvironmentItem> VisibleEnvironmentVariables { get; } = [];
    public ObservableCollection<RvmToolItem> Tools { get; } = [];

    public int SelectedEnvironmentCount => EnvironmentVariables.Count(item => item.IsSelected);

    public void Load(IEnumerable<string> environmentNames, IEnumerable<string> toolPaths, bool configured)
    {
        _loading = true;
        try
        {
            Configured = configured;
            Touched = false;
            ToolError = string.Empty;
            Filter = string.Empty;
            EnvironmentVariables.Clear();
            VisibleEnvironmentVariables.Clear();
            Tools.Clear();

            var selected = new HashSet<string>(environmentNames, StringComparer.OrdinalIgnoreCase);
            var host = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is not string name || string.IsNullOrWhiteSpace(name)) continue;
                if (IsManaged(name)) continue;
                host[name] = true;
            }
            foreach (var name in selected)
            {
                if (!IsManaged(name)) host.TryAdd(name, false);
            }
            foreach (var name in host.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                var item = new RvmEnvironmentItem(name, host[name])
                {
                    IsSelected = selected.Contains(name),
                };
                item.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName != nameof(RvmEnvironmentItem.IsSelected)) return;
                    if (!_loading) Touched = true;
                    OnPropertyChanged(nameof(SelectedEnvironmentCount));
                };
                EnvironmentVariables.Add(item);
            }
            ApplyFilter();

            foreach (var path in toolPaths) AddToolCore(path, validate: false);
        }
        finally
        {
            _loading = false;
        }
    }

    public void Reset() => Load([], [], false);

    public void AddTool(string path) => AddToolCore(path, validate: true);

    private void AddToolCore(string path, bool validate)
    {
        ToolError = string.Empty;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            if (validate) ToolError = "工具目录路径无效。";
            return;
        }
        full = Path.TrimEndingDirectorySeparator(full);
        if (validate && !Directory.Exists(full))
        {
            ToolError = "工具目录不存在。";
            return;
        }
        if (string.Equals(full, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            if (validate) ToolError = "不能把整个盘符作为工具目录。";
            return;
        }
        if (Tools.Any(item => string.Equals(item.Path, full, StringComparison.OrdinalIgnoreCase))) return;
        Tools.Add(new RvmToolItem(full));
        HasTools = Tools.Count > 0;
        if (validate) Touched = true;
    }

    [RelayCommand]
    private void RemoveTool(RvmToolItem? item)
    {
        if (item is null) return;
        Tools.Remove(item);
        HasTools = Tools.Count > 0;
        Touched = true;
    }

    public RvmSettings Build()
    {
        var environment = EnvironmentVariables.Where(item => item.IsSelected).Select(item => item.Name).ToArray();
        var tools = Tools.Select(item => item.Path).ToArray();
        Configured = true;
        return new RvmSettings(environment, tools);
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        VisibleEnvironmentVariables.Clear();
        var filter = Filter.Trim();
        foreach (var item in EnvironmentVariables)
        {
            if (filter.Length == 0 || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                VisibleEnvironmentVariables.Add(item);
        }
    }

    private static bool IsManaged(string name) =>
        ManagedNames.Contains(name)
        || name.StartsWith("RESPONSIBLE_RVM_", StringComparison.OrdinalIgnoreCase)
        || name.Contains('=');
}