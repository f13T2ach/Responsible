using CommunityToolkit.Mvvm.ComponentModel;

namespace Responsible.MoeViews.Plugins;

public sealed partial class PluginItemViewModel : ObservableObject
{
    private readonly Func<PluginItemViewModel, bool, Task> _toggle;
    private bool _suppressToggle;

    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private bool _canToggle;

    [ObservableProperty]
    private bool _pendingRestart;

    [ObservableProperty]
    private string? _readOnlyReason;

    [ObservableProperty]
    private string? _diagnostic;

    [ObservableProperty]
    private PluginRuntimeStatus _status;

    public PluginItemViewModel(MoePluginInfo info, Func<PluginItemViewModel, bool, Task> toggle)
    {
        _toggle = toggle;
        EntryId = info.EntryId;
        ToggleId = info.ToggleId;
        ModuleName = info.ModuleName;
        Title = info.Title;
        Description = info.Description;
        Update(info);
    }

    public string EntryId { get; }

    public string ToggleId { get; }

    public string ModuleName { get; }

    public string Title { get; }

    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool HasReadOnlyReason => !string.IsNullOrWhiteSpace(ReadOnlyReason);

    public bool HasDiagnostic => !string.IsNullOrWhiteSpace(Diagnostic);

    public string StatusText => Status switch
    {
        PluginRuntimeStatus.Active => "运行中",
        PluginRuntimeStatus.Failed => "加载失败",
        _ => "已停用",
    };

    public string PendingText => "待重启";

    internal void Update(MoePluginInfo info)
    {
        _suppressToggle = true;
        IsOn = info.Enabled;
        _suppressToggle = false;
        CanToggle = info.CanToggle;
        PendingRestart = info.PendingRestart;
        ReadOnlyReason = info.ReadOnlyReason;
        Diagnostic = info.Diagnostic;
        Status = info.Status;
        OnPropertyChanged(nameof(HasReadOnlyReason));
        OnPropertyChanged(nameof(HasDiagnostic));
        OnPropertyChanged(nameof(StatusText));
    }

    internal void SetIsOnSilently(bool value)
    {
        _suppressToggle = true;
        IsOn = value;
        _suppressToggle = false;
    }

    partial void OnIsOnChanged(bool value)
    {
        if (_suppressToggle || !CanToggle)
            return;
        _ = _toggle(this, value);
    }
}