namespace Responsible.MoeViews;

// Moe 内容区页面共用的导航契约：关闭当前页由宿主实现，页面不依赖主工程的消息类型。
public interface IMoeViewNavigator
{
    void CloseCurrent();
}