using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Responsible.Localization;
using Responsible.Services;

namespace Responsible.ViewModel;

public sealed class FolderItemViewModel(string path)
{
    public string Path { get; } = path;

    public string Name => new DirectoryInfo(Path).Name;
}

public partial class WorkspaceEditorViewModel : ObservableObject
{
    public WorkspaceEditorViewModel(IAclAuthorizationService acl)
    {
        RvmSettings = new(acl);
        LocationOptions = [Loc.T("L.Workspace.ThisPc")];
        _selectedLocation = LocationOptions[0];
    }

    public IReadOnlyList<string> LocationOptions { get; }

    [ObservableProperty]
    private string _selectedLocation;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGitIndexIdle))]
    private bool _isGitIndexBusy;

    [ObservableProperty]
    private bool _isGitIndexNeeded;

    [ObservableProperty]
    private bool _isGitIndexReady;

    [ObservableProperty]
    private string _gitIndexStatus = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool IsGitIndexIdle => !IsGitIndexBusy;

    public RvmSettingsViewModel RvmSettings { get; }

    partial void OnRvmProtectionChanged(bool value)
    {
        if (!value)
        {
            RvmSettingsOpen = false;
            IsRvmSkipWarningVisible = false;
            ResetGitIndexState();
            return;
        }
        if (Folders.Count > 0) _ = RefreshGitIndexAsync();
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
        if (RvmProtection) _ = RefreshGitIndexAsync();
    }

    [RelayCommand]
    private void RemoveFolder(FolderItemViewModel? item)
    {
        if (item is null) return;
        Folders.Remove(item);
        if (RvmProtection) _ = RefreshGitIndexAsync();
    }

    [RelayCommand]
    private async Task Save()
    {
        if (Folders.Count == 0)
        {
            ErrorText = "Add at least one folder.";
            return;
        }
        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorText = "Enter a workspace name.";
            return;
        }
        if (WorkspaceNames.Validate(Name) is { } invalid)
        {
            ErrorText = invalid;
            return;
        }
        if (!await EnsureGitIndexAsync().ConfigureAwait(true)) return;
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
    private async Task ContinueWithoutRvmSettings()
    {
        if (!await EnsureGitIndexAsync().ConfigureAwait(true)) return;
        IsRvmSkipWarningVisible = false;
        ErrorText = string.Empty;
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private Task InitializeGitIndexAsync() => RunIndexAsync(comprehensive: false);

    [RelayCommand]
    private Task InitializeFullGitIndexAsync() => RunIndexAsync(comprehensive: true);

    private async Task<bool> EnsureGitIndexAsync()
    {
        if (!RvmProtection || Folders.Count == 0) return true;
        if (IsGitIndexBusy)
        {
            ErrorText = "A Git index operation is already running. Wait for it to finish.";
            return false;
        }
        IsGitIndexBusy = true;
        try
        {
            var status = await RvmWorkspaceIndex.InspectAsync(Folders[0].Path).ConfigureAwait(true);
            ApplyGitIndexStatus(status);
            if (status.IsReady) return true;
            ErrorText = status.RequiresIndexing
                ? "RVM requires a Git index for this workspace. Use \"Full index\" to index the entire workspace (recommended for office folders without code), or create a standard Git index first."
                : status.Message;
            return false;
        }
        finally
        {
            IsGitIndexBusy = false;
        }
    }

    private async Task RunIndexAsync(bool comprehensive)
    {
        if (Folders.Count == 0 || IsGitIndexBusy) return;
        IsGitIndexBusy = true;
        GitIndexStatus = comprehensive ? Loc.T("L.GitStatus.BuildingFull") : Loc.T("L.GitStatus.Building");
        try
        {
            var status = await RvmWorkspaceIndex.InitializeAsync(Folders[0].Path, comprehensive)
                .ConfigureAwait(true);
            ApplyGitIndexStatus(status);
            if (status.IsReady)
            {
                if (ErrorText.Contains("Git index", StringComparison.Ordinal)) ErrorText = string.Empty;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            GitIndexStatus = $"Failed to build the Git index: {ex.Message}";
            IsGitIndexNeeded = true;
            IsGitIndexReady = false;
        }
        finally
        {
            IsGitIndexBusy = false;
        }
    }

    private async Task RefreshGitIndexAsync()
    {
        if (!RvmProtection || Folders.Count == 0 || IsGitIndexBusy) return;
        IsGitIndexBusy = true;
        GitIndexStatus = Loc.T("L.GitStatus.Checking");
        try
        {
            var status = await RvmWorkspaceIndex.InspectAsync(Folders[0].Path).ConfigureAwait(true);
            ApplyGitIndexStatus(status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            GitIndexStatus = $"Failed to check the Git index: {ex.Message}";
            IsGitIndexNeeded = true;
            IsGitIndexReady = false;
        }
        finally
        {
            IsGitIndexBusy = false;
        }
    }

    private void ApplyGitIndexStatus(RvmIndexStatus status)
    {
        IsGitIndexReady = status.IsReady;
        IsGitIndexNeeded = !status.IsReady;
        GitIndexStatus = status.IsReady
            ? Loc.F("L.GitStatus.Ready", status.IndexedFiles)
            : status.RequiresIndexing
                ? Loc.T("L.GitStatus.NeedsIndex")
                : status.Message;
    }

    private void ResetGitIndexState()
    {
        IsGitIndexNeeded = false;
        IsGitIndexReady = false;
        GitIndexStatus = string.Empty;
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
        ResetGitIndexState();
        ErrorText = string.Empty;
        SelectedLocation = LocationOptions[0];
    }
}
