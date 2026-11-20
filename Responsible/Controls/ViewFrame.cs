using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Responsible.Messages;

namespace Responsible.Controls;

public class ViewFrame : ContentControl
{
    public ViewFrame()
    {
        WeakReferenceMessenger.Default.Register<NavigateMessage>(
            this,
            static (recipient, message) => ((ViewFrame)recipient).OnNavigated(message));
    }

    private void OnNavigated(NavigateMessage message) =>
        Content = ((App)Application.Current).Services.GetRequiredService(message.Value);
}