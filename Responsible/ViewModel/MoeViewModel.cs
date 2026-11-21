using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Responsible.ExView;
using Responsible.Messages;
using Responsible.MoeViews.Plugins;
using Responsible.Services;
using Responsible.View;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Responsible.ViewModel;

public partial class MoeViewModel : ObservableObject
{
    private readonly IMessenger _messenger;
    private readonly IWorkspaceStore _workspaceStore;
    private readonly IWorkspaceEditorService _workspaceEditor;
    private readonly IContentDialogService _dialogs;
    private IReadOnlyList<DshSessionRow> _lastRows = [];

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

    [ObservableProperty]
    private bool _isMoeViewOpen;

    // 输入框下方的审批等级：选项与当前值全部来自 DSH 权限目录 / permissions 投影
    [ObservableProperty]
    private bool _isPermissionAvailable;

    [ObservableProperty]
    private string _permissionLabel = string.Empty;

    [ObservableProperty]
    private SymbolRegular _permissionIcon;

    [ObservableProperty]
    private bool _permissionHasIcon;

    [ObservableProperty]
    private string? _permissionDescription;

    [ObservableProperty]
    private bool _permissionIsDangerous;

    // 真实会话列表到位前禁止新建会话/工作区，避免在未加载的侧栏里制造错位条目
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewConversationCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateWorkspaceCommand))]
    private bool _isConversationsLoaded;

    // 恢复上次会话期间：内容区显示加载态，输入区禁用并显示加载文案
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendPromptCommand))]
    private bool _isSessionLoading;

    public MoeViewModel(
        IModelCatalogService catalog,
        IMessenger messenger,
        IChatSessionService session,
        IWorkspaceStore workspaceStore,
        IWorkspaceEditorService workspaceEditor,
        IContentDialogService dialogs)
    {
        _messenger = messenger;
        _workspaceStore = workspaceStore;
        _workspaceEditor = workspaceEditor;
        _dialogs = dialogs;
        Session = session;
        ModelOptions = catalog.ModelOptions;
        _selectedModelOption = ModelOptions.FirstOrDefault(o => o.IsSelected) ?? ModelOptions.FirstOrDefault();
        messenger.Register<ChatStatsMessage>(this, (_, m) =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) RaiseStatsChanged();
            else dispatcher.BeginInvoke(RaiseStatsChanged);
        });
        messenger.Register<MoeViewOpenChangedMessage>(this, (_, m) =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) IsMoeViewOpen = m.Value;
            else dispatcher.BeginInvoke(() => IsMoeViewOpen = m.Value);
        });
        messenger.Register<ConversationsChangedMessage>(this, (_, m) => Ui(() =>
        {
            IsConversationsLoaded = true;
            RebuildWorkspaces(m.Value);
        }));
        messenger.Register<ModelOptionsChangedMessage>(this, (_, _) => Ui(() =>
            SelectedModelOption = ModelOptions.FirstOrDefault(option => option.IsSelected) ?? ModelOptions.FirstOrDefault()));
        // 列表与历史并行加载：历史先到时当前会话可能还未出现在侧栏，加载完成后再选一次
        session.Reloaded += OnSessionReloaded;
        session.PermissionsChanged += OnPermissionsChanged;
        session.SessionLoadingChanged += OnSessionLoadingChanged;
        session.WorkspaceBadgeChanged += OnWorkspaceBadgeChanged;
        // 页面是 transient：先用服务里已有的列表（上次运行缓存）铺开，再拉一次最新数据（内核未就绪时由 InitializeAsync 负责）
        IsConversationsLoaded = session.IsConversationsLoaded;
        IsSessionLoading = session.IsSessionLoading;
        RebuildWorkspaces(session.Conversations);
        RefreshPermissionUi();
        _ = session.InitializeAsync();
        _ = session.RefreshConversationsAsync();
    }

    public IChatSessionService Session { get; }

    public ObservableCollection<ModelOptionViewModel> ModelOptions { get; }

    public ObservableCollection<PermissionOptionViewModel> PermissionOptions { get; } = [];

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];

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

    private void OnSessionReloaded() => Ui(SelectCurrentConversation);

    private void SelectCurrentConversation()
    {
        var current = Session.CurrentSessionId;
        if (SelectedConversation?.SessionId == current) return;
        SetSelectedConversation(Workspaces
            .SelectMany(workspace => workspace.Conversations)
            .FirstOrDefault(item => item.SessionId == current));
    }

    // 高亮只有一个来源：各工作区的对话项共享同一组 IsActive 标记
    private void SetSelectedConversation(ConversationItemViewModel? item)
    {
        SelectedConversation = item;
        foreach (var conversation in Workspaces.SelectMany(workspace => workspace.Conversations))
            conversation.IsActive = ReferenceEquals(conversation, item);
    }

    private void RebuildWorkspaces(IReadOnlyList<DshSessionRow> rows)
    {
        _lastRows = rows;
        var currentCwd = rows.FirstOrDefault(row => row.SessionId == Session.CurrentSessionId)?.Cwd;
        var expanded = Workspaces
            .Where(workspace => workspace.IsExpanded)
            .Select(workspace => workspace.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var merged = new List<WorkspaceItemViewModel>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 本地自定义工作区优先：先把落在其文件夹里的 DSH 会话归队
        foreach (var definition in _workspaceStore.All)
        {
            var workspace = new WorkspaceItemViewModel(
                definition.Name,
                definition.Directory,
                definition.Id,
                NewConversationInWorkspace,
                OpenWorkspaceFolder,
                RemoveWorkspace,
                canStart: CanCreate,
                rvmProtected: definition.RvmProtected,
                rvmSettings: EditWorkspaceRvmSettings);
            foreach (var row in rows
                         .Where(row => MatchesWorkingDirectory(row.Cwd, definition.Folders))
                         .OrderByDescending(row => row.UpdatedAt))
            {
                workspace.Conversations.Add(ToConversation(row));
                claimed.Add(row.SessionId);
            }
            workspace.IsExpanded = ShouldExpand(workspace, expanded, currentCwd,
                merged.Count == 0 && Workspaces.Count == 0);
            merged.Add(workspace);
        }

        // 剩余的 DSH 会话按 cwd 合并成隐式工作区，不写回本地存储
        var groups = rows
            .Where(row => !claimed.Contains(row.SessionId))
            .GroupBy(row => row.Cwd ?? string.Empty)
            .OrderByDescending(group => group.Key == currentCwd)
            .ThenBy(group => WorkspaceName(group.Key), StringComparer.CurrentCulture);
        foreach (var group in groups)
        {
            var workspace = new WorkspaceItemViewModel(
                WorkspaceName(group.Key),
                string.IsNullOrWhiteSpace(group.Key) ? null : group.Key,
                null,
                NewConversationInWorkspace,
                OpenWorkspaceFolder,
                canStart: CanCreate);
            foreach (var row in group.OrderByDescending(row => row.UpdatedAt))
                workspace.Conversations.Add(ToConversation(row));
            workspace.IsExpanded = ShouldExpand(workspace, expanded, currentCwd,
                merged.Count == 0 && Workspaces.Count == 0);
            merged.Add(workspace);
        }

        Workspaces.Clear();
        foreach (var workspace in merged)
            Workspaces.Add(workspace);

        SetSelectedConversation(Workspaces
            .SelectMany(workspace => workspace.Conversations)
            .FirstOrDefault(item => item.SessionId == Session.CurrentSessionId));
    }

    private bool ShouldExpand(
        WorkspaceItemViewModel workspace,
        HashSet<string> expanded,
        string? currentCwd,
        bool first)
    {
        if (expanded.Contains(workspace.Key)) return true;
        if (!string.IsNullOrWhiteSpace(currentCwd))
        {
            if (string.Equals(workspace.Cwd, currentCwd, StringComparison.OrdinalIgnoreCase)) return true;
            if (workspace.IsCustom
                && MatchesWorkingDirectory(currentCwd, FolderListOf(workspace)))
                return true;
        }
        return first;
    }

    private IReadOnlyList<string> FolderListOf(WorkspaceItemViewModel workspace) =>
        _workspaceStore.All
            .FirstOrDefault(definition => definition.Id == workspace.DefinitionId)?.Folders
        ?? [];

    private static bool MatchesWorkingDirectory(string? cwd, IReadOnlyList<string> folders)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return false;
        var trimmed = cwd.TrimEnd('\\', '/');
        return folders.Any(folder =>
            string.Equals(folder.TrimEnd('\\', '/'), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static ConversationItemViewModel ToConversation(DshSessionRow row) => new()
    {
        Title = string.IsNullOrWhiteSpace(row.Title) ? "新对话" : row.Title,
        SessionId = row.SessionId,
        UpdatedAt = row.UpdatedAt,
    };

    private void NewConversationInWorkspace(WorkspaceItemViewModel workspace) =>
        StartConversationInWorkspace(workspace.Cwd);

    private void StartConversationInWorkspace(string? cwd)
    {
        Session.NewConversation(cwd);
        SetSelectedConversation(null);
        _messenger.Send(new CloseMoeViewMessage());
    }

    private static void OpenWorkspaceFolder(WorkspaceItemViewModel workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace.Cwd) || !Directory.Exists(workspace.Cwd)) return;
        try
        {
            Process.Start(new ProcessStartInfo(workspace.Cwd) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
        }
    }

    private void RemoveWorkspace(WorkspaceItemViewModel workspace)
    {
        if (workspace.DefinitionId is null) return;
        _workspaceStore.Remove(workspace.DefinitionId);
        RebuildWorkspaces(_lastRows);
    }

    private static string WorkspaceName(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "未指定工作区";
        var trimmed = cwd.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        if (name == "default-workspace") return "默认工作区";
        return string.IsNullOrEmpty(name) ? cwd : name;
    }

    private static void Ui(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
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
        if (IsSessionLoading) return;
        Session.Send(PromptText, SelectedModelOption);
        PromptText = string.Empty;
    }

    private bool CanSendPrompt() => !IsSessionLoading && !string.IsNullOrWhiteSpace(PromptText);

    private void OnPermissionsChanged() => Ui(() =>
    {
        RefreshPermissionUi();
        RebuildWorkspaces(_lastRows);
    });

    private void OnSessionLoadingChanged(bool loading) => Ui(() => IsSessionLoading = loading);

    public string WorkspaceBadge => Session.WorkspaceBadge;

    public bool PermissionIsRvmProtection => Session.IsRvmProtected;

    public string? WorkspaceBadgeDetail => Session.WorkspaceBadgeDetail;

    public bool IsWorkspaceBadgeVisible => !string.IsNullOrEmpty(Session.WorkspaceBadge);

    private void OnWorkspaceBadgeChanged() => Ui(() =>
    {
        OnPropertyChanged(nameof(WorkspaceBadge));
        OnPropertyChanged(nameof(WorkspaceBadgeDetail));
        OnPropertyChanged(nameof(IsWorkspaceBadgeVisible));
    });

    private void RefreshPermissionUi()
    {
        var catalog = Session.PermissionCatalog;
        var value = Session.PermissionValue;
        var protectedByRvm = Session.IsRvmProtected;
        OnPropertyChanged(nameof(PermissionIsRvmProtection));
        PermissionOptions.Clear();
        if (catalog is null || value is null)
        {
            IsPermissionAvailable = false;
            PermissionLabel = string.Empty;
            PermissionIcon = default;
            PermissionHasIcon = false;
            PermissionDescription = null;
            PermissionIsDangerous = false;
            return;
        }

        PermissionOptions.Add(new PermissionOptionViewModel
        {
            Value = ChatSessionService.RvmPresetValue,
            Label = "RVM 隔离保护",
            Description = "命令在独立沙箱会话中执行，工作区经专属盘符挂载，临时文件写入工作区专属 Temp；选择下方其它等级会关闭保护并恢复 DSH 沙箱。",
            Badge = "安全",
            Icon = SymbolRegular.Shield24,
            HasIcon = true,
            IsRvmProtection = true,
            IsCurrent = protectedByRvm,
        });

        foreach (var option in catalog.Options)
        {
            var icon = PermissionIconOf(option.Value);
            PermissionOptions.Add(new PermissionOptionViewModel
            {
                Value = option.Value,
                Label = PermissionLabelOf(option.Value, option.Name),
                Description = option.Value == "auto"
                    ? AutoReviewDescription
                    : option.Description ?? PresetDescriptionOf(option.Value),
                Badge = option.Value == "auto" ? "EXP" : null,
                IsDangerous = option.Value == "danger-full-access",
                Icon = icon ?? default,
                HasIcon = icon is not null,
                IsCurrent = !protectedByRvm && option.Value == value,
            });
        }

        if (protectedByRvm)
        {
            IsPermissionAvailable = true;
            PermissionLabel = "RVM 隔离保护";
            PermissionIcon = SymbolRegular.Shield24;
            PermissionHasIcon = true;
            PermissionDescription = PermissionOptions[0].Description;
            PermissionIsDangerous = false;
            return;
        }

        var current = PermissionOptions.FirstOrDefault(option =>
            !option.IsRvmProtection && option.Value == value);
        var currentIcon = current is not null ? current.Icon : PermissionIconOf(value);
        IsPermissionAvailable = PermissionOptions.Count > 0;
        PermissionLabel = current?.Label ?? PermissionLabelOf(value, value);
        PermissionIcon = currentIcon ?? default;
        PermissionHasIcon = current?.HasIcon ?? currentIcon is not null;
        PermissionDescription = current?.Description;
        PermissionIsDangerous = current?.IsDangerous ?? value == "danger-full-access";
    }

    [RelayCommand]
    private async Task SelectPermission(PermissionOptionViewModel? option)
    {
        if (option is null) return;

        if (option.IsRvmProtection)
        {
            if (Session.IsRvmProtected) return;
            var current = Session.CurrentWorkspace;
            RvmSettings? settings = null;
            // 此工作区从未配置过：开启保护时先让用户决定暴露哪些环境变量/工具
            if (current is null || !current.RvmConfigured)
            {
                settings = await ShowRvmSettingsDialogAsync("开启 RVM 隔离保护", current);
                if (settings is null && !await ConfirmRvmSkipSettingsAsync()) return;
            }

            var enabled = await Session.EnableRvmProtectionAsync(settings);
            if (enabled.Ok)
            {
                RebuildWorkspaces(_lastRows);
                return;
            }
            await ShowFailureAsync("开启 RVM 隔离保护失败", enabled.Message);
            return;
        }

        // 受 RVM 保护时，点击同一个 DSH preset 也意味着关闭保护
        if (option.Value == Session.PermissionValue && !Session.IsRvmProtected) return;
        if ((option.Value is "danger-full-access" or "auto") && !await ConfirmPermissionRiskAsync(option))
            return;

        var result = await Session.SelectPermissionAsync(option.Value);
        if (result.Ok) return;
        await ShowFailureAsync("切换权限失败", result.Message);
    }

    private void EditWorkspaceRvmSettings(WorkspaceItemViewModel item) =>
        _ = EditWorkspaceRvmSettingsAsync(item);

    private async Task EditWorkspaceRvmSettingsAsync(WorkspaceItemViewModel item)
    {
        if (item.DefinitionId is not { Length: > 0 } id) return;
        var definition = _workspaceStore.All.FirstOrDefault(workspace => workspace.Id == id);
        if (definition is null) return;

        var settings = await ShowRvmSettingsDialogAsync("RVM 设置", definition);
        if (settings is null) return;
        var result = await Session.UpdateRvmSettingsAsync(id, settings);
        if (result.Ok) return;
        await ShowFailureAsync("保存 RVM 设置失败", result.Message);
    }

    private async Task<RvmSettings?> ShowRvmSettingsDialogAsync(string title, WorkspaceDefinition? definition)
    {
        var viewModel = new RvmSettingsViewModel();
        viewModel.Load(definition?.ExposedEnvironment ?? [], definition?.ExposedTools ?? [],
            definition?.RvmConfigured ?? false);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new RvmSettingsView { DataContext = viewModel },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DialogWidth = 700,
            DialogHeight = 620,
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary ? viewModel.Build() : null;
    }

    private async Task<bool> ConfirmRvmSkipSettingsAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "AI 受到隔离",
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "AI 受到隔离，可能无法使用你安装在本机的工具，确定不额外提供吗？",
                TextWrapping = System.Windows.TextWrapping.Wrap,
            },
            PrimaryButtonText = "仍然开启",
            CloseButtonText = "返回",
            DialogWidth = 480,
            DialogHeight = 280,
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private Task ShowFailureAsync(string title, string message) =>
        ShowDialogAsync(new ContentDialog
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = System.Windows.TextWrapping.Wrap,
            },
            CloseButtonText = "知道了",
            DialogWidth = 440,
            DialogHeight = 240,
        });

    // 与 DSH 的 RiskConfirmation 一致：必须勾选风险确认后才能启用完全权限 / 自动审查
    private async Task<bool> ConfirmPermissionRiskAsync(PermissionOptionViewModel option)
    {
        var isAuto = option.Value == "auto";
        var acknowledge = new System.Windows.Controls.CheckBox
        {
            Content = isAuto ? "我已了解这些风险，并愿意继续" : "我已了解风险，并愿意继续",
            Margin = new System.Windows.Thickness(0, 12, 0, 0),
        };
        var content = new System.Windows.Controls.StackPanel();
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = isAuto ? AutoReviewConfirmDescription : FullAccessConfirmDescription,
            TextWrapping = System.Windows.TextWrapping.Wrap,
        });
        content.Children.Add(acknowledge);
        var dialog = new ContentDialog
        {
            Title = isAuto ? "确认启用自动审查（实验）？" : "确认启用完全权限？",
            Content = content,
            PrimaryButtonText = isAuto ? "启用自动审查" : "启用完全权限",
            CloseButtonText = "取消",
            IsPrimaryButtonEnabled = false,
            DialogWidth = 480,
            DialogHeight = 340,
        };
        acknowledge.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        acknowledge.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private const string FullAccessConfirmDescription =
        "启用完全权限后，智能体将减少确认步骤，并且可以直接执行更多操作，包括敏感操作、文件修改或外部命令。仅建议在你信任当前任务时使用。";

    private const string AutoReviewDescription =
        "无沙箱运行；每次原生工具调用和 PTC 内层调用前由同一模型进行实验性审查。";

    private const string AutoReviewConfirmDescription =
        "自动审查不使用沙箱。每次原生工具调用和 PTC 内层调用前，都会由与当前 agent 相同的模型进行审查；审查拒绝的调用由你批准或拒绝。此功能仍属实验性，可能误放行或误拒绝，并会消耗额外 token。";

    // 内置预设的沙箱说明；宿主目录自带 description 时以宿主文案优先
    private const string ReadOnlyDescription = "只能读取文件，写入与修改会被沙箱拒绝";

    private const string WorkspaceWriteDescription = "可在当前工作区内读写文件，工作区外的修改会被拒绝";

    private const string FullAccessDescription = "不再询问，可自由访问你的文件、终端和网络";

    // 与 DSH 客户端同一套内置标签映射：配置名仍是 kebab 键时才用产品文案
    private static readonly Dictionary<string, (string Label, string English)> PresetLabels = new()
    {
        ["read-only"] = ("仅可查看", "Read Only"),
        ["workspace-write"] = ("工作区内修改", "Workspace Write"),
        ["danger-full-access"] = ("完全权限", "Full access"),
    };

    private static readonly Dictionary<string, string> PresetDescriptions = new()
    {
        ["read-only"] = ReadOnlyDescription,
        ["workspace-write"] = WorkspaceWriteDescription,
        ["danger-full-access"] = FullAccessDescription,
    };

    private static readonly Dictionary<string, SymbolRegular> PresetIcons = new()
    {
        ["read-only"] = SymbolRegular.Eye24,
        ["workspace-write"] = SymbolRegular.Edit24,
        ["danger-full-access"] = SymbolRegular.Warning24,
        ["auto"] = SymbolRegular.Brain24,
        ["custom"] = SymbolRegular.ShieldQuestion24,
    };

    private static string PermissionLabelOf(string value, string name)
    {
        if (value == "auto") return "自动审查";
        if (PresetLabels.TryGetValue(value, out var labels) && (name == value || name == labels.English))
            return labels.Label;
        return DisplayPresetName(name);
    }

    private static SymbolRegular? PermissionIconOf(string value) =>
        PresetIcons.TryGetValue(value, out var icon) ? icon : null;

    private static string? PresetDescriptionOf(string value) =>
        PresetDescriptions.TryGetValue(value, out var description) ? description : null;

    private static string DisplayPresetName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var words = name.Split('-');
        if (!words.All(word => word.Length > 0
                               && word.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9')))
            return name;
        return string.Join(' ', words.Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    [RelayCommand]
    private void OpenSearch() => IsSearchOpen = true;

    [RelayCommand]
    private void CloseSearch() => IsSearchOpen = false;

    [RelayCommand]
    private void OpenSettings() => _messenger.Send(new OpenExViewMessage(typeof(SettingsExView)));

    [RelayCommand]
    private void OpenPlugins() => _messenger.Send(new OpenMoeViewMessage(typeof(PluginsManagerViewModel)));

    // 侧栏对话点击即回到对话区，覆盖 Moe 内容页（重复点当前对话也要生效）
    [RelayCommand]
    private async Task OpenConversation(ConversationItemViewModel? item)
    {
        if (item is not null)
        {
            SetSelectedConversation(item);
            if (item.SessionId is { Length: > 0 } sessionId && sessionId != Session.CurrentSessionId)
                await Session.OpenConversationAsync(sessionId);
        }
        _messenger.Send(new CloseMoeViewMessage());
    }

    [RelayCommand]
    private async Task DeleteConversation(ConversationItemViewModel? item)
    {
        if (item?.SessionId is not { Length: > 0 } sessionId) return;

        var confirm = await ShowDialogAsync(new ContentDialog
        {
            Title = "删除对话",
            Content = new System.Windows.Controls.TextBlock
            {
                Text = $"确定删除「{item.Title}」吗？本地的对话记录会一并删除，且无法恢复。",
                TextWrapping = System.Windows.TextWrapping.Wrap,
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DialogWidth = 460,
            DialogHeight = 260,
        });
        if (confirm != ContentDialogResult.Primary) return;

        if (await Session.DeleteConversationAsync(sessionId)) return;

        await ShowDialogAsync(new ContentDialog
        {
            Title = "删除失败",
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "会话文件未能删除，请稍后重试。",
                TextWrapping = System.Windows.TextWrapping.Wrap,
            },
            CloseButtonText = "知道了",
            DialogWidth = 420,
            DialogHeight = 220,
        });
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        // 对话框在 WebView2 之上会被 HWND 空域遮住，弹出期间折叠 Web
        _messenger.Send(new DialogOpenChangedMessage(true));
        try
        {
            return await _dialogs.ShowAsync(dialog, CancellationToken.None);
        }
        finally
        {
            _messenger.Send(new DialogOpenChangedMessage(false));
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void NewConversation()
    {
        Session.NewConversation();
        SetSelectedConversation(null);
        _messenger.Send(new CloseMoeViewMessage());
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateWorkspace()
    {
        var created = await _workspaceEditor.CreateAsync();
        if (created is null) return;
        RebuildWorkspaces(_lastRows);
        foreach (var workspace in Workspaces.Where(item => item.DefinitionId == created.Id))
            workspace.IsExpanded = true;
        // 建完工作区直接进入它的新会话页
        StartConversationInWorkspace(created.Directory);
    }

    private bool CanCreate() => IsConversationsLoaded;
}