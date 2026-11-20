using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Responsible.ExView;
using Responsible.Messages;
using Responsible.Services;

namespace Responsible.ViewModel;

public partial class MoeViewModel : ObservableObject
{
    private readonly IMessenger _messenger;

    [ObservableProperty]
    private bool _isSidebarVisible = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendPromptCommand))]
    private string _promptText = string.Empty;

    [ObservableProperty]
    private ConversationItemViewModel? _selectedConversation;

    [ObservableProperty]
    private bool _isSearchOpen;

    [ObservableProperty]
    private ModelOptionViewModel? _selectedModelOption;

    public MoeViewModel(IModelCatalogService catalog, IMessenger messenger, IChatSessionService session)
    {
        _messenger = messenger;
        Session = session;
        ModelOptions = catalog.ModelOptions;
        _selectedModelOption = ModelOptions.FirstOrDefault(o => o.IsSelected) ?? ModelOptions.FirstOrDefault();
        messenger.Register<ChatStatsMessage>(this, (_, m) =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) RaiseStatsChanged();
            else dispatcher.BeginInvoke(RaiseStatsChanged);
        });
    }

    public IChatSessionService Session { get; }

    public ObservableCollection<ModelOptionViewModel> ModelOptions { get; }

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } =
    [
        new()
        {
            Name = "Responsible",
            IsExpanded = true,
            Conversations =
            {
                new() { Title = "主页面架构设计" },
                new() { Title = "导航服务实现" },
            },
        },
        new()
        {
            Name = "Playground",
            Conversations =
            {
                new() { Title = "演示对话" },
            },
        },
    ];

    public int Rounds => Session.Stats.Rounds;

    public int Steps => Session.Stats.Steps;

    public int TokensPerSecond => Session.Stats.TokensPerSecond;

    public string TokenUsageText => FormatTokens(Session.Stats.TotalTokens);

    public string CacheHitRateText => FormatCache(Session.Stats.CacheHitRate);

    public int ContextUsagePercent => (int)Math.Round(Session.Stats.ContextPercent);

    private void RaiseStatsChanged()
    {
        OnPropertyChanged(nameof(Rounds));
        OnPropertyChanged(nameof(Steps));
        OnPropertyChanged(nameof(TokensPerSecond));
        OnPropertyChanged(nameof(TokenUsageText));
        OnPropertyChanged(nameof(CacheHitRateText));
        OnPropertyChanged(nameof(ContextUsagePercent));
    }

    private static string FormatTokens(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.0}M tok" : n >= 1_000 ? $"{n / 1_000.0:0.0}K tok" : $"{n} tok";

    // 部分命中不得四舍五入成 100%（与 Web 统计条同规则）
    private static string FormatCache(double rate)
    {
        var fixedText = rate.ToString("0.0");
        return rate > 0 && rate < 100 && fixedText == "100.0" ? "99.9%" : $"{fixedText}%";
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand(CanExecute = nameof(CanSendPrompt))]
    private void SendPrompt()
    {
        Session.Send(PromptText, SelectedModelOption?.DisplayName ?? "Test");
        PromptText = string.Empty;
    }

    private bool CanSendPrompt() => !string.IsNullOrWhiteSpace(PromptText);

    [RelayCommand]
    private void OpenSearch() => IsSearchOpen = true;

    [RelayCommand]
    private void CloseSearch() => IsSearchOpen = false;

    [RelayCommand]
    private void OpenSettings() => _messenger.Send(new OpenExViewMessage(typeof(SettingsExView)));
}