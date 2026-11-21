using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Responsible.Services;

namespace Responsible.ViewModel;

public sealed class FolderItemViewModel(string path)
{
    public string Path { get; } = path;

    public string Name => new DirectoryInfo(Path).Name;
}

public partial class WorkspaceEditorViewModel : ObservableObject
{
    public WorkspaceEditorViewModel(IAclAuthorizationService acl) => RvmSettings = new(acl);

    public IReadOnlyList<string> LocationOptions { get; } = ["此电脑"];

    [ObservableProperty]
    private string _selectedLocation = "此电脑";

    [ObservableProperty]
    private string _name = string.Empty;

    // 默认关闭：只有显式勾选才为这个工作区创建 RVM 沙箱会话
    [ObservableProperty]
    private bool _rvmProtection;

    [ObservableProperty]
    private bool _rvmSettingsOpen;

    [ObservableProperty]
    private bool _isRvmSkipWarningVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public RvmSettingsViewModel RvmSettings { get; }

    partial void OnRvmProtectionChanged(bool value)
    {
        if (value) return;
        RvmSettingsOpen = false;
        IsRvmSkipWarningVisible = false;
    }

    partial void OnRvmSettingsOpenChanged(bool value)
    {
        if (!value) return;
        RvmSettings.Touched = true;
        IsRvmSkipWarningVisible = false;
    }

    partial void OnNameChanged(string value)
    {
        if (!string.IsNullOrEmpty(ErrorText)) ErrorText = string.Empty;
    }

    public ObservableCollection<FolderItemViewModel> Folders { get; } = [];

    public event EventHandler? SaveRequested;

    public event EventHandler? CancelRequested;

    // 视图的文件夹选择器把结果交回这里：去重、首个文件夹自动给出名称
    public void AddFolder(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }
        if (!Directory.Exists(full)) return;
        if (Folders.Any(item => string.Equals(item.Path, full, StringComparison.OrdinalIgnoreCase))) return;
        Folders.Add(new FolderItemViewModel(full));
        if (string.IsNullOrWhiteSpace(Name))
            Name = new DirectoryInfo(full).Name;
        ErrorText = string.Empty;
    }

    [RelayCommand]
    private void RemoveFolder(FolderItemViewModel? item)
    {
        if (item is null) return;
        Folders.Remove(item);
    }

    [RelayCommand]
    private void Save()
    {
        if (Folders.Count == 0)
        {
            ErrorText = "请至少添加一个文件夹。";
            return;
        }
        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorText = "请输入工作区名称。";
            return;
        }
        if (WorkspaceNames.Validate(Name) is { } invalid)
        {
            ErrorText = invalid;
            return;
        }
        // 开着隔离但从没打开设置：先提醒 AI 可能用不了本机工具。
        if (RvmProtection && !RvmSettings.Touched && !RvmSettings.Configured)
        {
            IsRvmSkipWarningVisible = true;
            return;
        }
        ErrorText = string.Empty;
        IsRvmSkipWarningVisible = false;
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ContinueWithoutRvmSettings()
    {
        IsRvmSkipWarningVisible = false;
        ErrorText = string.Empty;
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void DismissRvmWarning() => IsRvmSkipWarningVisible = false;

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke(this, EventArgs.Empty);

    public WorkspaceDefinition Build()
    {
        var folders = Folders.Select(item => item.Path).ToArray();
        var settings = RvmProtection ? RvmSettings.Build() : Responsible.Services.RvmSettings.Empty;
        return new WorkspaceDefinition(
            Guid.NewGuid().ToString("n"), Name.Trim(), folders[0], folders, RvmProtection,
            settings.Environment, settings.Tools, RvmProtection && RvmSettings.Touched);
    }

    public void Reset()
    {
        Folders.Clear();
        Name = string.Empty;
        RvmProtection = false;
        RvmSettingsOpen = false;
        IsRvmSkipWarningVisible = false;
        RvmSettings.Reset();
        ErrorText = string.Empty;
        SelectedLocation = LocationOptions[0];
    }
}
