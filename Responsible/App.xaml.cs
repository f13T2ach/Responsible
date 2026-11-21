using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Responsible.ExView;
using Responsible.MoeViews;
using Responsible.MoeViews.Plugins;
using Responsible.Services;
using Responsible.View;
using Responsible.ViewModel;
using Wpf.Ui;
using INavigationService = Responsible.Services.INavigationService;
using NavigationService = Responsible.Services.NavigationService;

namespace Responsible
{
    public partial class App : Application
    {
        public IServiceProvider Services { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            Services.GetRequiredService<IAppThemeService>();
            var window = Services.GetRequiredService<MainWindow>();
            window.Show();
            Services.GetRequiredService<IAppThemeService>().Attach(window);
            Services.GetRequiredService<INavigationService>().NavigateTo<MoeViewModel>();
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<IModelCatalogService, ModelCatalogService>();
            services.AddSingleton<IWorkspaceChangeTracker, WorkspaceChangeTracker>();
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
            services.AddSingleton<IContentDialogService, ContentDialogService>();
            services.AddSingleton<IProviderEditorService, ProviderEditorService>();
            services.AddSingleton<IWorkspaceEditorService, WorkspaceEditorService>();

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
            (Services as IDisposable)?.Dispose();
            base.OnExit(e);
        }
    }
}