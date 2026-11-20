using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Responsible.Messages;

public sealed class NavigateMessage(Type viewModelType) : ValueChangedMessage<Type>(viewModelType);