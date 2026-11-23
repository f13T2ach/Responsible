using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Responsible.ExView;
using Responsible.Localization;
using Responsible.Messages;
using Responsible.MoeViews;
using Responsible.MoeViews.Plugins;
using Responsible.Services;
using Responsible.Services.Dsh;
using Responsible.Infrastructure;
using Responsible.View;
using Responsible.ViewModel;
using System.IO;
using Wpf.Ui;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using INavigationService = Responsible.Services.INavigationService;
using NavigationService = Responsible.Services.NavigationService;

namespace Responsible
{
    public partial class App : Application
    {
        public IServiceProvider Services { get; private set; } = null!;

        private CrashTray? _crashTray;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();
            Loc.Initialize();

            Services.GetRequiredService<IAppThemeService>();
            // 提前实例化 RVM 服务：它会在构造时清掉上次崩溃遗留的 subst 盘符映射
            Services.GetRequiredService<IRvmEngineService>();
            _crashTray = CrashTray.Start(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Responsible"));
            var window = Services.GetRequiredService<MainWindow>();
            window.Show();
            Services.GetRequiredService<IAppThemeService>().Attach(window);
            Services.GetRequiredService<INavigationService>().NavigateTo<MoeViewModel>();
            _ = RunStartupChecksAsync(window);
        }

        // 启动检查在后台执行，失效项在主界面出现后以对话框列出。
        private async Task RunStartupChecksAsync(Window window)
        {
            IReadOnlyList<StartupCheckItem> issues;
            try
            {
                issues = await Task.Run(() =>
                    Services.GetRequiredService<IStartupCheckService>().RunAsync());
            }
            catch (Exception ex)
            {
                issues = [new StartupCheckItem(Loc.T("L.Startup.Title"), "Startup check did not complete", ex.Message)];
            }
            if (issues.Count == 0) return;

            // 等首帧布局完成后再弹窗；此时 Moe 页面与 WebView 已建好，
            // 折叠消息（或 DialogOpenState）能真正压住 WebView2 的空域。
            await window.Dispatcher.InvokeAsync(async () =>
            {
                var messenger = Services.GetRequiredService<IMessenger>();
                DialogOpenState.IsOpen = true;
                messenger.Send(new DialogOpenChangedMessage(true));
                try
                {
                    await Services.GetRequiredService<IContentDialogService>()
                        .ShowAsync(BuildStartupDialog(issues), CancellationToken.None);
                }
                finally
                {
                    DialogOpenState.IsOpen = false;
                    messenger.Send(new DialogOpenChangedMessage(false));
                }
            }, DispatcherPriority.ApplicationIdle).Task.Unwrap();
        }

        private static ContentDialog BuildStartupDialog(IReadOnlyList<StartupCheckItem> issues)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = Loc.F("L.Startup.Summary", issues.Count),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });

            foreach (var issue in issues)
            {
                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text = issue.Category,
                    FontSize = 11,
                    Foreground = Application.Current.TryFindResource("TextFillColorTertiaryBrush") as Brush,
                });
                content.Children.Add(new TextBlock
                {
                    Text = issue.Title,
                    FontSize = 13.5,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                });
                content.Children.Add(new TextBlock
                {
                    Text = issue.Detail,
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0),
                    Foreground = Application.Current.TryFindResource("TextFillColorSecondaryBrush") as Brush,
                });
                panel.Children.Add(new Border
                {
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 8),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Application.Current.TryFindResource("ControlStrokeColorDefaultBrush") as Brush,
                    Background = Application.Current.TryFindResource("ControlFillColorSecondaryBrush") as Brush,
                    Child = content,
                });
            }

            return new ContentDialog
            {
                Title = Loc.T("L.Startup.FailedTitle"),
                Content = new ScrollViewer
                {
                    Content = panel,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = 400,
                },
                CloseButtonText = Loc.T("L.Common.GotIt"),
                DialogWidth = 540,
                DialogHeight = 480,
            };
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<IModelCatalogService, ModelCatalogService>();
            services.AddSingleton<IWorkspaceChangeTracker, WorkspaceChangeTracker>();
        services.AddSingleton<ApprovalBridge>();
        services.AddSingleton<QuestionBridge>();
            services.AddSingleton<PluginOverridesStore>();
            services.AddSingleton<IWorkspaceStore, WorkspaceStore>();
            services.AddSingleton<IChatSessionService, ChatSessionService>();
            services.AddSingleton<IDshKernelService, DshKernelService>();
            services.AddSingleton<IMoeViewNavigator, MoeViewNavigator>();
            services.AddSingleton<IDshPluginService, DshPluginService>();
            services.AddSingleton<IAppThemeService, AppThemeService>();
            services.AddSingleton<IWinFspService, WinFspService>();
            services.AddSingleton<IAclAuthorizationService, AclAuthorizationService>();
            services.AddSingleton<IRvmEngineService, RvmEngineService>();
            services.AddSingleton<IRvmPolicyService, RvmPolicyService>();
            services.AddSingleton<IStartupCheckService, StartupCheckService>();
            services.AddSingleton<IStartupCheck, AclValidityStartupCheck>();
            services.AddSingleton<IStartupCheck, RvmEngineStartupCheck>();
            services.AddSingleton<IContentDialogService, ContentDialogService>();
            services.AddSingleton<IProviderEditorService, ProviderEditorService>();
            services.AddSingleton<IWorkspaceEditorService, WorkspaceEditorService>();
            services.AddSingleton<IWorkspacePreparation, WorkspacePreparation>();

            services.AddSingleton<MainWindow>();
            services.AddSingleton<MainWindowViewModel>();

            services.AddTransient<MoeViewModel>();
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<SettingsExView>();
            services.AddTransient<ProviderEditorViewModel>();
            services.AddTransient<ProviderEditorView>();
            services.AddTransient<WorkspaceEditorViewModel>();
            services.AddTransient<WorkspaceEditorView>();
            services.AddTransient<PluginsManagerViewModel>();
            services.AddTransient<PluginsManagerView>();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 引擎收尾可能因超时/强杀漏掉 subst 卸载，这里在容器释放前补一刀
            try
            {
                if (Services.GetService(typeof(IRvmEngineService)) is IRvmEngineService rvm)
                    rvm.CleanUpDriveMappings();
            }
            catch (ObjectDisposedException)
            {
            }
            (Services as IDisposable)?.Dispose();
            // 收尾都做完了再告别：RTray 收到告别就只记录、不再插手清理
            _crashTray?.SayGoodbye();
            base.OnExit(e);
        }
    }
}