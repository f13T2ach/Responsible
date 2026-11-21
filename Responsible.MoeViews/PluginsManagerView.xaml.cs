using System.Windows.Controls;
using System.Windows.Threading;
using Responsible.MoeViews.Plugins;

namespace Responsible.MoeViews;

public partial class PluginsManagerView : UserControl
{
    private readonly PluginsManagerViewModel _viewModel;

    public PluginsManagerView(PluginsManagerViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Dispatcher.BeginInvoke(() => _viewModel.LoadAsync(), DispatcherPriority.Background);
    }
}