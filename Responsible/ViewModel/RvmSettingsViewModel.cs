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
    public bool IsPath => string.Equals(Name, "PATH", StringComparison.OrdinalIgnoreCase);

    public string DisplayName => IsPath
        ? "PATH（追加宿主 PATH 中的可访问目录）"
        : Available ? Name : $"{Name}（当前不存在）";
}

public partial class RvmToolOptionItem : ObservableObject
{
    public RvmToolOptionItem(string name, string path, bool available, bool isSelected)
    {
        Name = name;
        Path = path;
        Available = available;
        IsSelected = isSelected;
    }

    public string Name { get; }
    public string Path { get; }
    public bool Available { get; }
    public string DisplayName => Available ? Name : $"{Name}（未授权）";

    [ObservableProperty]
    private bool _isSelected;
}

// 选择器只保存名称/目录；环境变量的值在每次启动沙箱会话时由宿主读取。
public partial class RvmSettingsViewModel : ObservableObject
{
    private static readonly HashSet<string> ManagedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATHEXT", "TEMP", "TMP", "TMPDIR", "SYSTEMROOT", "WINDIR", "SYSTEMDRIVE",
        "COMSPEC", "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA",
        "OS", "PROCESSOR_ARCHITECTURE", "USERNAME", "NUMBER_OF_PROCESSORS", "PSMODULEPATH",
    };

    private readonly IAclAuthorizationService _acl;
    private bool _loading;

    public RvmSettingsViewModel(IAclAuthorizationService acl) => _acl = acl;

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
    public ObservableCollection<RvmToolOptionItem> ToolOptions { get; } = [];

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
            ToolOptions.Clear();

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

            LoadToolOptions(toolPaths);
        }
        finally
        {
            _loading = false;
        }
    }

    public void Reset() => Load([], [], false);

    private void LoadToolOptions(IEnumerable<string> selectedPaths)
    {
        var selected = new HashSet<string>(selectedPaths, StringComparer.OrdinalIgnoreCase);
        var authorized = _acl.Grants
            .Where(grant => grant.State == AclGrantState.Authorized)
            .GroupBy(grant => grant.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(grant => grant.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var grant in authorized)
        {
            var item = new RvmToolOptionItem(grant.Name, grant.Path, available: true,
                selected.Contains(grant.Path));
            item.PropertyChanged += OnToolOptionChanged;
            ToolOptions.Add(item);
        }

        var known = new HashSet<string>(authorized.Select(grant => grant.Path),
            StringComparer.OrdinalIgnoreCase);
        var missing = selectedPaths
            .Where(path => !known.Contains(path))
            .Select(path => Path.TrimEndingDirectorySeparator(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var path in missing)
        {
            var item = new RvmToolOptionItem(new DirectoryInfo(path).Name, path, available: false,
                isSelected: false);
            item.PropertyChanged += OnToolOptionChanged;
            ToolOptions.Add(item);
        }

        if (missing.Length > 0)
            ToolError = "部分工具目录已不在全局授权列表中，请到“设置 → 通用”重新授权或移除后再保存。";

        HasTools = ToolOptions.Count > 0;
    }

    private void OnToolOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(RvmToolOptionItem.IsSelected) && !_loading)
            Touched = true;
    }

    public RvmSettings Build()
    {
        var environment = EnvironmentVariables.Where(item => item.IsSelected).Select(item => item.Name).ToArray();
        var tools = ToolOptions
            .Where(item => item.Available && item.IsSelected)
            .Select(item => item.Path)
            .ToArray();
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