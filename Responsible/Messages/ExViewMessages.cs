using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Responsible.Messages;

public sealed class OpenExViewMessage(Type exViewType) : ValueChangedMessage<Type>(exViewType);

public sealed class CloseExViewMessage;