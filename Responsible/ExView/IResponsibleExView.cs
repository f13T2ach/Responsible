namespace Responsible.ExView;

// 自实现弹出视图契约：ExViewHost 经 OpenExViewMessage 解析承载，生命周期由宿主回调
public interface IResponsibleExView
{
    void OnOpened();

    void OnClosed();
}