using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Responsible.Messages;

// ContentDialog 宿主渲染在 WebView2 之下；对话框打开期间必须折叠 Web 才能看到弹窗
public sealed class DialogOpenChangedMessage(bool isOpen) : ValueChangedMessage<bool>(isOpen);
