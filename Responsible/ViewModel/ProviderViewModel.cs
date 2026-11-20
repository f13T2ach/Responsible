using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Responsible.ViewModel;

public enum ProviderKind
{
    Account,
    Catalog,
    Custom,
}

public partial class ProviderModelEntryViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;
}

public partial class ProviderViewModel : ObservableObject
{
    public required string Id { get; init; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAccount))]
    [NotifyPropertyChangedFor(nameof(IsCustom))]
    private ProviderKind _kind;

    [ObservableProperty]
    private bool _isConfigured = true;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    private string _protocol = "OpenAI Chat Completions";

    public ObservableCollection<ProviderModelEntryViewModel> Models { get; } = [];

    public bool IsAccount => Kind == ProviderKind.Account;

    public bool IsCustom => Kind == ProviderKind.Custom;
}