using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Responsible.Messages;

// ContentDialog 宿主渲染在 WebView2 之下；对话框打开期间必须折叠 Web 才能看到弹窗
public sealed class DialogOpenChangedMessage(bool isOpen) : ValueChangedMessage<bool>(isOpen);

// WebView 可能晚于消息创建（启动早期的对话框），初始化时从这里补读当前状态，
// 否则新创建的 WebView2 会以可见状态盖住已经打开的对话框。
public static class DialogOpenState
{
    public static bool IsOpen { get; set; }
}
