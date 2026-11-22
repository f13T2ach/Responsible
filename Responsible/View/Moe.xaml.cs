using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Responsible.Controls;
using Responsible.Infrastructure;
using Responsible.ViewModel;
using Wpf.Ui.Controls;

namespace Responsible.View
{
    public partial class Moe : UserControl
    {
        private const int WmNchitTest = 0x0084;
        private const int HtClient = 1;

        public Moe()
        {
            InitializeComponent();
            HostTitleBar.WndProcInvoked += OnTitleBarWndProc;
        }

        // 对话区里点了“已编辑 n 个文件”或其中某个文件：展开右侧列并把这一轮的改动交给它。
        // Path 为空表示看这一轮的第一个文件。
        private void OnDiffRequested(object? sender, DiffRequest request)
        {
            if (DataContext is not MoeViewModel viewModel) return;
            var changes = viewModel.Session.Changes
                .FirstOrDefault(item => item.Turn == request.Turn);
            if (changes is null) return;
            viewModel.Changes.Load(changes);
            viewModel.Changes.Selected = viewModel.Changes.Files
                .FirstOrDefault(file => file.Path == request.Path) ?? viewModel.Changes.Selected;
            viewModel.IsDiffOpen = true;
        }

        // 列表项自己消费 MouseDown 做选中，需要在这里接 Up 才能覆盖“重复点击当前对话”
        private void OnConversationClicked(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBox listBox
                || e.OriginalSource is not DependencyObject source
                || ItemsControl.ContainerFromElement(listBox, source) is not ListBoxItem item
                || DataContext is not MoeViewModel viewModel)
            {
                return;
            }

            viewModel.OpenConversationCommand.Execute(item.DataContext as ConversationItemViewModel);
        }

        // TitleBar 的命中钩子默认把整条标题栏判为 HTCAPTION 拖拽区，叠加其上的菜单簇与搜索控件需在此放行
        private void OnTitleBarWndProc(object? sender, HwndProcEventArgs e)
        {
            if (e.Message != WmNchitTest)
            {
                return;
            }

            long lParam = e.LParam.ToInt64();
            var screenPoint = new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
            Point local = ToolbarOverlay.PointFromScreen(screenPoint);

            // 菜单簇整块放行（含项间空隙），其余靠命中测试
            var clusterBounds = new Rect(
                LeftCluster.TranslatePoint(new Point(0, 0), ToolbarOverlay),
                new Size(LeftCluster.ActualWidth, LeftCluster.ActualHeight));

            if (clusterBounds.Contains(local)
                || (ToolbarOverlay.InputHitTest(local) is DependencyObject hit && hit != ToolbarOverlay))
            {
                e.ReturnValue = (nint)HtClient;
                e.Handled = true;
            }
        }
    }
}