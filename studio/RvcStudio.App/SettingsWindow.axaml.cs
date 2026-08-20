using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace RvcStudio.App;

public partial class SettingsWindow : Window
{
    private readonly AppUpdateService _updateService = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _isChecking;
    private bool _isClosed;

    public SettingsWindow()
    {
        InitializeComponent();
        CurrentVersionText.Text = AppUpdateResult
            .NotAvailable(AppUpdateService.CurrentVersion)
            .CurrentVersionText;
    }

    private async void CheckUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (_isChecking || _isClosed) return;

        _isChecking = true;
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesButtonText.Text = "正在检查…";
        UpdateProgress.IsVisible = true;
        SetUpdateStatus("正在连接更新服务器…", "#B7F36B", "#35452E");

        try
        {
            var update = await _updateService.CheckAsync(_lifetimeCts.Token);
            if (_isClosed) return;

            if (update.IsAvailable)
            {
                SetUpdateStatus($"发现新版本 {update.AvailableVersionText}", "#B7F36B", "#415634");
                await new UpdateNoticeWindow(update).ShowDialog(this);
            }
            else
            {
                SetUpdateStatus("当前已是最新版本。", "#8DD2A0", "#34533C");
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_isClosed)
            {
                SetUpdateStatus($"检查更新失败：{exception.Message}", "#FF9B93", "#603733");
            }
        }
        finally
        {
            _isChecking = false;
            if (!_isClosed)
            {
                CheckUpdatesButton.IsEnabled = true;
                CheckUpdatesButtonText.Text = "再次检查";
                UpdateProgress.IsVisible = false;
            }
        }
    }

    private void SetUpdateStatus(string message, string accentColor, string borderColor)
    {
        var accent = new ImmutableSolidColorBrush(Color.Parse(accentColor));
        UpdateStatusText.Text = message;
        UpdateStatusText.Foreground = accent;
        UpdateStatusIndicator.Fill = accent;
        UpdateStatusPanel.BorderBrush = new ImmutableSolidColorBrush(Color.Parse(borderColor));
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _lifetimeCts.Cancel();
        _updateService.Dispose();
        _lifetimeCts.Dispose();
    }
}
