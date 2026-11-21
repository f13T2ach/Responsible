using CommunityToolkit.Mvvm.ComponentModel;

namespace Responsible.ViewModel;

public partial class ConversationItemViewModel : ObservableObject
{
    public required string Title { get; init; }

    // 占位项（DSH 尚未启动）没有持久化会话 id，点击只负责回到对话区
    public string? SessionId { get; init; }

    public long UpdatedAt { get; init; }

    // 选中态只由 MoeViewModel 维护，避免多个工作区的 ListBox 各自保留选中项
    [ObservableProperty]
    private bool _isActive;

    public bool CanDelete => SessionId is { Length: > 0 };
}
