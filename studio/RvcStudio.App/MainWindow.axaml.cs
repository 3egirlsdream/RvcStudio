using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RvcStudio.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private bool _canClose;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void Window_Opened(object? sender, EventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private async void RefreshDevices_Click(object? sender, RoutedEventArgs e) => await _viewModel.ReloadDevicesAsync();

    private void DeviceSelectionChanged(object? sender, SelectionChangedEventArgs e) => _viewModel.QueueDeviceSelectionApply();

    private async void StartStop_Click(object? sender, RoutedEventArgs e) => await _viewModel.StartOrStopAsync();

    private void RestoreDefaults_Click(object? sender, RoutedEventArgs e) => _viewModel.RestoreModelDefaults();

    private async void Settings_Click(object? sender, RoutedEventArgs e) =>
        await new SettingsWindow(_viewModel).ShowDialog(this);

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
        try
        {
            await _viewModel.DisposeAsync();
        }
        finally
        {
            _canClose = true;
            Close();
        }
    }

}
