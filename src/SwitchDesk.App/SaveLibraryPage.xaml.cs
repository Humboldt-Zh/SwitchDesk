using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SwitchDesk.Core;

namespace SwitchDesk.App;

public partial class SaveLibraryPage : UserControl
{
    private Point dragOrigin;
    private Snapshot? dragSnapshot;
    public SaveLibraryPage() => InitializeComponent();
    public void FocusSearch()
    {
        if (DataContext is ShellViewModel shell) shell.SaveLibrary.SelectedGame = null;
        SaveSearchBox.Focus(); SaveSearchBox.SelectAll();
    }
    private void OnHistoryMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragSnapshot = null; dragOrigin = e.GetPosition(SaveTimeline);
        var current = e.OriginalSource as DependencyObject;
        while (current != null)
        {
            if (current is Button) return;
            if (current is FrameworkElement { DataContext: SaveLibraryVersion row }) { dragSnapshot = row.Snapshot; return; }
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
    }
    private void OnHistoryMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragSnapshot is not {} snapshot) return;
        var point = e.GetPosition(SaveTimeline);
        if (Math.Abs(point.X - dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragSnapshot = null;
        try
        {
            if (!File.Exists(snapshot.Path)) throw new IOException("此备份文件已移动或删除，请刷新存档");
            VirtualFileDrag.Start(async () => (await SnapshotStore.VerifyAsync(snapshot.Path)).Path, PathSafety.SafeName(snapshot.Name) + ".zip");
            e.Handled = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Runtime.InteropServices.COMException)
        { if (DataContext is ShellViewModel shell) shell.Notify(ex.Message); }
    }
}
