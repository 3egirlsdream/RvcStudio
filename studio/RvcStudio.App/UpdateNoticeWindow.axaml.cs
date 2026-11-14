using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;

namespace RvcStudio.App;

public partial class UpdateNoticeWindow : Window
{
    private static UpdateNoticeWindow? _open;
    private readonly AppUpdateResult _update;
    private readonly AppUpdateService _service = new();
    private CancellationTokenSource? _download;
    private string? _cache;
    private bool _installing;
    private bool _closed;

    public static void ShowFor(AppUpdateResult update, Window owner)
    {
        if (_open is not null) { _open.Activate(); return; }
        _open = new UpdateNoticeWindow(update);
        _open.Show(owner);
    }
    public UpdateNoticeWindow() : this(AppUpdateResult.NotAvailable(new Version(1, 0, 0))) { }
    public UpdateNoticeWindow(AppUpdateResult update)
    {
        InitializeComponent();
        _update = update;
        VersionText.Text = $"{update.CurrentVersionText} → {update.AvailableVersionText}";
        MemoText.Text = string.IsNullOrWhiteSpace(update.Memo) ? "暂无更新说明。" : update.Memo;
        var artifact = update.CanUseSmallPackage ? update.Manifest?.Package : update.Manifest?.Installer;
        SizeText.Text = artifact is null ? "暂无下载信息" : $"本次下载：{UpdatePackage.FormatSize(artifact.Size)}（{artifact.Size:N0} 字节）";
        StatusText.Text = update.CanUseSmallPackage ? "只更新程序和引擎代码。下载时可以继续使用软件。" : "此版本的运行环境或基础模型发生变化，或当前安装缺少基线，需要下载完整安装包。";
        ActionButton.Content = update.CanUseSmallPackage ? "下载更新" : "下载完整安装包";
        ActionButton.IsEnabled = artifact is not null;
        Closing += (_, e) => { if (_installing) e.Cancel = true; };
        Closed += (_, _) =>
        {
            _closed = true;
            _download?.Cancel();
            _service.Dispose();
            if (!_installing && _cache is not null) { try { Directory.Delete(_cache, true); } catch (IOException) { } }
            if (_open == this) _open = null;
        };
    }

    private async void Action_Click(object? sender, RoutedEventArgs e)
    {
        if (!_update.CanUseSmallPackage)
        {
            try { Process.Start(new ProcessStartInfo(_update.Manifest!.Installer.Url) { UseShellExecute = true }); }
            catch (Exception ex) { StatusText.Text = "无法打开下载地址：" + ex.Message; }
            return;
        }
        if (_cache is not null)
        {
            _installing = true;
            ActionButton.IsEnabled = LaterButton.IsEnabled = false;
            StatusText.Text = "正在请求管理员权限，允许后将停止变声并重启更新…";
            try
            {
                await UpdateRunner.BeginAsync(_cache);
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    _installing = false; // owned windows may now close as part of normal engine shutdown
                    desktop.MainWindow?.Close();
                }
            }
            catch (Exception ex)
            {
                _installing = false;
                StatusText.Text = ex.Message;
                ActionButton.IsEnabled = LaterButton.IsEnabled = true;
            }
            return;
        }
        _download = new CancellationTokenSource();
        ActionButton.IsEnabled = false;
        DownloadProgress.IsVisible = CancelButton.IsVisible = true;
        StatusText.Text = "正在下载并验证更新…";
        try
        {
            _cache = await _service.DownloadAsync(_update, new Progress<double>(value => { if (!_closed) DownloadProgress.Value = value; }), _download.Token);
            if (_closed) { Directory.Delete(_cache, true); _cache = null; }
            if (!_closed) { ActionButton.Content = "重启并更新"; StatusText.Text = "下载和校验完成。重启会停止当前变声。"; }
        }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = "下载已取消，可重新下载。"; }
        catch (Exception ex) { if (!_closed) StatusText.Text = "下载失败，可重试：" + ex.Message; }
        finally
        {
            _download.Dispose(); _download = null;
            if (!_closed) { ActionButton.IsEnabled = true; CancelButton.IsVisible = DownloadProgress.IsVisible = false; }
        }
    }
    private void Cancel_Click(object? sender, RoutedEventArgs e) => _download?.Cancel();
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
