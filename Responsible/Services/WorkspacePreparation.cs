using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Responsible.Messages;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Responsible.Services;

/// <summary>
/// 会话盘播种要几秒到几十秒，期间给用户一个不可取消的进度对话框；
/// 对话框在 WebView2 之上会被空域遮住，所以同时折叠 Web 内容。
/// </summary>
public interface IWorkspacePreparation
{
    Task<PermissionResult> RunAsync(string title, string message, Func<Task<PermissionResult>> work);
}

public sealed class WorkspacePreparation(IContentDialogService dialogs, IMessenger messenger)
    : IWorkspacePreparation
{
    public async Task<PermissionResult> RunAsync(
        string title, string message, Func<Task<PermissionResult>> work)
    {
        var content = new StackPanel { Orientation = Orientation.Vertical };
        content.Children.Add(new ProgressRing
        {
            IsIndeterminate = true,
            Width = 32,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            IsFooterVisible = false,
            DialogWidth = 460,
            DialogHeight = 240,
        };

        DialogOpenState.IsOpen = true;
        messenger.Send(new DialogOpenChangedMessage(true));
        var shown = dialogs.ShowAsync(dialog, CancellationToken.None);
        try
        {
            // 先让对话框上屏，再开始占 CPU/IO 的准备工作
            await Task.Delay(80);
            return await work();
        }
        finally
        {
            dialog.Hide();
            try
            {
                await shown;
            }
            catch (InvalidOperationException)
            {
                // 宿主正在关闭时对话框可能已被移除
            }
            DialogOpenState.IsOpen = false;
            messenger.Send(new DialogOpenChangedMessage(false));
        }
    }
}
