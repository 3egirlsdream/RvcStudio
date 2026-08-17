using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace RvcStudio.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly AppUpdateService _updateService = new();
    private readonly CancellationTokenSource _updateCheckCts = new();
    private bool _canClose;
    private bool _closing;
    private bool _updateCheckStarted;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void Window_Opened(object? sender, EventArgs e)
    {
        await _viewModel.InitializeAsync();
        await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updateCheckStarted || _closing) return;
        _updateCheckStarted = true;
        try
        {
            var update = await _updateService.CheckAsync(_updateCheckCts.Token);
            if (update.IsAvailable && !_closing)
            {
                await new UpdateNoticeWindow(update).ShowDialog(this);
            }
        }
        catch (OperationCanceledException) when (_closing)
        {
        }
        catch (Exception exception)
        {
            _viewModel.ReportUpdateCheckFailure(exception);
        }
    }

    private async void BrowseModel_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(
            "选择 RVC .pth 音色模型",
            "RVC model",
            "*.pth",
            _viewModel.GetModelBrowseDirectory());
        if (path is not null) _viewModel.ModelPath = path;
    }

    private async void BrowseIndex_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(
            "选择 .index 索引文件（可选）",
            "Index file",
            "*.index",
            _viewModel.GetIndexBrowseDirectory());
        if (path is not null) _viewModel.IndexPath = path;
    }

    private async void RefreshDevices_Click(object? sender, RoutedEventArgs e) => await _viewModel.ReloadDevicesAsync();

    private void DeviceSelectionChanged(object? sender, SelectionChangedEventArgs e) => _viewModel.QueueDeviceSelectionApply();

    private async void StartStop_Click(object? sender, RoutedEventArgs e) => await _viewModel.StartOrStopAsync();

    private async void Account_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new AccountWindow(_viewModel.Account);
        await dialog.ShowDialog(this);
        if (dialog.OpenMembershipRequested && _viewModel.Account.IsAuthenticated)
        {
            await new MembershipWindow(_viewModel.Account).ShowDialog(this);
        }
    }

    private void OpenLog_Click(object? sender, RoutedEventArgs e) => _viewModel.OpenEngineLog();

    private async void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_canClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _updateCheckCts.Cancel();
        try
        {
            await _viewModel.DisposeAsync();
        }
        finally
        {
            _updateService.Dispose();
            _updateCheckCts.Dispose();
            _canClose = true;
            Close();
        }
    }

    private async Task<string?> PickFileAsync(
        string title,
        string typeName,
        string pattern,
        string suggestedDirectory)
    {
        IStorageFolder? suggestedStartLocation = null;
        try
        {
            suggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(suggestedDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // If the folder becomes unavailable, let Windows choose its normal default location.
        }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = suggestedStartLocation,
            FileTypeFilter = [new FilePickerFileType(typeName) { Patterns = [pattern] }],
        });
        return files.Count > 0 && files[0].Path.IsFile ? files[0].Path.LocalPath : null;
    }
}
