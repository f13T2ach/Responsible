using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Web.WebView2.Core;
using Responsible.Infrastructure;
using Responsible.Localization;
using Responsible.Messages;
using Responsible.Services;
using Wpf.Ui;
using Wpf.Ui.Appearance;

namespace Responsible.Controls;

// WebView2 宿主：背景色与内容区同源（导航前写 DefaultBackgroundColor），会话事件双向桥接 RWebView
public partial class ChatWebView : UserControl
{
    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(
        nameof(Session),
        typeof(IChatSessionService),
        typeof(ChatWebView),
        new PropertyMetadata(null, OnSessionChanged));

    private IChatSessionService? _session;
    private bool _initialized;
    private bool _fallback;
    private bool _exViewOpen;
    private bool _moeViewOpen;
    private bool _dialogOpen;

    public ChatWebView()
    {
        InitializeComponent();
        // 启动早期的对话框可能早于本控件创建，补读状态避免 WebView2 盖住弹窗
        _dialogOpen = DialogOpenState.IsOpen;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        WeakReferenceMessenger.Default.Register<ExViewOpenChangedMessage>(
            this, static (r, m) => ((ChatWebView)r).OnExViewOpenChanged(m.Value));
        WeakReferenceMessenger.Default.Register<MoeViewOpenChangedMessage>(
            this, static (r, m) => ((ChatWebView)r).OnMoeViewOpenChanged(m.Value));
        WeakReferenceMessenger.Default.Register<DialogOpenChangedMessage>(
            this, static (r, m) => ((ChatWebView)r).OnDialogOpenChanged(m.Value));
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(
            this, static (r, _) => ((ChatWebView)r).OnLanguageChanged());
        WeakReferenceMessenger.Default.Register<ApprovalPendingMessage>(
            this, static (r, m) => ((ChatWebView)r).OnApprovalPending(m.Value));
    }

    /// <summary>前端点了改动卡片或其中某个文件。</summary>
    public event EventHandler<DiffRequest>? DiffRequested;

    public IChatSessionService? Session
    {
        get => (IChatSessionService?)GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    private static void OnSessionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (ChatWebView)d;
        if (e.OldValue is IChatSessionService old) self.Unsubscribe(old);
        self._session = e.NewValue as IChatSessionService;
        if (self._session is { } session && self.IsLoaded)
        {
            self.Subscribe(session);
            self.PostInit();
            self.RefreshFallback();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WebLog("loaded");
        _session ??= Session;
        if (_session is { } session) Subscribe(session);
        ApplicationThemeManager.Changed += OnThemeChanged;
        UpdateWebVisibility();
        RefreshFallback();
        _ = InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_session is { } session) Unsubscribe(session);
        ApplicationThemeManager.Changed -= OnThemeChanged;
        if (Web.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnWebMessage;
            Web.NavigationCompleted -= OnNavigationCompleted;
        }
        Web.Dispose();
        _initialized = false;
    }

    private async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Responsible", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await Web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception)
        {
            ShowFallback();
            return;
        }

        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.WebMessageReceived += OnWebMessage;
        // 前端报错默认看不见：把 window.onerror 与未处理 rejection 转发到宿主日志
        core.WebMessageReceived += (_, args) =>
        {
            var raw = args.TryGetWebMessageAsString();
            if (raw is { Length: > 0 } && raw.Contains("\"type\":\"clientError\"")) WebLog("client: " + raw);
        };
        Web.NavigationCompleted += OnNavigationCompleted;

