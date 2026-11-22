using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Responsible.Services;
using Responsible.Localization;

namespace Responsible.ViewModel;

public partial class RvmDeviceItem : ObservableObject
{
    public RvmDeviceItem(string name, bool available, bool isSelected)
    {
        Name = name;
        Available = available;
        IsSelected = isSelected;
    }

    public string Name { get; }

    public bool Available { get; }

    public string DisplayName => Available ? Name : Loc.F("L.Penetration.NotDetected", Name);

    [ObservableProperty]
    private bool _isSelected;
}

// 穿透策略是宿主级设置：会话创建时固化到 AppContainer 能力与设备 ACL，
// 因此修改后需要下一次启动受保护会话才会生效。
public partial class RvmPenetrationViewModel : ObservableObject
{
    private readonly IRvmPolicyService _store;
    private bool _loading;

    public RvmPenetrationViewModel(IRvmPolicyService store)
    {
        _store = store;
        var policy = store.Current;
        _loading = true;
        _networkEnabled = policy.NetworkEnabled;
        _devicesEnabled = policy.DevicesEnabled;
        _loading = false;
        _loadError = store.LoadError ?? string.Empty;
        RefreshDevices(policy);
    }

    [ObservableProperty]
    private bool _networkEnabled;

    [ObservableProperty]
    private bool _devicesEnabled;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _loadError = string.Empty;

    public ObservableCollection<RvmDeviceItem> Devices { get; } = [];

    public bool HasNoDevices => Devices.Count == 0;

    partial void OnNetworkEnabledChanged(bool value) => Persist();

    partial void OnDevicesEnabledChanged(bool value) => Persist();

    [RelayCommand]
    private void RefreshDevices() => RefreshDevices(_store.Current);

    private void RefreshDevices(RvmPolicy policy)
    {
        foreach (var item in Devices)
            item.PropertyChanged -= OnDeviceChanged;
        Devices.Clear();

        var selected = new HashSet<string>(policy.Devices, StringComparer.OrdinalIgnoreCase);
        var ports = _store.EnumerateSerialPorts();
        foreach (var port in ports)
        {
            var item = new RvmDeviceItem(port, true, selected.Contains(port));
            item.PropertyChanged += OnDeviceChanged;
            Devices.Add(item);
        }
        foreach (var missing in policy.Devices.Where(name => !ports.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            var item = new RvmDeviceItem(missing, false, true);
            item.PropertyChanged += OnDeviceChanged;
            Devices.Add(item);
        }

        OnPropertyChanged(nameof(HasNoDevices));
        StatusText = ports.Count == 0
            ? Loc.T("L.Penetration.NoPorts")
            : Loc.F("L.Penetration.PortsDetected", ports.Count);
    }

    private void OnDeviceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loading || e.PropertyName != nameof(RvmDeviceItem.IsSelected)) return;
        Persist();
    }

    private void Persist()
    {
        if (_loading) return;
        try
        {
            _store.Save(new RvmPolicy(
                NetworkEnabled,
                DevicesEnabled,
                Devices.Where(item => item.IsSelected).Select(item => item.Name).ToArray()));
            LoadError = string.Empty;
            StatusText = Loc.T("L.Penetration.Saved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadError = $"Failed to save the penetration policy: {ex.Message}";
        }
    }
}