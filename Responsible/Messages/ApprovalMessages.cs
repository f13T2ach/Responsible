using CommunityToolkit.Mvvm.Messaging.Messages;
using Responsible.Infrastructure;

namespace Responsible.Messages;

// 待用户决定的审批问询（已在宿主侧过滤掉 RVM 会话与非当前会话）
public sealed class ApprovalPendingMessage(IReadOnlyList<WireApproval> approvals)
    : ValueChangedMessage<IReadOnlyList<WireApproval>>(approvals);

// 前端点了允许/拒绝，宿主据此回写内核
public sealed class ApprovalAnsweredMessage(string id, string outcome)
    : ValueChangedMessage<(string Id, string Outcome)>((id, outcome));
