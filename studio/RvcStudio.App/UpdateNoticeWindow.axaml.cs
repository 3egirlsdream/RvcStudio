using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace RvcStudio.App;

public partial class UpdateNoticeWindow : Window
{
    public UpdateNoticeWindow() : this(AppUpdateResult.NotAvailable(new Version(1, 0, 0)))
    {
    }

    public UpdateNoticeWindow(AppUpdateResult update)
    {
        InitializeComponent();
        CurrentVersionText.Text = update.CurrentVersionText;
        AvailableVersionText.Text = update.AvailableVersionText;
        MemoText.Text = string.IsNullOrWhiteSpace(update.Memo) ? "暂无更新说明。" : update.Memo;
    }

    private async void CopyGroup_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(AppUpdateService.QqGroupNumber);
        CopyButton.Content = "已复制";
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
