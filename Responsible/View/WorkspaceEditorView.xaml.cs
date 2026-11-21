using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Responsible.ViewModel;

namespace Responsible.View;

public partial class WorkspaceEditorView : UserControl
{
    public WorkspaceEditorView() => InitializeComponent();

    private WorkspaceEditorViewModel? ViewModel => DataContext as WorkspaceEditorViewModel;

    private void OnBrowseFolders(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择工作区文件夹", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        foreach (var folder in dialog.FolderNames)
            ViewModel?.AddFolder(folder);
    }

    private void OnFoldersDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Link
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFoldersDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths)
            if (Directory.Exists(path))
                ViewModel?.AddFolder(path);
        e.Handled = true;
    }
}
