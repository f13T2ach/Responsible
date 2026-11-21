using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Responsible.Messages;

public sealed class OpenExViewMessage(Type exViewType) : ValueChangedMessage<Type>(exViewType);

public sealed class CloseExViewMessage;

// WebView2 是 HWND 子窗口，永远盖在 WPF 覆盖层之上；ExView 打开期间宿主需折叠 Web 才能露出卡片
public sealed class ExViewOpenChangedMessage(bool isOpen) : ValueChangedMessage<bool>(isOpen);