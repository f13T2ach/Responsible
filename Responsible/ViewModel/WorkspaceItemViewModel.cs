using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Responsible.ViewModel;

public partial class WorkspaceItemViewModel : ObservableObject
{
    public required string Name { get; init; }

    [ObservableProperty]
    private bool _isExpanded;

    public ObservableCollection<ConversationItemViewModel> Conversations { get; } = [];
}