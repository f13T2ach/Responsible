using CommunityToolkit.Mvvm.Messaging;
using Responsible.Messages;
using Responsible.MoeViews;

namespace Responsible.Services;

public sealed class MoeViewNavigator(IMessenger messenger) : IMoeViewNavigator
{
    public void CloseCurrent() => messenger.Send(new CloseMoeViewMessage());
}