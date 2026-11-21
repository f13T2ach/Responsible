using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Responsible.ViewModel;

public partial class WorkspaceItemViewModel : ObservableObject
{
    private readonly Action<WorkspaceItemViewModel>? _newConversation;
    private readonly Action<WorkspaceItemViewModel>? _openFolder;
    private readonly Action<WorkspaceItemViewModel>? _remove;
    private readonly Func<bool>? _canStart;

    public WorkspaceItemViewModel(
        string name,
        string? cwd,
        string? definitionId,
        Action<WorkspaceItemViewModel>? newConversation = null,
        Action<WorkspaceItemViewModel>? openFolder = null,
        Action<WorkspaceItemViewModel>? remove = null,
        Func<bool>? canStart = null)
    {
        Name = name;
        Cwd = cwd;
        DefinitionId = definitionId;
        _newConversation = newConversation;
        _openFolder = openFolder;
        _remove = remove;
        _canStart = canStart;
    }

    public string Name { get; }

    public string? Cwd { get; }

    public string? DefinitionId { get; }

    public bool IsCustom => DefinitionId is not null;

    public string Key => DefinitionId ?? $"cwd:{Cwd}";

    [ObservableProperty]
    private bool _isExpanded;

    public ObservableCollection<ConversationItemViewModel> Conversations { get; } = [];

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void NewConversation() => _newConversation?.Invoke(this);

    private bool CanStart() => _canStart?.Invoke() ?? true;

    [RelayCommand]
    private void OpenFolder() => _openFolder?.Invoke(this);

    [RelayCommand]
    private void RemoveFromSidebar() => _remove?.Invoke(this);
}
