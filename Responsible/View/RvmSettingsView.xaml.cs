using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Responsible.ViewModel;

namespace Responsible.View;

public partial class RvmSettingsView : UserControl
{
    public RvmSettingsView() => InitializeComponent();

    private void OnAddTool(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择允许 AI 使用的工具目录" };
        if (dialog.ShowDialog() != true) return;
        (DataContext as RvmSettingsViewModel)?.AddTool(dialog.FolderName);
    }
}