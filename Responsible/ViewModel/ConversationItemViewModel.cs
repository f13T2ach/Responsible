using CommunityToolkit.Mvvm.ComponentModel;

namespace Responsible.ViewModel;

public partial class ConversationItemViewModel : ObservableObject
{
    public required string Title { get; init; }
}