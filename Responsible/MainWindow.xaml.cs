using Responsible.ViewModel;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Responsible
{
    public partial class MainWindow : FluentWindow
    {
        public MainWindow(MainWindowViewModel viewModel, IContentDialogService contentDialogs)
        {
            DataContext = viewModel;
            InitializeComponent();
            contentDialogs.SetDialogHost(DialogHost);
        }
    }
}