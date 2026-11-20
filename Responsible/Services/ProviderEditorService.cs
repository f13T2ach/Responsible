using Microsoft.Extensions.DependencyInjection;
using Responsible.View;
using Responsible.ViewModel;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Responsible.Services;

public interface IProviderEditorService
{
    Task EditAsync(ProviderViewModel? existing);
}

public sealed class ProviderEditorService(
    IContentDialogService dialogs,
    IModelCatalogService catalog,
    IServiceProvider services) : IProviderEditorService
{
    public async Task EditAsync(ProviderViewModel? existing)
    {
        var viewModel = services.GetRequiredService<ProviderEditorViewModel>();
        viewModel.Load(existing);

        var view = services.GetRequiredService<ProviderEditorView>();
        view.DataContext = viewModel;

        var dialog = new ContentDialog
        {
            Content = view,
            DialogWidth = 780,
            DialogHeight = 720,
            DialogMaxHeight = 760,
            IsFooterVisible = false,
        };
        viewModel.SaveRequested += (_, _) => dialog.Hide(ContentDialogResult.Primary);
        viewModel.CancelRequested += (_, _) => dialog.Hide(ContentDialogResult.None);

        var result = await dialogs.ShowAsync(dialog, CancellationToken.None);
        if (result != ContentDialogResult.Primary)
            return;

        var built = viewModel.BuildProvider();
        if (existing != null && built.Id != existing.Id)
            catalog.Remove(existing);
        catalog.AddOrUpdate(built);
    }
}