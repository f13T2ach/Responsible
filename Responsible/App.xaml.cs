using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Responsible.ExView;
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
            services.AddSingleton<IChatSessionService, ChatSessionService>();
            services.AddSingleton<IDshKernelService, DshKernelService>();
            services.AddSingleton<IAppThemeService, AppThemeService>();
            services.AddSingleton<IContentDialogService, ContentDialogService>();
            services.AddSingleton<IProviderEditorService, ProviderEditorService>();

            services.AddSingleton<MainWindow>();
            services.AddSingleton<MainWindowViewModel>();

            services.AddTransient<MoeViewModel>();
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<SettingsExView>();
            services.AddTransient<ProviderEditorViewModel>();
            services.AddTransient<ProviderEditorView>();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            (Services as IDisposable)?.Dispose();
            base.OnExit(e);
        }
    }
}