using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Responsible.Localization;
using Responsible.ViewModel;

namespace Responsible.View;

public partial class SettingsRvmTab : UserControl
{
    public SettingsRvmTab() => InitializeComponent();

    private void OnAddAclTool(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("L.Common.SelectToolFolder") };
        if (dialog.ShowDialog() != true)
            return;

        (DataContext as SettingsViewModel)?.AddAclTool(dialog.FolderName);
    }
}