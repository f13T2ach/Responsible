using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Responsible.Localization;
using Responsible.Messages;
using Responsible.Services;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Responsible.ViewModel;

public sealed partial class SettingsNavItemViewModel(string id, string labelKey, SymbolRegular icon) : ObservableObject
{
    public string Id { get; } = id;

    public string LabelKey { get; } = labelKey;

    public string Label => Loc.T(LabelKey);

    public SymbolRegular Icon { get; } = icon;

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly IModelCatalogService _catalog;
    private readonly IProviderEditorService _editor;
    private readonly IAppThemeService _theme;
    private readonly IMessenger _messenger;
    private readonly IWinFspService _winFsp;
    private readonly IAclAuthorizationService _acl;
    private readonly IChatSessionService _chat;
    private readonly IRvmEngineService _rvm;
    private readonly IContentDialogService _dialogs;

    public SettingsViewModel(
        IModelCatalogService catalog,
        IProviderEditorService editor,
        IAppThemeService theme,
        IMessenger messenger,
        IWinFspService winFsp,
        IAclAuthorizationService acl,
        IChatSessionService chat,
        IRvmEngineService rvm,
        IRvmPolicyService rvmPolicy,
        IContentDialogService dialogs)
    {
        _catalog = catalog;
        _editor = editor;
        _theme = theme;
        _messenger = messenger;
        _winFsp = winFsp;
        _acl = acl;
        _chat = chat;
        _rvm = rvm;
        _dialogs = dialogs;
        Penetration = new RvmPenetrationViewModel(rvmPolicy);
        _winFspDriveLetter = winFsp.DriveLetter;
        WinFspDriveLetters = winFsp.AvailableDriveLetters();
        RefreshWinFsp();
        LoadAclAuthorizations();
        _selectedNavItem = NavItems.First(i => i.Id == "models");
        messenger.Register<LanguageChangedMessage>(this, (_, _) => OnLanguageChanged());
    }

    private void OnLanguageChanged()
    {
        foreach (var item in NavItems)
            item.RefreshLabel();
        OnPropertyChanged(nameof(PlaceholderText));
        OnPropertyChanged(nameof(WinFspStatusText));
        OnPropertyChanged(nameof(WinFspInstallerText));
        OnPropertyChanged(nameof(RvmSessionStatusText));
        if (_lastRvmDiff is { } diff)
        {
            ApplyRvmArchive(diff);
            if (_rvmStatusFactory is { } factory)
                RvmArchiveStatus = factory();
        }
    }

    public ObservableCollection<SettingsNavItemViewModel> NavItems { get; } =
    [
        new("rvm", "L.SettingsNav.Rvm", SymbolRegular.Shield24),
        new("general", "L.SettingsNav.General", SymbolRegular.Settings24),
        new("models", "L.SettingsNav.Models", SymbolRegular.Database24),
        new("plugins", "L.SettingsNav.Plugins", SymbolRegular.Options24),
        new("presets", "L.SettingsNav.Presets", SymbolRegular.Bot24),
    ];

    public IReadOnlyList<string> LanguageOptions { get; } = ["简体中文", "English"];

    [ObservableProperty]
    private string _selectedLanguage = Loc.IsChinese ? "简体中文" : "English";

    partial void OnSelectedLanguageChanged(string value) =>
        Loc.SetLanguage(value == "English" ? Loc.English : Loc.Chinese);

