using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;

namespace RvcStudio.App;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel? _mainViewModel;
    private readonly ModelPackageService? _modelPackages;
    private readonly AppUpdateService _updateService = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Dictionary<CheckBox, ManagedModel> _exportSelections = [];
    private ModelImportPreview? _importPreview;
    private bool _isChecking;
    private bool _isImporting;
    private bool _isExporting;
    private bool _isClosed;

    public SettingsWindow()
    {
        InitializeComponent();
        ImportPitchMethodCombo.ItemsSource = new[] { "FCPE", "RMVPE", "PM" };
        CurrentVersionText.Text = AppUpdateResult
            .NotAvailable(AppUpdateService.CurrentVersion)
            .CurrentVersionText;
    }

    public SettingsWindow(MainViewModel mainViewModel) : this()
    {
        _mainViewModel = mainViewModel;
        _modelPackages = mainViewModel.ModelPackages;
    }

    private void Window_Opened(object? sender, EventArgs e)
    {
        if (_modelPackages is null)
        {
            ChooseImportZipButton.IsEnabled = false;
            ExportButton.IsEnabled = false;
            RefreshModelsButton.IsEnabled = false;
            ImportStatusText.Text = "实时引擎尚未就绪，暂时无法确定模型目录。";
            ExportStatusText.Text = "实时引擎尚未就绪，模型管理不可用。";
            return;
        }
        RefreshModelList();
    }

    private async void ChooseImportZip_Click(object? sender, RoutedEventArgs e)
    {
        if (_modelPackages is null || _isImporting || _isExporting) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择包含 PTH 和 INDEX 的模型 ZIP",
            AllowMultiple = false,
            SuggestedStartLocation = await TryGetFolderAsync(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            FileTypeFilter = [new FilePickerFileType("ZIP 压缩包") { Patterns = ["*.zip"] }],
        });
        if (files.Count == 0 || !files[0].Path.IsFile) return;

        SetImportBusy(true, "正在读取压缩包…");
        try
        {
            var preview = await _modelPackages.PreviewImportAsync(
                files[0].Path.LocalPath,
                _lifetimeCts.Token);
            if (_isClosed) return;
            _importPreview = preview;
            ImportZipPathText.Text = preview.ZipPath;
            ImportPthText.Text = preview.ModelEntryName;
            ImportIndexText.Text = preview.IndexEntryName;
            ImportJsonText.Text = preview.JsonEntryName ?? "无 · 使用程序全局默认";
            ImportModelNameText.Text = preview.SuggestedName;
            ApplyImportSettings(preview.Settings);
            ImportPreviewPanel.IsVisible = true;
            ImportStatusText.Text = preview.Settings.IsAppDefault
                ? "参数已预填程序全局默认；全部保持默认时不会生成模型 JSON。"
                : "已从压缩包 JSON 读取模型单独参数，可继续手工修改。";
            ImportStatusText.Foreground = Brush("#9CA69F");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _importPreview = null;
            ImportPreviewPanel.IsVisible = false;
            ImportZipPathText.Text = files[0].Path.LocalPath;
            ImportStatusText.Text = exception.Message;
            ImportStatusText.Foreground = Brush("#FF9B93");
        }
        finally
        {
            SetImportBusy(false);
        }
    }

    private async void ImportModel_Click(object? sender, RoutedEventArgs e)
    {
        if (_modelPackages is null || _mainViewModel is null || _importPreview is null || _isImporting || _isExporting) return;
        if (!TryReadImportSettings(out var settings, out var validationError))
        {
            ImportStatusText.Text = validationError;
            ImportStatusText.Foreground = Brush("#FF9B93");
            return;
        }

        string modelName;
        string targetModelPath;
        try
        {
            modelName = ModelPackageService.ValidateModelName(ImportModelNameText.Text ?? string.Empty);
            targetModelPath = _modelPackages.GetModelPath(modelName);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            ImportStatusText.Text = exception.Message;
            ImportStatusText.Foreground = Brush("#FF9B93");
            return;
        }

        var hasConflict = _modelPackages.HasConflict(modelName);
        if (hasConflict && _mainViewModel.IsCurrentModelPath(targetModelPath) && _mainViewModel.IsRunning)
        {
            ImportStatusText.Text = "实时变声运行中不能替换当前模型，请先停止实时变声。";
            ImportStatusText.Foreground = Brush("#FF9B93");
            return;
        }
        if (hasConflict && !await ConfirmAsync(
                "替换同名模型",
                $"模型“{modelName}”已经存在。继续将整体替换 PTH、INDEX 和配置 JSON。",
                "确认替换"))
        {
            return;
        }

        SetImportBusy(true, $"正在导入 {modelName}…");
        try
        {
            await _mainViewModel.PrepareModelReplacementAsync(targetModelPath, _lifetimeCts.Token);
            var result = await _modelPackages.ImportAsync(
                _importPreview,
                modelName,
                settings,
                _lifetimeCts.Token);
            await _mainViewModel.ReloadModelAfterImportAsync(result.ModelPath, _lifetimeCts.Token);
            if (_isClosed) return;
            RefreshModelList();
            ImportStatusText.Text = result.ReplacedExisting
                ? $"已整体替换模型 {result.Name}。"
                : $"已导入模型 {result.Name}。";
            ImportStatusText.Foreground = Brush("#8DD2A0");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ImportStatusText.Text = $"导入失败：{exception.Message}";
            ImportStatusText.Foreground = Brush("#FF9B93");
        }
        finally
        {
            SetImportBusy(false);
        }
    }

    private void RefreshModels_Click(object? sender, RoutedEventArgs e) => RefreshModelList();

    private void RefreshModelList()
    {
        ExportModelList.Children.Clear();
        _exportSelections.Clear();
        if (_modelPackages is null) return;
        try
        {
            var models = _modelPackages.ScanModels();
            foreach (var model in models)
            {
                var checkBox = new CheckBox
                {
                    IsEnabled = model.CanExport,
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = model.Name,
                    MinWidth = 210,
                };
                var status = new TextBlock
                {
                    Text = model.CanExport
                        ? model.HasCustomSettings ? "PTH + INDEX + 单独配置" : "PTH + INDEX · 全局默认"
                        : "缺少同名 INDEX，无法导出",
                    Foreground = Brush(model.CanExport ? "#9CA69F" : "#E7C66A"),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 10.5,
                };
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    ColumnSpacing = 10,
                    Margin = new Thickness(3, 4),
                };
                row.Children.Add(checkBox);
                Grid.SetColumn(status, 1);
                row.Children.Add(status);
                ExportModelList.Children.Add(row);
                _exportSelections[checkBox] = model;
            }
            ExportStatusText.Text = models.Count == 0
                ? "模型目录中没有可管理的 PTH。"
                : $"已发现 {models.Count} 个模型。";
            ExportStatusText.Foreground = Brush("#9CA69F");
        }
        catch (Exception exception)
        {
            ExportStatusText.Text = exception.Message;
            ExportStatusText.Foreground = Brush("#FF9B93");
        }
    }

    private async void ExportModels_Click(object? sender, RoutedEventArgs e)
    {
        if (_modelPackages is null || _isImporting || _isExporting) return;
        var selected = _exportSelections
            .Where(item => item.Key.IsChecked == true && item.Value.CanExport)
            .Select(item => item.Value)
            .ToList();
        if (selected.Count == 0)
        {
            ExportStatusText.Text = "请先勾选至少一个可导出的模型。";
            ExportStatusText.Foreground = Brush("#E7C66A");
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择模型 ZIP 导出文件夹",
            AllowMultiple = false,
            SuggestedStartLocation = await TryGetFolderAsync(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        });
        if (folders.Count == 0) return;
        var destination = folders[0].Path.LocalPath;
        var existing = _modelPackages.GetExistingExportPaths(selected, destination);
        if (existing.Count > 0 && !await ConfirmAsync(
                "覆盖已有 ZIP",
                $"目标文件夹中已有 {existing.Count} 个同名 ZIP。继续将覆盖这些导出文件。",
                "确认覆盖"))
        {
            return;
        }

        SetExportBusy(true, selected.Count);
        try
        {
            var progress = new Progress<ModelTransferProgress>(item =>
            {
                ExportProgress.Value = item.Completed;
                ExportStatusText.Text = item.Message;
            });
            var results = await _modelPackages.ExportAsync(
                selected,
                destination,
                overwrite: existing.Count > 0,
                progress,
                _lifetimeCts.Token);
            if (_isClosed) return;
            ExportStatusText.Text = $"已导出 {results.Count} 个模型到：{destination}";
            ExportStatusText.Foreground = Brush("#8DD2A0");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ExportStatusText.Text = $"导出失败：{exception.Message}";
            ExportStatusText.Foreground = Brush("#FF9B93");
        }
        finally
        {
            SetExportBusy(false, selected.Count);
        }
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
            if (!_isClosed) SetUpdateStatus($"检查更新失败：{exception.Message}", "#FF9B93", "#603733");
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

    private void ApplyImportSettings(ModelTuningSettings settings)
    {
        ImportPitchMethodCombo.SelectedItem = settings.PitchMethod.ToUpperInvariant();
        ImportPitchText.Text = Format(settings.Pitch);
        ImportFormantText.Text = Format(settings.Formant);
        ImportIndexRateText.Text = Format(settings.IndexRate);
        ImportRmsMixRateText.Text = Format(settings.RmsMixRate);
        ImportThresholdText.Text = Format(settings.Threshold);
        ImportBlockTimeText.Text = Format(settings.BlockTime);
        ImportCrossfadeText.Text = Format(settings.CrossfadeLength);
        ImportExtraTimeText.Text = Format(settings.ExtraTime);
    }

    private bool TryReadImportSettings(out ModelTuningSettings settings, out string error)
    {
        var boxes = new (string Name, TextBox Box)[]
        {
            ("Pitch", ImportPitchText),
            ("Formant", ImportFormantText),
            ("Index", ImportIndexRateText),
            ("RMS", ImportRmsMixRateText),
            ("门限", ImportThresholdText),
            ("分块", ImportBlockTimeText),
            ("交叉淡化", ImportCrossfadeText),
            ("额外推理", ImportExtraTimeText),
        };
        var values = new double[boxes.Length];
        for (var index = 0; index < boxes.Length; index++)
        {
            if (!TryParseNumber(boxes[index].Box.Text, out values[index]))
            {
                settings = ModelTuningSettings.AppDefaults;
                error = $"{boxes[index].Name} 不是有效数字。";
                return false;
            }
        }
        return ModelTuningSettings.TryValidate(new ModelTuningSettings(
            ImportPitchMethodCombo.SelectedItem as string ?? string.Empty,
            values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]),
            out settings,
            out error);
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("#121613"),
        };
        var confirm = new Button { Content = confirmText, MinWidth = 104 };
        confirm.Classes.Add("primary");
        var cancel = new Button { Content = "取消", MinWidth = 86 };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, confirm },
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#B8C0BA") },
                buttons,
            },
        };
        return await dialog.ShowDialog<bool>(this);
    }

    private void SetImportBusy(bool busy, string? status = null)
    {
        _isImporting = busy;
        ChooseImportZipButton.IsEnabled = !busy && !_isExporting;
        ImportButton.IsEnabled = !busy && !_isExporting;
        ExportButton.IsEnabled = !busy && !_isExporting;
        ImportProgress.IsVisible = busy;
        if (status is not null) ImportStatusText.Text = status;
    }

    private void SetExportBusy(bool busy, int total)
    {
        _isExporting = busy;
        ChooseImportZipButton.IsEnabled = !busy && !_isImporting;
        ImportButton.IsEnabled = !busy && !_isImporting;
        ExportButton.IsEnabled = !busy && !_isImporting;
        RefreshModelsButton.IsEnabled = !busy;
        ExportProgress.IsVisible = busy;
        ExportProgress.Maximum = Math.Max(1, total);
        if (!busy) ExportProgress.Value = 0;
    }

    private void SetUpdateStatus(string message, string accentColor, string borderColor)
    {
        var accent = Brush(accentColor);
        UpdateStatusText.Text = message;
        UpdateStatusText.Foreground = accent;
        UpdateStatusIndicator.Fill = accent;
        UpdateStatusPanel.BorderBrush = Brush(borderColor);
    }

    private async Task<IStorageFolder?> TryGetFolderAsync(string path)
    {
        try
        {
            return await StorageProvider.TryGetFolderFromPathAsync(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryParseNumber(string? value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
        double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result);

    private static string Format(double value) => value.ToString("0.#################", CultureInfo.InvariantCulture);

    private static ImmutableSolidColorBrush Brush(string color) =>
        new(Color.Parse(color));

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _lifetimeCts.Cancel();
        _updateService.Dispose();
        _lifetimeCts.Dispose();
    }
}
