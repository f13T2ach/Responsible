using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Responsible.Messages;
using Responsible.Services;
using Wpf.Ui.Controls;

namespace Responsible.ViewModel;

public sealed class SettingsNavItemViewModel(string id, string label, SymbolRegular icon)
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    public SymbolRegular Icon { get; } = icon;
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly IModelCatalogService _catalog;
    private readonly IProviderEditorService _editor;
    private readonly IAppThemeService _theme;
    private readonly IMessenger _messenger;
    private readonly IWinFspService _winFsp;
    private readonly IAclAuthorizationService _acl;

    public SettingsViewModel(
        IModelCatalogService catalog,
        IProviderEditorService editor,
        IAppThemeService theme,
        IMessenger messenger,
        IWinFspService winFsp,
        IAclAuthorizationService acl)
    {
        _catalog = catalog;
        _editor = editor;
        _theme = theme;
        _messenger = messenger;
        _winFsp = winFsp;
        _acl = acl;
        _winFspDriveLetter = winFsp.DriveLetter;
        WinFspDriveLetters = winFsp.AvailableDriveLetters();
        RefreshWinFsp();
        LoadAclAuthorizations();
        _selectedNavItem = NavItems.First(i => i.Id == "models");
    }

    public ObservableCollection<SettingsNavItemViewModel> NavItems { get; } =
    [
        new("account", "账号与余额", SymbolRegular.Person24),
        new("general", "通用设置", SymbolRegular.Settings24),
        new("models", "模型", SymbolRegular.Database24),
        new("plugins", "内置插件", SymbolRegular.Options24),
        new("presets", "Agent 预设", SymbolRegular.Bot24),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModelsTab))]
    [NotifyPropertyChangedFor(nameof(IsGeneralTab))]
    [NotifyPropertyChangedFor(nameof(IsPlaceholderTab))]
    private SettingsNavItemViewModel _selectedNavItem = null!;

    public bool IsModelsTab => SelectedNavItem.Id == "models";

    public bool IsGeneralTab => SelectedNavItem.Id == "general";

    public bool IsPlaceholderTab => SelectedNavItem.Id is not ("models" or "general");

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
    private string _winFspVersion = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWinFspNotBusy))]
    private bool _isWinFspBusy;

    [ObservableProperty]
    private string _winFspError = string.Empty;

    [ObservableProperty]
    private string _winFspDriveLetter;

    public string WinFspStatusText => IsWinFspInstalled ? $"已安装 {WinFspVersion}" : "未安装";

    public string WinFspInstallerText => IsWinFspInstalled ? "修复 / 重装" : "安装 WinFsp";

    public bool IsWinFspNotBusy => !IsWinFspBusy;

    partial void OnWinFspDriveLetterChanged(string value)
    {
        if (_winFsp.TrySetDriveLetter(value, out var error))
            WinFspError = string.Empty;
        else
            WinFspError = error ?? "盘符无效。";
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
            AclError = error ?? "无法添加该目录。";
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
        if (item is null || item.State != AclGrantState.Error)
            return Task.CompletedTask;

        return item.LastOperationWasRevoke ? RevokeAclAsync(item) : GrantAclAsync(item);
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
            item.Complete(AclGrantState.Error, $"授权失败：{ex.Message}");
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
            item.Complete(AclGrantState.Error, $"撤销失败：{ex.Message}");
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