    public string PlaceholderText => Loc.F("L.Settings.ComingSoon", SelectedNavItem.Label);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModelsTab))]
    [NotifyPropertyChangedFor(nameof(IsGeneralTab))]
    [NotifyPropertyChangedFor(nameof(IsRvmTab))]
    [NotifyPropertyChangedFor(nameof(IsPlaceholderTab))]
    [NotifyPropertyChangedFor(nameof(PlaceholderText))]
    private SettingsNavItemViewModel _selectedNavItem = null!;

    public bool IsModelsTab => SelectedNavItem.Id == "models";

    public bool IsGeneralTab => SelectedNavItem.Id == "general";

    public bool IsRvmTab => SelectedNavItem.Id == "rvm";

    public bool IsPlaceholderTab => SelectedNavItem.Id is not ("models" or "general" or "rvm");

    public bool IsSystemTheme
    {
        get => _theme.Mode == ThemeMode.System;
        set { if (value) _theme.Mode = ThemeMode.System; }
    }

    public bool IsLightTheme
    {
        get => _theme.Mode == ThemeMode.Light;
        set { if (value) _theme.Mode = ThemeMode.Light; }
    }

    public bool IsDarkTheme
    {
        get => _theme.Mode == ThemeMode.Dark;
        set { if (value) _theme.Mode = ThemeMode.Dark; }
    }

    public ObservableCollection<ProviderViewModel> Providers => _catalog.Providers;

    public IReadOnlyList<string> WinFspDriveLetters { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WinFspStatusText))]
    [NotifyPropertyChangedFor(nameof(WinFspInstallerText))]
    private bool _isWinFspInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WinFspStatusText))]
    private string _winFspVersion = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWinFspNotBusy))]
    private bool _isWinFspBusy;

    [ObservableProperty]
    private string _winFspError = string.Empty;

    [ObservableProperty]
    private string _winFspDriveLetter;

    public string WinFspStatusText => IsWinFspInstalled
        ? Loc.F("L.WinFsp.Installed", WinFspVersion)
        : Loc.T("L.WinFsp.NotInstalled");

    public string WinFspInstallerText => IsWinFspInstalled
        ? Loc.T("L.WinFsp.Repair")
        : Loc.T("L.WinFsp.Install");

    public bool IsWinFspNotBusy => !IsWinFspBusy;

    partial void OnWinFspDriveLetterChanged(string value)
    {
        if (_winFsp.TrySetDriveLetter(value, out var error))
            WinFspError = string.Empty;
        else
            WinFspError = error ?? "Invalid drive letter.";
    }

    [RelayCommand]
    private void RefreshWinFsp()
    {
        var probe = _winFsp.Probe();
        IsWinFspInstalled = probe.Installed;
        WinFspVersion = probe.Installed ? probe.Version : string.Empty;
    }

    [RelayCommand]
    private async Task InstallWinFspAsync()
    {
        IsWinFspBusy = true;
        WinFspError = string.Empty;
        try
        {
            var result = await _winFsp.InstallAsync();
            if (result.Success)
                RefreshWinFsp();
            else
                WinFspError = result.Message;
        }
        finally
        {
            IsWinFspBusy = false;
        }
    }

    public RvmPenetrationViewModel Penetration { get; }

    public ObservableCollection<AclToolItemViewModel> AclAuthorizations { get; } = [];

    [ObservableProperty]
    private string _aclError = string.Empty;

    public bool HasNoAclAuthorizations => AclAuthorizations.Count == 0;

    public bool IsAclNotBusy => !AclAuthorizations.Any(item => item.IsBusy);

    public void AddAclTool(string path)
    {
        if (!IsAclNotBusy)
            return;

        var (grant, error) = _acl.Prepare(path);
        if (grant is null)
        {
            AclError = error ?? "Unable to add this directory.";
            return;
        }

        AclError = string.Empty;
        var item = new AclToolItemViewModel(grant);
        item.PropertyChanged += OnAclItemPropertyChanged;
        AclAuthorizations.Add(item);
        OnPropertyChanged(nameof(HasNoAclAuthorizations));
        OnPropertyChanged(nameof(IsAclNotBusy));
        _ = GrantAclAsync(item);
    }

    [RelayCommand]
    private Task RetryAclAsync(AclToolItemViewModel? item)
    {
        if (item is null || item.IsBusy) return Task.CompletedTask;
        if (item.State != AclGrantState.Error && item.IsValid) return Task.CompletedTask;

        return item.LastOperationWasRevoke && item.State == AclGrantState.Error
            ? RevokeAclAsync(item)
            : GrantAclAsync(item);
    }

    [RelayCommand]
    private Task RemoveAclAsync(AclToolItemViewModel? item) =>
        item is null ? Task.CompletedTask : RevokeAclAsync(item);

    private async Task GrantAclAsync(AclToolItemViewModel item)
    {
        item.BeginGrant();
        AclError = string.Empty;
        try
        {
            var progress = new Progress<AclProgress>(item.ApplyProgress);
            var result = await _acl.GrantAsync(item.Grant, progress);
            item.Complete(
                result.Success ? AclGrantState.Authorized : AclGrantState.Error,
                result.Message);
        }
        catch (Exception ex)
        {
            item.Complete(AclGrantState.Error, $"Grant failed: {ex.Message}");
        }
    }

    private async Task RevokeAclAsync(AclToolItemViewModel item)
    {
        item.BeginRevoke();
        AclError = string.Empty;
        try
        {
            var progress = new Progress<AclProgress>(item.ApplyProgress);
            var result = await _acl.RevokeAsync(item.Grant, progress);
            if (!result.Success)
            {
                item.Complete(AclGrantState.Error, result.Message);
                return;
            }

            item.PropertyChanged -= OnAclItemPropertyChanged;
            AclAuthorizations.Remove(item);
            OnPropertyChanged(nameof(HasNoAclAuthorizations));
            OnPropertyChanged(nameof(IsAclNotBusy));
        }
        catch (Exception ex)
        {
            item.Complete(AclGrantState.Error, $"Revoke failed: {ex.Message}");
        }
    }

    private void LoadAclAuthorizations()
    {
        foreach (var grant in _acl.Grants)
        {
            var item = new AclToolItemViewModel(grant);
            item.PropertyChanged += OnAclItemPropertyChanged;
            AclAuthorizations.Add(item);
        }

        if (!string.IsNullOrWhiteSpace(_acl.LoadError))
            AclError = _acl.LoadError;
    }

    private void OnAclItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AclToolItemViewModel.IsBusy))
            OnPropertyChanged(nameof(IsAclNotBusy));
    }

    public ObservableCollection<RvmArchiveChangeItem> RvmArchiveChanges { get; } = [];

    private RvmWorkspaceDiff? _lastRvmDiff;
    private Func<string>? _rvmStatusFactory;

    public bool HasRvmSession => _chat.HasActiveRvmSession;

    public bool HasRvmArchiveChanges => RvmArchiveChanges.Count > 0;

    public bool ShowEmptyRvmArchive => HasAuditedRvmArchive && !HasRvmArchiveChanges;

    public string RvmSessionStatusText => HasRvmSession
        ? Loc.F("L.Session.RvmMounted", _chat.ActiveRvmRoot)
        : Loc.T("L.Session.RvmNotMounted");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRvmArchiveIdle))]
    [NotifyPropertyChangedFor(nameof(CanOperateRvmArchive))]
    private bool _isRvmArchiveBusy;

    [ObservableProperty]
    private string _rvmArchiveStatus = string.Empty;

    [ObservableProperty]
    private string _rvmArchiveError = string.Empty;

    [ObservableProperty]
    private bool _hasAuditedRvmArchive;

    public bool IsRvmArchiveIdle => !IsRvmArchiveBusy;

    public bool CanOperateRvmArchive => HasRvmSession && !IsRvmArchiveBusy;

    [RelayCommand]
    private void RefreshRvmSession()
    {
        OnPropertyChanged(nameof(HasRvmSession));
        OnPropertyChanged(nameof(RvmSessionStatusText));
        OnPropertyChanged(nameof(CanOperateRvmArchive));
        if (!HasRvmSession)
        {
            ClearRvmArchive();
            RvmArchiveStatus = string.Empty;
        }
        OnPropertyChanged(nameof(ShowEmptyRvmArchive));
    }

    [RelayCommand]
    private async Task AuditRvmArchiveAsync()
    {
        if (!TryBeginRvmArchive(out var error))
        {
            RvmArchiveError = error;
            return;
        }

        try
        {
            var diff = await _chat.DiffRvmWorkspaceAsync();
            if (diff is null)
            {
                RvmArchiveError = $"Failed to read the session archive: {_rvm.Status}";
                return;
            }

            ApplyRvmArchive(diff);
            HasAuditedRvmArchive = true;
            SetRvmStatus(() => DescribeRvmDiff(diff));
        }
        catch (Exception ex)
        {
            RvmArchiveError = $"Failed to read the session archive: {ex.Message}";
        }
        finally
        {
            IsRvmArchiveBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncRvmArchiveAsync()
    {
        if (!TryBeginRvmArchive(out var error))
        {
            RvmArchiveError = error;
            return;
        }

        try
        {
            if (!await ConfirmRvmArchiveAsync(
                    Loc.T("L.Session.SyncConfirmTitle"),
                    Loc.T("L.Session.SyncConfirmText")))
            {
                RvmArchiveStatus = string.Empty;
                return;
            }

            var result = await _chat.SyncRvmWorkspaceAsync(true);
            if (result is null)
            {
                RvmArchiveError = $"Sync failed: {_rvm.Status}";
                return;
            }

            SetRvmStatus(() => result.Conflicts.Count == 0
                ? Loc.F("L.Session.SyncDone", result.Applied.Count)
                : Loc.F("L.Session.SyncDoneConflicts", result.Applied.Count, result.Conflicts.Count));
            HasAuditedRvmArchive = false;
            await RefreshRvmArchiveSilentlyAsync();
        }
        catch (Exception ex)
        {
            RvmArchiveError = $"Sync failed: {ex.Message}";
        }
        finally
        {
            IsRvmArchiveBusy = false;
        }
    }

    [RelayCommand]
    private async Task RollbackRvmArchiveAsync()
    {
        if (!TryBeginRvmArchive(out var error))
        {
            RvmArchiveError = error;
            return;
        }

        try
        {
            if (!await ConfirmRvmArchiveAsync(
                    Loc.T("L.Session.RollbackConfirmTitle"),
                    Loc.T("L.Session.RollbackConfirmText")))
            {
                RvmArchiveStatus = string.Empty;
                return;
            }

            var result = await _chat.RollbackRvmWorkspaceAsync();
            if (result is null)
            {
                RvmArchiveError = $"Rollback failed: {_rvm.Status}";
                return;
            }

            SetRvmStatus(() => Loc.T("L.Session.RollbackDone"));
            HasAuditedRvmArchive = false;
            await RefreshRvmArchiveSilentlyAsync();
        }
        catch (Exception ex)
        {
            RvmArchiveError = $"Rollback failed: {ex.Message}";
        }
        finally
        {
            IsRvmArchiveBusy = false;
        }
    }

    // 会话可能在设置窗口打开期间结束；读取前重新核对，避免把“无会话”误报成引擎失败。
    private bool TryBeginRvmArchive(out string error)
    {
        OnPropertyChanged(nameof(HasRvmSession));
        OnPropertyChanged(nameof(RvmSessionStatusText));
        OnPropertyChanged(nameof(CanOperateRvmArchive));
        if (!HasRvmSession)
        {
            error = "No RVM session disk is mounted for this conversation.";
            return false;
        }
        if (IsRvmArchiveBusy)
        {
            error = "Another session archive operation is still running. Wait for it to finish.";
            return false;
        }

        error = string.Empty;
        IsRvmArchiveBusy = true;
        RvmArchiveError = string.Empty;
        return true;
    }

    private async Task RefreshRvmArchiveSilentlyAsync()
    {
        var diff = await _chat.DiffRvmWorkspaceAsync();
        if (diff is null) return;
        ApplyRvmArchive(diff);
        HasAuditedRvmArchive = true;
    }

    private void ApplyRvmArchive(RvmWorkspaceDiff diff)
    {
        _lastRvmDiff = diff;
        var conflicts = diff.Conflicts.ToDictionary(item => item.Path, item => item, StringComparer.OrdinalIgnoreCase);
        RvmArchiveChanges.Clear();
        foreach (var change in diff.Changes)
        {
            conflicts.Remove(change.Path, out var conflict);
            RvmArchiveChanges.Add(RvmArchiveChangeItem.From(change, conflict));
        }
        foreach (var conflict in conflicts.Values)
            RvmArchiveChanges.Add(RvmArchiveChangeItem.ForConflict(conflict));

        OnPropertyChanged(nameof(HasRvmArchiveChanges));
        OnPropertyChanged(nameof(ShowEmptyRvmArchive));
    }

    partial void OnHasAuditedRvmArchiveChanged(bool value) =>
        OnPropertyChanged(nameof(ShowEmptyRvmArchive));

    private void ClearRvmArchive()
    {
        RvmArchiveChanges.Clear();
        HasAuditedRvmArchive = false;
        _lastRvmDiff = null;
        _rvmStatusFactory = null;
        OnPropertyChanged(nameof(HasRvmArchiveChanges));
    }

    private void SetRvmStatus(Func<string> factory)
    {
        _rvmStatusFactory = factory;
        RvmArchiveStatus = factory();
    }

    private static string DescribeRvmDiff(RvmWorkspaceDiff diff)
    {
        var text = diff.Changes.Count == 0 && diff.Conflicts.Count == 0
            ? Loc.T("L.Session.DiffEmpty")
            : Loc.F("L.Session.DiffSummary", diff.Changes.Count, diff.Conflicts.Count);
        return diff.Truncated ? text + " " + Loc.T("L.Session.DiffTruncated") : text;
    }

    private async Task<bool> ConfirmRvmArchiveAsync(string title, string message)
    {
        _messenger.Send(new DialogOpenChangedMessage(true));
        try
        {
            var result = await _dialogs.ShowAsync(new ContentDialog
            {
                Title = title,
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = message,
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                },
                PrimaryButtonText = Loc.T("L.Common.Continue"),
                CloseButtonText = Loc.T("L.Common.Cancel"),
                DialogWidth = 460,
                DialogHeight = 260,
            }, CancellationToken.None);
            return result == ContentDialogResult.Primary;
        }
        finally
        {
            _messenger.Send(new DialogOpenChangedMessage(false));
        }
    }

    [RelayCommand]
    private void Close() => _messenger.Send(new CloseExViewMessage());

    [RelayCommand]
    private void OpenConfigFile() => _catalog.OpenConfigFile();

    [RelayCommand]
    private void AddProvider() => _ = _editor.EditAsync(null);

    [RelayCommand]
    private void EditProvider(ProviderViewModel? provider) => _ = _editor.EditAsync(provider);

    [RelayCommand]
    private void DeleteProvider(ProviderViewModel? provider)
    {
        if (provider != null)
            _catalog.Remove(provider);
    }
}

public sealed class RvmArchiveChangeItem
{
    private RvmArchiveChangeItem(string path, string kindKey, string sizeText, string conflictText)
    {
        Path = path;
        KindKey = kindKey;
        SizeText = sizeText;
        ConflictText = conflictText;
    }

    public string Path { get; }

    public string KindKey { get; }

    public string KindText => Loc.T(KindKey);

    public string SizeText { get; }

    public string ConflictText { get; }

    public bool HasConflict => ConflictText.Length > 0;

    public static RvmArchiveChangeItem From(RvmFileChange change, RvmSyncConflict? conflict) =>
        new(change.Path, change.Kind switch
        {
            RvmChangeKind.Added => "L.Session.KindAdded",
            RvmChangeKind.Deleted => "L.Session.KindDeleted",
            _ => "L.Session.KindModified",
        }, $"{FormatBytes(change.BeforeBytes)} → {FormatBytes(change.AfterBytes)}", conflict?.Reason ?? string.Empty);

    public static RvmArchiveChangeItem ForConflict(RvmSyncConflict conflict) =>
        new(conflict.Path, "L.Session.KindConflict", "—", conflict.Reason);

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1024UL * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024UL * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
        >= 1024UL => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B",
    };
}