using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Responsible.Controls;

// 受 RVM 保护会话里的快捷控件：绿色按钮打开本区 RVM 设置，
// 悬停时在上方浮出“同步 / 撤销”面板。
//
// 面板用 Popup + 关闭延时：鼠标从按钮移到面板上时，控件本身已经不算 IsMouseOver，
// 直接绑 IsMouseOver 会让面板在指针经过间隙的瞬间消失，按钮也就点不到了。
public partial class RvmWorkspaceControls : UserControl
{
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(260);

    public static readonly DependencyProperty OpenSettingsCommandProperty =
        DependencyProperty.Register(nameof(OpenSettingsCommand), typeof(ICommand),
            typeof(RvmWorkspaceControls), new PropertyMetadata(null));

    public static readonly DependencyProperty SyncCommandProperty =
        DependencyProperty.Register(nameof(SyncCommand), typeof(ICommand),
            typeof(RvmWorkspaceControls), new PropertyMetadata(null));

    public static readonly DependencyProperty RollbackCommandProperty =
        DependencyProperty.Register(nameof(RollbackCommand), typeof(ICommand),
            typeof(RvmWorkspaceControls), new PropertyMetadata(null));

    private DispatcherTimer? _closeTimer;

    public RvmWorkspaceControls()
    {
        InitializeComponent();
        MouseEnter += (_, _) => ShowPanel();
        MouseLeave += (_, _) => ScheduleHide();
        Unloaded += (_, _) => HoverPopup.IsOpen = false;
        Loaded += (_, _) =>
        {
            if (HoverPopup.Child is not UIElement child) return;
            child.MouseEnter += (_, _) => CancelHide();
            child.MouseLeave += (_, _) => ScheduleHide();
        };
    }

    public ICommand? OpenSettingsCommand
    {
        get => (ICommand?)GetValue(OpenSettingsCommandProperty);
        set => SetValue(OpenSettingsCommandProperty, value);
    }

    public ICommand? SyncCommand
    {
        get => (ICommand?)GetValue(SyncCommandProperty);
        set => SetValue(SyncCommandProperty, value);
    }

    public ICommand? RollbackCommand
    {
        get => (ICommand?)GetValue(RollbackCommandProperty);
        set => SetValue(RollbackCommandProperty, value);
    }

    private void ShowPanel()
    {
        CancelHide();
        HoverPopup.IsOpen = true;
    }

    private void CancelHide()
    {
        _closeTimer?.Stop();
        _closeTimer = null;
    }

    private void ScheduleHide()
    {
        CancelHide();
        _closeTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = CloseDelay };
        _closeTimer.Tick += (_, _) =>
        {
            CancelHide();
            if (IsMouseOver) return;
            if (HoverPopup.Child is UIElement child && child.IsMouseOver) return;
            HoverPopup.IsOpen = false;
        };
        _closeTimer.Start();
    }
}
