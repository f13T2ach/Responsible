using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Responsible.Messages;
using Responsible.MoeViews;

namespace Responsible.Controls;

// Moe 内容区页面宿主：整块覆盖历史区与输入区，不弹窗；页面自 MoeViews 工程按 MoeViewAttribute 解析
public class MoeViewHost : ContentControl
{
    private FrameworkElement? _current;

    public MoeViewHost()
    {
        Visibility = Visibility.Collapsed;
        WeakReferenceMessenger.Default.Register<OpenMoeViewMessage>(
            this, static (recipient, message) => ((MoeViewHost)recipient).Open(message.Value));
        WeakReferenceMessenger.Default.Register<CloseMoeViewMessage>(
            this, static (recipient, _) => ((MoeViewHost)recipient).Close());
    }

    private void Open(Type viewModelType)
    {
        if (_current is not null)
            return;

        var viewType = viewModelType.GetCustomAttribute<MoeViewAttribute>()?.ViewType;
        if (viewType is null)
            return;
        if (((App)Application.Current).Services.GetService(viewType) is not FrameworkElement view)
            return;

        _current = view;
        Content = view;
        Visibility = Visibility.Visible;
        WeakReferenceMessenger.Default.Send(new MoeViewOpenChangedMessage(true));
        Dispatcher.BeginInvoke(() => view.Focus());
    }

    private void Close()
    {
        if (_current is null)
            return;

        _current = null;
        Content = null;
        Visibility = Visibility.Collapsed;
        WeakReferenceMessenger.Default.Send(new MoeViewOpenChangedMessage(false));
    }
}