        // 导航前落背景色：首帧即与内容区同色，避免白闪
        Web.DefaultBackgroundColor = ToDrawingColor(WebBridge.ParseHex(WebBridge.ResolvePalette().Bg));
        Web.NavigateToString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RWebView", "index.html")));
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) ShowFallback();
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var msg = WebBridge.Parse(e.TryGetWebMessageAsString());
        switch (msg?.Type)
        {
            case "ready":
                PostInit();
                break;
            case "copyText":
                try
                {
                    if (msg.Text is { Length: > 0 }) Clipboard.SetText(msg.Text);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // 剪贴板被其它进程占用时放弃本次复制
                }
                break;
            case "branch":
                if (msg.Id is { Length: > 0 }) _ = BranchAsync(msg.Id);
                break;
            case "stop":
                if (msg.Id is { Length: > 0 }) _session?.Stop(msg.Id);
                break;
            case "openLink":
                OpenExternal(msg.Href);
                break;
            case "files.request":
                _ = PushFilesAsync();
                break;
            case "approval.answer":
                if (msg.Id is { Length: > 0 } approvalId && msg.Text is { Length: > 0 } outcome)
                    WeakReferenceMessenger.Default.Send(new ApprovalAnsweredMessage(approvalId, outcome));
                break;
            case "showDiff":
                // 对话区把“看这个文件的 diff”交给宿主：右侧是独立 WebView，不是覆盖层
                DiffRequested?.Invoke(this, new DiffRequest(msg.Turn ?? 0, msg.Path, msg.Index));
                break;
        }
    }

    private async Task BranchAsync(string messageId)
    {
        if (_session is not { } session) return;
        BranchResult result;
        try
        {
            result = await session.BranchAsync(messageId);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            result = new BranchResult(false, $"Branch failed: {ex.Message}");
        }
        if (!result.Ok) Post(new HostToast(result.Message));
    }

    // 只放行 http(s) 与真实存在的本地路径；其余一律拒绝并提示
    private void OpenExternal(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return;
        try
        {
            if (Uri.TryCreate(href, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return;
            }
            var path = href.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(href, UriKind.Absolute, out var fileUri)
                ? fileUri.LocalPath
                : href;
            if (!Path.IsPathRooted(path))
                path = Path.Combine(_session?.WorkingDirectory ?? AppContext.BaseDirectory, path);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Post(new HostToast($"File not found: {path}"));
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UriFormatException or ArgumentException)
        {
            Post(new HostToast("Unable to open link"));
        }
    }

    private void OnLanguageChanged() => Ui(() => Post(new HostLocale(Loc.Language)));

    // 侧栏「工作区文件」：取会话工作区根目录，一次性推给前端
    private async Task PushFilesAsync()
    {
        if (_session is not { } session) return;
        var kernel = (Application.Current as App)?.Services.GetService(typeof(IDshKernelService)) as IDshKernelService;
        var bridge = kernel?.Bridge;
        if (bridge is null) return;
        try
        {
            var entries = await DshBridgeHttpClient.ReadFilesAsync(bridge, session.CurrentSessionId, CancellationToken.None)
                .ConfigureAwait(false);
            Ui(() => Post(new HostWorkspaceFiles(entries.Select(entry =>
                new WireFileEntry(entry.Name, entry.Dir, entry.Size)).ToArray())));
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException)
        {
        }
    }

    private void OnRunningTools(IReadOnlyList<RunningTool> tools) => Ui(() =>
        Post(new HostTasks(tools.Select(tool => new WireTask(tool.CallId, tool.Name, tool.Summary)).ToArray())));

    private void OnApprovalPending(IReadOnlyList<WireApproval> approvals) =>
        Ui(() => Post(new HostApprovalRequest(approvals)));

    private void OnThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent)
    {
        var palette = WebBridge.ResolvePalette();
        if (!_fallback) Web.DefaultBackgroundColor = ToDrawingColor(WebBridge.ParseHex(palette.Bg));
        Post(new HostTheme(WebBridge.ThemeOf(palette), palette));
    }

    private void Subscribe(IChatSessionService session)
    {
        session.Appended += OnAppended;
        session.MessageUpdated += OnMessageUpdated;
        session.Reloaded += OnReloaded;
        session.Delivered += OnDelivered;
        session.ReasoningDelivered += OnReasoningDelivered;
        session.Restarted += OnRestarted;
        session.Completed += OnCompleted;
        session.ChangesReady += OnChangesReady;
        session.RunningToolsChanged += OnRunningTools;
        session.StatsChanged += OnStatsChanged;
        session.RunningChanged += OnRunningChanged;
        session.SessionLoadingChanged += OnSessionLoadingChanged;
    }

    private void Unsubscribe(IChatSessionService session)
    {
        session.Appended -= OnAppended;
        session.MessageUpdated -= OnMessageUpdated;
        session.Reloaded -= OnReloaded;
        session.Delivered -= OnDelivered;
        session.ReasoningDelivered -= OnReasoningDelivered;
        session.Restarted -= OnRestarted;
        session.Completed -= OnCompleted;
        session.ChangesReady -= OnChangesReady;
        session.RunningToolsChanged -= OnRunningTools;
        session.StatsChanged -= OnStatsChanged;
        session.RunningChanged -= OnRunningChanged;
        session.SessionLoadingChanged -= OnSessionLoadingChanged;
    }

    private void OnAppended(ChatMessage message) => Ui(() =>
    {
        Post(new HostMessageAppend(ToWire(message)));
        RefreshFallback();
    });

    private void OnMessageUpdated(ChatMessage message) => Ui(() => Post(new HostMessageUpdate(ToWire(message))));

    // 切换持久化会话后整段重置：消息、统计与改动都来自新的 Session
    private void OnReloaded() => Ui(() =>
    {
        WebLog("reloaded");
        if (_session is not { } session) return;
        Post(new HostHistoryReset(session.Messages.Select(ToWire).ToArray()));
        Post(new HostStats(ToWire(session.Stats)));
        // 改动记录跟着历史一起重建，否则重启后“已编辑 n 个文件”会整片消失
        Post(new HostChangesReset(session.Changes.Select(ToWire).ToArray()));
        RefreshFallback();
    });

    private void OnDelivered(string id, string chunk) => Ui(() => Post(new HostMessageDelta(id, chunk)));

    private void OnReasoningDelivered(string id, string chunk) => Ui(() => Post(new HostReasoningDelta(id, chunk)));

    private void OnRestarted(string id) => Ui(() => Post(new HostMessageRestart(id)));

    private void OnCompleted(string id, string state) => Ui(() => Post(new HostMessageDone(id, state)));

    private void OnChangesReady(ChatChanges changes) => Ui(() => Post(new HostChangesAppend(ToWire(changes))));

    private void OnStatsChanged(ChatStats stats) => Ui(() => Post(new HostStats(ToWire(stats))));

    private void OnRunningChanged(bool running) => Ui(() => Post(new HostAgentState(running ? "running" : "idle")));

    private void OnSessionLoadingChanged(bool loading) => Ui(() =>
    {
        RefreshFallback();
        Post(new HostHistoryLoading(loading));
    });

    // ExView / Moe 页面覆盖层与 WebView2 存在 airspace：任一打开期间折叠 Web，全部关闭后恢复
    private void OnExViewOpenChanged(bool open) => Ui(() =>
    {
        _exViewOpen = open;
        UpdateWebVisibility();
    });

    private void OnMoeViewOpenChanged(bool open) => Ui(() =>
    {
        _moeViewOpen = open;
        UpdateWebVisibility();
    });

    private void OnDialogOpenChanged(bool open) => Ui(() =>
    {
        _dialogOpen = open;
        UpdateWebVisibility();
    });

    private void UpdateWebVisibility()
    {
        if (_fallback) return;
        Web.Visibility = _exViewOpen || _moeViewOpen || _dialogOpen
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private void PostInit()
    {
        if (_fallback || _session is not { } session) return;
        var palette = WebBridge.ResolvePalette();
        Post(new HostInit(
            WebBridge.ProtocolVersion,
            WebBridge.ThemeOf(palette),
            palette,
            session.Messages.Select(ToWire).ToArray(),
            ToWire(session.Stats),
            session.Changes.Select(ToWire).ToArray(),
            session.IsSessionLoading,
            Loc.Language));
    }

    private void Post(object message)
    {
        if (_fallback) { WebLog("post skipped: fallback"); return; }
        if (Web.CoreWebView2 is null) { WebLog("post skipped: core null"); return; }
        var script = WebBridge.ScriptFor(message);
        _ = Web.ExecuteScriptAsync(script).ContinueWith(task =>
        {
            if (task.IsFaulted) WebLog($"post failed: {task.Exception?.GetBaseException().Message}");
        }, TaskScheduler.Default);
        if (message is HostChangesReset reset) WebLog($"post changes.reset rows={reset.Changes.Count}");
    }

    // 前端渲染问题的唯一可观测线索：把 Post 的结果写进日志
    private static void WebLog(string line)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Responsible", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "webview.log"),
                $"{DateTimeOffset.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ShowFallback()
    {
        _fallback = true;
        Web.Visibility = Visibility.Collapsed;
        Fallback.Visibility = Visibility.Visible;
        RefreshFallback();
    }

    private void RefreshFallback()
    {
        if (!_fallback) return;
        var messages = _session?.Messages ?? [];
        var loading = _session?.IsSessionLoading == true && messages.Count == 0;
        FallbackLoading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        FallbackEmpty.Visibility = messages.Count == 0 && !loading ? Visibility.Visible : Visibility.Collapsed;
        FallbackScroll.Visibility = messages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        FallbackList.ItemsSource = messages;
    }

    private static WireMessage ToWire(ChatMessage m) =>
        new(m.Id, m.Role, m.Text, m.State, m.Ts, m.Model, m.Reasoning, m.ReasoningMs,
            ToWire(m.Usage), m.BranchSeq, m.BranchUnavailable,
            m.Tools is { Count: > 0 } tools ? tools.Select(ToWire).ToArray() : null,
            m.Parts is { Count: > 0 } parts
                ? parts.Select(part => new WirePart(
                    part.Kind,
                    part.Text,
                    part.Tool is null ? null : ToWire(part.Tool))).ToArray()
                : null);

    private static WireTool ToWire(ChatToolCall tool) =>
        new(tool.CallId,
            tool.Name,
            tool.Variant,
            tool.Summary,
            tool.State,
            tool.Ts,
            tool.Output,
            tool.Input,
            tool.IsError ? true : null);

    private static WireUsage? ToWire(ChatTurnUsage? usage) => usage is null ? null : new WireUsage(
        usage.UncachedInputTokens,
        usage.OutputTokens,
        usage.TotalTokens,
        usage.CacheReadTokens,
        usage.CacheWriteTokens,
        usage.ReasoningTokens,
        usage.Routes.Select(route => new WireUsageRoute(route.Provider, route.Model)).ToArray());

    internal static WireChanges ToWire(ChatChanges c) => new(
        c.Turn,
        c.Cwd,
        c.Total,
        c.Added,
        c.Deleted,
        c.Files.Select(f => new WireChangedFile(
            f.Path,
            f.Display,
            f.Added,
            f.Deleted,
            f.Binary ? true : null,
            f.Oversized ? true : null)).ToArray(),
        // diff 文本一起推给前端：点开文件就能直接看，不必再回宿主取
        c.Diffs.Count == 0 ? null : new Dictionary<string, string>(c.Diffs));

    private static WireStats ToWire(ChatStats s) =>
        new(s.Rounds, s.Steps, s.TokensPerSecond, s.TotalTokens, s.CacheHitRate, s.ContextPercent);

    private static System.Drawing.Color ToDrawingColor(System.Windows.Media.Color c) =>
        System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
}
