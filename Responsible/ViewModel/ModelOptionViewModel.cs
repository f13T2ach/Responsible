using CommunityToolkit.Mvvm.ComponentModel;

namespace Responsible.ViewModel;

public partial class ModelOptionViewModel : ObservableObject
{
    public required string ProviderName { get; init; }

    public required string DisplayName { get; init; }

    // DSH 注册的 provider 路由与模型 id；本地目录项留空，由 ChatSessionService 回退映射
    public string ProviderRoute { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}