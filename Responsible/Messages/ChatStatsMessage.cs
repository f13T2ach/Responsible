using CommunityToolkit.Mvvm.Messaging.Messages;
using Responsible.Services;

namespace Responsible.Messages;

public sealed class ChatStatsMessage(ChatStats stats) : ValueChangedMessage<ChatStats>(stats);
