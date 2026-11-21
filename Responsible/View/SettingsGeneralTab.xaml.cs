using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Responsible.ViewModel;

namespace Responsible.View;

public partial class SettingsGeneralTab : UserControl
{
    public SettingsGeneralTab() => InitializeComponent();

    private void OnAddAclTool(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要一次性授权给 RVM 的工具目录" };
        if (dialog.ShowDialog() != true)
            return;

        (DataContext as SettingsViewModel)?.AddAclTool(dialog.FolderName);
    }
}