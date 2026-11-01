using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Backspace.Services;

namespace Backspace.Views;

public partial class FileWaitPanel : UserControl
{
    public FileWaitPanel() { InitializeComponent(); IsVisible = false; }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FileAccessRetry.Changed += OnChanged;
        Refresh();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        FileAccessRetry.Changed -= OnChanged;
        base.OnDetachedFromVisualTree(e);
    }
    private void OnChanged() => Dispatcher.UIThread.Post(Refresh);
    private void Refresh()
    {
        var notices = FileAccessRetry.Snapshot();
        Notices.ItemsSource = notices;
        IsVisible = notices.Length > 0;
    }
}
