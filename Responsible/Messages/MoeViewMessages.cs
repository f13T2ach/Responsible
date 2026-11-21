using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Responsible.Messages;

// Moe 内容区页面导航：按 ViewModel 类型打开，宿主用 MoeViewAttribute 解析对应视图
public sealed class OpenMoeViewMessage(Type viewModelType) : ValueChangedMessage<Type>(viewModelType);

public sealed class CloseMoeViewMessage;

// 内容区页面打开期间 WebView2 必须折叠（HWND 空域），输入框也随页面隐藏
public sealed class MoeViewOpenChangedMessage(bool isOpen) : ValueChangedMessage<bool>(isOpen);