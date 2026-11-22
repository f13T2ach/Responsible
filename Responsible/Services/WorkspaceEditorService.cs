using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Responsible.Messages;
using Responsible.View;
using Responsible.ViewModel;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Responsible.Services;

public sealed class WorkspaceEditorService(
    IContentDialogService dialogs,
    IWorkspaceStore store,
    IServiceProvider services,
    IMessenger messenger) : IWorkspaceEditorService
{
    public async Task<WorkspaceDefinition?> CreateAsync()
    {
        var viewModel = services.GetRequiredService<WorkspaceEditorViewModel>();
        viewModel.Reset();

        var view = services.GetRequiredService<WorkspaceEditorView>();
        view.DataContext = viewModel;

        var dialog = new ContentDialog
        {
            Content = view,
            DialogWidth = 700,
            DialogHeight = 620,
            DialogMaxHeight = 660,
            IsFooterVisible = false,
        };
        viewModel.SaveRequested += (_, _) => dialog.Hide(ContentDialogResult.Primary);
        viewModel.CancelRequested += (_, _) => dialog.Hide(ContentDialogResult.None);

        ContentDialogResult result;
        // 对话框在 WebView2 之上会被 HWND 空域遮住，弹出期间折叠 Web
        DialogOpenState.IsOpen = true;
        messenger.Send(new DialogOpenChangedMessage(true));
        try
        {
            result = await dialogs.ShowAsync(dialog, CancellationToken.None);
        }
        finally
        {
            DialogOpenState.IsOpen = false;
            messenger.Send(new DialogOpenChangedMessage(false));
        }
        if (result != ContentDialogResult.Primary)
            return null;

        var created = viewModel.Build();
        store.Add(created);
        return created;
    }
}
