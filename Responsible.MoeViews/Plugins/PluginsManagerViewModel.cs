using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Responsible.MoeViews.Plugins;

[MoeView(typeof(PluginsManagerView))]
public partial class PluginsManagerViewModel : ObservableObject
{
    private readonly IDshPluginService _plugins;
    private readonly IMoeViewNavigator _navigator;
    private readonly ObservableCollection<PluginItemViewModel> _userItems = [];
    private readonly ObservableCollection<PluginItemViewModel> _builtInItems = [];
    private readonly SemaphoreSlim _toggleGate = new(1, 1);

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "正在读取插件…";

    [ObservableProperty]
    private bool _isLive;

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private string _lastMessage = string.Empty;

    public PluginsManagerViewModel(IDshPluginService plugins, IMoeViewNavigator navigator)
    {
        _plugins = plugins;
        _navigator = navigator;
        UserView = new ListCollectionView(_userItems) { Filter = IsPluginVisible };
        BuiltInView = new ListCollectionView(_builtInItems) { Filter = IsPluginVisible };
        UserView.SortDescriptions.Add(new SortDescription(nameof(PluginItemViewModel.Title), ListSortDirection.Ascending));
        BuiltInView.SortDescriptions.Add(new SortDescription(nameof(PluginItemViewModel.Title), ListSortDirection.Ascending));
    }

    public ICollectionView UserView { get; }

    public ICollectionView BuiltInView { get; }

    public int UserCount => _userItems.Count;

    public int BuiltInCount => _builtInItems.Count;

    public bool HasUserPlugins => UserCount > 0;

    public bool HasBuiltInPlugins => BuiltInCount > 0;

    public bool IsEmpty => UserCount == 0 && BuiltInCount == 0;

    public bool HasMessage => LastMessage.Length > 0;

    public bool HasPendingRestart => PendingCount > 0;

    public bool CanRestartKernel => PendingCount > 0 && !IsLoading;

    public string PendingText => $"有 {PendingCount} 项更改等待重启内核后生效";

    public async Task LoadAsync()
    {
        if (IsLoading)
            return;

        IsLoading = true;
        try
        {
            var inventory = await _plugins.GetInventoryAsync();
            Apply(inventory, preserveItems: false);
        }
        catch (Exception ex)
        {
            LastMessage = $"读取插件失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private async Task RestartKernelAsync()
    {
        if (IsLoading)
            return;

        IsLoading = true;
        try
        {
            var result = await _plugins.RestartKernelAsync();
            LastMessage = result.Message;
            var inventory = await _plugins.GetInventoryAsync();
            Apply(inventory, preserveItems: false);
        }
        catch (Exception ex)
        {
            LastMessage = $"重启内核失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Close() => _navigator.CloseCurrent();

    partial void OnSearchTextChanged(string value) => RaiseCountsChanged();

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(CanRestartKernel));

    partial void OnPendingCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasPendingRestart));
        OnPropertyChanged(nameof(CanRestartKernel));
        OnPropertyChanged(nameof(PendingText));
    }

    partial void OnLastMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    private void Apply(MoePluginInventory inventory, bool preserveItems)
    {
        StatusText = inventory.StatusText;
        IsLive = inventory.IsLive;
        PendingCount = inventory.PendingCount;

        if (!preserveItems)
        {
            _userItems.Clear();
            _builtInItems.Clear();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in inventory.Plugins.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            seen.Add(info.EntryId);
            var target = info.Origin == PluginOrigin.User ? _userItems : _builtInItems;
            var existing = target.FirstOrDefault(item => item.EntryId == info.EntryId);
            if (existing is not null)
                existing.Update(info);
            else
                target.Add(new PluginItemViewModel(info, ToggleAsync));
        }

        if (preserveItems)
        {
            foreach (var stale in _userItems.Where(item => !seen.Contains(item.EntryId)).ToArray())
                _userItems.Remove(stale);
            foreach (var stale in _builtInItems.Where(item => !seen.Contains(item.EntryId)).ToArray())
                _builtInItems.Remove(stale);
        }

        RaiseCountsChanged();
    }

    private async Task ToggleAsync(PluginItemViewModel item, bool enabled)
    {
        await _toggleGate.WaitAsync();
        try
        {
            var inventory = await _plugins.SetEnabledAsync(item.ToggleId, enabled);
            Apply(inventory, preserveItems: true);
            LastMessage = inventory.PendingCount > 0
                ? $"已保存，{inventory.PendingCount} 项更改将在重启内核后生效"
                : "插件状态已更新";
        }
        catch (Exception ex)
        {
            item.SetIsOnSilently(!enabled);
            LastMessage = $"切换失败：{ex.Message}";
        }
        finally
        {
            _toggleGate.Release();
        }
    }

    private bool IsPluginVisible(object item)
    {
        if (string.IsNullOrWhiteSpace(SearchText) || item is not PluginItemViewModel plugin)
            return true;

        var query = SearchText.Trim();
        return plugin.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || plugin.ModuleName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (plugin.Description?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    private void RaiseCountsChanged()
    {
        UserView.Refresh();
        BuiltInView.Refresh();
        OnPropertyChanged(nameof(UserCount));
        OnPropertyChanged(nameof(BuiltInCount));
        OnPropertyChanged(nameof(HasUserPlugins));
        OnPropertyChanged(nameof(HasBuiltInPlugins));
        OnPropertyChanged(nameof(IsEmpty));
    }
}