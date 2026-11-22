using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Responsible.Localization;
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
    IMessenger messenger,
    IWorkspacePreparation preparation,
    IChatSessionService session) : IWorkspaceEditorService
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
        // 勾了隔离保护，但目录没有 Git 索引：会话盘建不出来。与其落进“保护了但用不了”
        // 的状态，不如按未受保护保存并说明原因——用户建好索引后再打开即可。
        if (created.RvmProtected && created.Folders.Count > 0)
        {
            var index = await RvmWorkspaceIndex.InspectAsync(created.Folders[0]);
            if (!index.IsReady)
            {
                created = created with { RvmProtected = false };
                store.Add(created);
                await ShowPrepareFailureAsync(
                    $"{index.Message} The workspace was saved without RVM isolation; "
                    + "create the Git index for it first, then enable isolation again.");
                return created;
            }
        }
        store.Add(created);

        // 勾了隔离保护就把会话盘先建好：否则用户点完创建会以为准备好了，
        // 真正发第一条消息时才在后台默默播种。
        if (created.RvmProtected && created.Folders.Count > 0)
        {
            var prepared = await preparation.RunAsync(
                Loc.T("L.Workspace.Preparing"),
                Loc.T("L.Workspace.PreparingDetail"),
                () => session.PrepareRvmWorkspaceAsync(created.Folders[0]));
            if (!prepared.Ok) await ShowPrepareFailureAsync(prepared.Message);
        }
        return created;
    }

    private Task ShowPrepareFailureAsync(string message) =>
        dialogs.ShowAsync(
            new ContentDialog
            {
                Title = Loc.T("L.Workspace.PrepareFailed"),
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(message)
                        ? Loc.T("L.Session.RvmUnavailable")
                        : message,
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                },
                CloseButtonText = Loc.T("L.Common.GotIt"),
                DialogWidth = 460,
                DialogHeight = 260,
            },
            CancellationToken.None);
}
