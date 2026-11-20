using CommunityToolkit.Mvvm.Messaging;
using Responsible.Messages;

namespace Responsible.Services;

public sealed class NavigationService(IMessenger messenger) : INavigationService
{
    public void NavigateTo<TViewModel>() where TViewModel : class =>
        messenger.Send(new NavigateMessage(typeof(TViewModel)));
}