using System.Collections.ObjectModel;
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

    public SettingsViewModel(IModelCatalogService catalog, IProviderEditorService editor, IAppThemeService theme, IMessenger messenger)
    {
        _catalog = catalog;
        _editor = editor;
        _theme = theme;
        _messenger = messenger;
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