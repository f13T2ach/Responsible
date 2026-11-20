using CommunityToolkit.Mvvm.ComponentModel;

namespace Responsible.ViewModel;

public partial class ModelOptionViewModel : ObservableObject
{
    public required string ProviderName { get; init; }

    public required string DisplayName { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}