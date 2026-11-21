using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Web.WebView2.Core;
using Responsible.Infrastructure;
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

    public ChatWebView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        WeakReferenceMessenger.Default.Register<ExViewOpenChangedMessage>(
            this, static (r, m) => ((ChatWebView)r).OnExViewOpenChanged(m.Value));
    }

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
        _session ??= Session;
        if (_session is { } session) Subscribe(session);
        ApplicationThemeManager.Changed += OnThemeChanged;
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
            case "retry":
                if (msg.Id is { Length: > 0 }) _session?.Retry(msg.Id);
                break;
            case "stop":
                if (msg.Id is { Length: > 0 }) _session?.Stop(msg.Id);
                break;
        }
    }

    private void OnThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent)
    {
        var palette = WebBridge.ResolvePalette();
        if (!_fallback) Web.DefaultBackgroundColor = ToDrawingColor(WebBridge.ParseHex(palette.Bg));
        Post(new HostTheme(WebBridge.ThemeOf(palette), palette));
    }

    private void Subscribe(IChatSessionService session)
    {
        session.Appended += OnAppended;
        session.Delivered += OnDelivered;
        session.Restarted += OnRestarted;
        session.Completed += OnCompleted;
        session.StatsChanged += OnStatsChanged;
        session.RunningChanged += OnRunningChanged;
    }

    private void Unsubscribe(IChatSessionService session)
    {
        session.Appended -= OnAppended;
        session.Delivered -= OnDelivered;
        session.Restarted -= OnRestarted;
        session.Completed -= OnCompleted;
        session.StatsChanged -= OnStatsChanged;
        session.RunningChanged -= OnRunningChanged;
    }

    private void OnAppended(ChatMessage message) => Ui(() =>
    {
        Post(new HostMessageAppend(ToWire(message)));
        RefreshFallback();
    });

    private void OnDelivered(string id, string chunk) => Ui(() => Post(new HostMessageDelta(id, chunk)));

    private void OnRestarted(string id) => Ui(() => Post(new HostMessageRestart(id)));

    private void OnCompleted(string id, string state) => Ui(() => Post(new HostMessageDone(id, state)));

    private void OnStatsChanged(ChatStats stats) => Ui(() => Post(new HostStats(ToWire(stats))));

    private void OnRunningChanged(bool running) => Ui(() => Post(new HostAgentState(running ? "running" : "idle")));

    // ExView 覆盖层与 WebView2 存在 airspace：打开期间折叠 Web 露出 WPF 卡片，关闭淡出后恢复
    private void OnExViewOpenChanged(bool open) => Ui(() =>
    {
        if (_fallback) return;
        Web.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
    });

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
            ToWire(session.Stats)));
    }

    private void Post(object message)
    {
        if (_fallback || Web.CoreWebView2 is null) return;
        _ = Web.ExecuteScriptAsync(WebBridge.ScriptFor(message));
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
        FallbackEmpty.Visibility = messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FallbackScroll.Visibility = messages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        FallbackList.ItemsSource = messages;
    }

    private static WireMessage ToWire(ChatMessage m) => new(m.Id, m.Role, m.Text, m.State, m.Ts, m.Model);

    private static WireStats ToWire(ChatStats s) =>
        new(s.Rounds, s.Steps, s.TokensPerSecond, s.TotalTokens, s.CacheHitRate, s.ContextPercent);

    private static System.Drawing.Color ToDrawingColor(System.Windows.Media.Color c) =>
        System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
}
