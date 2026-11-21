using CommunityToolkit.Mvvm.Messaging.Messages;
using Responsible.Services;

namespace Responsible.Messages;

public sealed class ConversationsChangedMessage(IReadOnlyList<DshSessionRow> rows)
    : ValueChangedMessage<IReadOnlyList<DshSessionRow>>(rows);
