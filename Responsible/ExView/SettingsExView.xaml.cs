using System.Windows.Controls;
using Responsible.ViewModel;

namespace Responsible.ExView;

public partial class SettingsExView : UserControl, IResponsibleExView
{
    public SettingsExView(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    public void OnOpened()
    {
    }

    public void OnClosed()
    {
    }
}