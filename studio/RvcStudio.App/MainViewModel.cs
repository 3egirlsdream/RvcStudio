using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Threading;

namespace RvcStudio.App;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private enum ConfigApplyKind
    {
        Hot,
        RestartRequired,
    }

    private readonly EngineClient _engine = new();
    private readonly DailyUsageQuotaService _usageQuota = new();
    private readonly SemaphoreSlim _quotaUpdateGate = new(1, 1);
    public AccountService Account { get; } = new();
    private readonly DispatcherTimer _statusTimer;
    private CancellationTokenSource? _deviceSelectionCts;
    private CancellationTokenSource? _configApplyCts;
    private CancellationTokenSource? _toastCts;
    private bool _polling;
    private bool _isDisposing;
    private Task? _disposeTask;
    private bool _interactiveReady;
    private bool _applyingDeviceSelection;
    private bool _suppressConfigApply;
    private ConfigApplyKind _pendingConfigApplyKind;
    private string _lastAppliedInputId = string.Empty;
    private string _lastAppliedOutputId = string.Empty;
    private bool _isRunning;
    private string _statusText = "正在连接实时引擎…";
    private IBrush _statusBrush = new SolidColorBrush(Color.Parse("#E7C66A"));
    private string _gpuText = "正在检测 GPU…";
    private string _modelPath = string.Empty;
    private string _indexPath = string.Empty;
    private AudioDevice? _selectedInput;
    private AudioDevice? _selectedOutput;
    private string _pitchMethod = "fcpe";
    private double _pitch;
    private double _formant;
    private double _indexRate;
    private double _rmsMixRate;
    private double _threshold = -60;
    private double _blockTime = 0.25;
    private double _crossfadeLength = 0.05;
    private double _extraTime = 2.5;
    private bool _inputNoiseReduce;
    private bool _outputNoiseReduce;
    private bool _wasapiExclusive;
    private bool _useDeviceSampleRate;
    private double _inputMeter;
    private double _outputMeter;
    private string _timingText = "延迟 -- ms · 推理 -- ms";
    private string _logText = "正在初始化 RVC Studio…";
    private bool _isToastVisible;
    private string _toastMessage = string.Empty;
    private IBrush _toastBrush = new SolidColorBrush(Color.Parse("#253A20"));
    private string _usageQuotaText = "免费额度 · 正在读取";
    private string _usageQuotaLabel = "今日免费剩余";
    private string _usageQuotaValue = "--:--:--";
    private string _usageQuotaHint = "每日 00:00 自动刷新";
    private IBrush _usageQuotaBrush = new SolidColorBrush(Color.Parse("#E7C66A"));
    private bool _quotaExhausted;
    private bool _quotaIntegrityIssue;

    public MainViewModel()
    {
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _statusTimer.Tick += async (_, _) => await PollStatusAsync();
        Account.Changed += Account_Changed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AudioDevice> Inputs { get; } = [];
    public ObservableCollection<AudioDevice> Outputs { get; } = [];
    public IReadOnlyList<string> PitchMethods { get; } = ["fcpe", "rmvpe", "pm"];

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public IBrush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }
    public string ModelPath { get => _modelPath; set { if (Set(ref _modelPath, value)) QueueConfigApply(ConfigApplyKind.RestartRequired); } }
    public string IndexPath { get => _indexPath; set { if (Set(ref _indexPath, value)) QueueConfigApply(ConfigApplyKind.RestartRequired); } }
    public AudioDevice? SelectedInput { get => _selectedInput; set => Set(ref _selectedInput, value); }
    public AudioDevice? SelectedOutput { get => _selectedOutput; set => Set(ref _selectedOutput, value); }
    public string PitchMethod { get => _pitchMethod; set { if (Set(ref _pitchMethod, value)) QueueConfigApply(ConfigApplyKind.Hot); } }
    public double Pitch { get => _pitch; set { if (Set(ref _pitch, value)) QueueConfigApply(ConfigApplyKind.Hot); } }
    public double Formant { get => _formant; set { if (Set(ref _formant, value)) QueueConfigApply(ConfigApplyKind.Hot); } }
    public double IndexRate { get => _indexRate; set { if (Set(ref _indexRate, value)) QueueConfigApply(ConfigApplyKind.Hot); } }
    public double RmsMixRate { get => _rmsMixRate; set { if (Set(ref _rmsMixRate, value)) QueueConfigApply(ConfigApplyKind.Hot); } }
    public double Threshold { get => _threshold; set { if (Set(ref _threshold, value)) QueueConfigApply(ConfigApplyKind.Hot); } }
    public double BlockTime { get => _blockTime; set { if (Set(ref _blockTime, value)) QueueConfigApply(ConfigApplyKind.RestartRequired); } }
    public double CrossfadeLength { get => _crossfadeLength; set { if (Set(ref _crossfadeLength, value)) QueueConfigApply(ConfigApplyKind.RestartRequired); } }
    public double ExtraTime { get => _extraTime; set { if (Set(ref _extraTime, value)) QueueConfigApply(ConfigApplyKind.RestartRequired); } }
    public bool InputNoiseReduce { get => _inputNoiseReduce; set { if (Set(ref _inputNoiseReduce, value)) QueueConfigApply(ConfigApplyKind.Hot); } }
    public bool OutputNoiseReduce { get => _outputNoiseReduce; set { if (Set(ref _outputNoiseReduce, value)) QueueConfigApply(ConfigApplyKind.Hot); } }
    public bool WasapiExclusive { get => _wasapiExclusive; set { if (Set(ref _wasapiExclusive, value)) QueueConfigApply(ConfigApplyKind.RestartRequired); } }
    public bool UseDeviceSampleRate { get => _useDeviceSampleRate; set { if (Set(ref _useDeviceSampleRate, value)) QueueConfigApply(ConfigApplyKind.RestartRequired); } }
    public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) { OnPropertyChanged(nameof(StartButtonText)); OnPropertyChanged(nameof(StartButtonGlyph)); OnPropertyChanged(nameof(CanToggleConversion)); } } }
    public string StartButtonText => IsRunning ? "停止实时变声" : IsFreeUser && _quotaExhausted ? "今日额度已用完" : "开始实时变声";
    public string StartButtonGlyph => IsRunning ? "■" : IsFreeUser && _quotaExhausted ? "⌛" : "▶";
    public bool CanToggleConversion => IsRunning || !IsFreeUser || !_quotaExhausted;
    public string UsageQuotaText { get => _usageQuotaText; private set => Set(ref _usageQuotaText, value); }
    public string UsageQuotaLabel { get => _usageQuotaLabel; private set => Set(ref _usageQuotaLabel, value); }
    public string UsageQuotaValue { get => _usageQuotaValue; private set => Set(ref _usageQuotaValue, value); }
    public string UsageQuotaHint { get => _usageQuotaHint; private set => Set(ref _usageQuotaHint, value); }
    public IBrush UsageQuotaBrush { get => _usageQuotaBrush; private set => Set(ref _usageQuotaBrush, value); }
    public double InputMeter { get => _inputMeter; private set => Set(ref _inputMeter, value); }
    public double OutputMeter { get => _outputMeter; private set => Set(ref _outputMeter, value); }
    public string TimingText { get => _timingText; private set => Set(ref _timingText, value); }
    public string LogText { get => _logText; private set => Set(ref _logText, value); }
    public bool IsToastVisible { get => _isToastVisible; private set => Set(ref _isToastVisible, value); }
    public string ToastMessage { get => _toastMessage; private set => Set(ref _toastMessage, value); }
    public IBrush ToastBrush { get => _toastBrush; private set => Set(ref _toastBrush, value); }
    public bool IsAuthenticated => Account.IsAuthenticated;
    public string AccountButtonTitle => Account.Account?.DisplayName ?? "登录 / 注册";
    public string AccountButtonSubtitle
    {
        get
        {
            var account = Account.Account;
            if (account?.IsMember != true) return account is null ? "RVC Studio 账号" : "免费账号";
            return account.MembershipType switch
            {
                "RVC_STUDIO_MONTHLY" => "月度会员",
                "RVC_STUDIO_QUARTERLY" => "季度会员",
                "RVC_STUDIO_YEARLY" => "年度会员",
                _ => "RVC Studio 会员"
            };
        }
    }
    public string AccountInitial => Account.Account?.Initial ?? "R";

    public string GetModelBrowseDirectory() => ResolveBrowseDirectory(ModelPath, "assets", "weights");

    public string GetIndexBrowseDirectory() => ResolveBrowseDirectory(IndexPath, "assets", "indices");

    public void ReportUpdateCheckFailure(Exception exception) =>
        AppendLog($"检查更新失败：{exception.Message}");

    public async Task InitializeAsync()
    {
        var accountInitialization = Account.InitializeAsync();
        var quotaInitialization = _usageQuota.InitializeAsync();
        try
        {
            await _engine.StartAsync();
            var capabilities = await _engine.GetCapabilitiesAsync();
            GpuText = capabilities.CudaAvailable
                ? $"{capabilities.GpuName} · CUDA {capabilities.CudaVersion}"
                : "未检测到可用 CUDA GPU";
            await ReloadDevicesAsync(false);
            ApplyStatus(await _engine.GetStatusAsync(), applySavedConfig: true);
            _interactiveReady = true;
            RememberAppliedDeviceSelection();
            SetReady("引擎已就绪", "可选择模型和音频设备。FCPE 已可用。");
            _statusTimer.Start();
        }
        catch (Exception exception)
        {
            SetError("无法启动 RVC 引擎", exception);
        }
        try
        {
            await accountInitialization;
        }
        catch (Exception exception)
        {
            AppendLog($"账号状态加载失败：{exception.Message}");
        }
        try
        {
            await quotaInitialization;
            await UpdateUsageQuotaAsync(IsRunning, allowAutomaticStop: false);
        }
        catch (Exception exception)
        {
            ApplyQuotaStorageFailure(exception);
        }
    }

    private void Account_Changed()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposing) return;
            OnPropertyChanged(nameof(IsAuthenticated));
            OnPropertyChanged(nameof(AccountButtonTitle));
            OnPropertyChanged(nameof(AccountButtonSubtitle));
            OnPropertyChanged(nameof(AccountInitial));
            OnPropertyChanged(nameof(StartButtonText));
            OnPropertyChanged(nameof(StartButtonGlyph));
            OnPropertyChanged(nameof(CanToggleConversion));
            _ = RefreshQuotaAfterAccountChangeAsync();
        });
    }

    public async Task ReloadDevicesAsync(bool refresh = true)
    {
        try
        {
            var oldInput = SelectedInput?.Id;
            var oldOutput = SelectedOutput?.Id;
            var devices = await _engine.GetDevicesAsync(refresh);
            Inputs.Clear();
            Outputs.Clear();
            foreach (var item in devices.Inputs) Inputs.Add(item);
            foreach (var item in devices.Outputs) Outputs.Add(item);
            SelectedInput = Inputs.FirstOrDefault(item => item.Id == oldInput)
                ?? Inputs.FirstOrDefault(item => item.IsDefault)
                ?? Inputs.FirstOrDefault();
            SelectedOutput = Outputs.FirstOrDefault(item => item.Id == oldOutput)
                ?? Outputs.FirstOrDefault(IsStandardVbCableInput)
                ?? Outputs.FirstOrDefault(item => item.IsDefault)
                ?? Outputs.FirstOrDefault();
            AppendLog($"音频设备已刷新：{Inputs.Count} 个输入，{Outputs.Count} 个输出。");
        }
        catch (Exception exception)
        {
            SetError("刷新音频设备失败", exception);
        }
    }

    public async Task StartOrStopAsync()
    {
        if (_isDisposing) return;
        try
        {
            if (IsRunning)
            {
                ApplyStatus(await _engine.StopConversionAsync());
                await UpdateUsageQuotaAsync(engineRunning: false, allowAutomaticStop: false);
                SetReady("已停止", "实时音频流已安全关闭。");
                AppendLog("实时变声已停止。");
                return;
            }
            await UpdateUsageQuotaAsync(engineRunning: false, allowAutomaticStop: false);
            if (IsFreeUser && _quotaExhausted)
            {
                ShowToast(
                    _quotaIntegrityIssue
                        ? "本地免费额度记录校验失败，今天无法开启。请勿修改用量数据。"
                        : "今天的 1 小时免费额度已用完，明天 00:00 自动恢复。",
                    "#4A241D");
                return;
            }
            if (SelectedInput is null || SelectedOutput is null)
            {
                throw new InvalidOperationException("请选择输入麦克风和虚拟输出设备。");
            }
            if (string.IsNullOrWhiteSpace(ModelPath))
            {
                throw new InvalidOperationException("请先选择 .pth 音色模型。");
            }
            StatusText = "正在加载模型和启动音频流…";
            StatusBrush = new SolidColorBrush(Color.Parse("#E7C66A"));
            await _engine.UpdateConfigAsync(BuildConfig());
            ApplyStatus(await _engine.StartConversionAsync());
            await UpdateUsageQuotaAsync(IsRunning);
            RememberAppliedDeviceSelection();
            SetReady("正在实时变声", "输出请在 QQ/游戏中选择对应的 VB-Audio 虚拟麦克风。", active: true);
            AppendLog($"已启动：{SelectedInput.Name} → {SelectedOutput.Name}；算法：{PitchMethod.ToUpperInvariant()}。");
        }
        catch (Exception exception)
        {
            SetError("无法启动实时变声", exception);
        }
    }

    public void OpenEngineLog()
    {
        if (string.IsNullOrEmpty(_engine.EngineLogPath) || !File.Exists(_engine.EngineLogPath))
        {
            AppendLog("引擎日志尚未生成。");
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = _engine.EngineLogPath, UseShellExecute = true });
    }

    public void QueueDeviceSelectionApply()
    {
        if (!_interactiveReady)
        {
            return;
        }
        _deviceSelectionCts?.Cancel();
        _deviceSelectionCts?.Dispose();
        _deviceSelectionCts = new CancellationTokenSource();
        _ = ApplyDeviceSelectionAfterDelayAsync(_deviceSelectionCts.Token);
    }

    private void QueueConfigApply(ConfigApplyKind kind)
    {
        if (!_interactiveReady || _suppressConfigApply)
        {
            return;
        }
        _pendingConfigApplyKind = _pendingConfigApplyKind == ConfigApplyKind.RestartRequired || kind == ConfigApplyKind.RestartRequired
            ? ConfigApplyKind.RestartRequired
            : ConfigApplyKind.Hot;
        _configApplyCts?.Cancel();
        _configApplyCts?.Dispose();
        _configApplyCts = new CancellationTokenSource();
        _ = ApplyConfigAfterDelayAsync(_configApplyCts.Token);
    }

    private async Task ApplyConfigAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(180, cancellationToken);
            var kind = _pendingConfigApplyKind;
            _pendingConfigApplyKind = ConfigApplyKind.Hot;
            await _engine.UpdateConfigAsync(BuildConfig(), cancellationToken);
            if (kind == ConfigApplyKind.RestartRequired && IsRunning)
            {
                ShowToast("模型、设备或缓冲设置已修改。请停止后重新开始实时变声以应用。", "#4A381D");
                StatusText = "设置待重启实时流后应用";
                StatusBrush = new SolidColorBrush(Color.Parse("#E7C66A"));
            }
        }
        catch (OperationCanceledException)
        {
            // A later edit superseded this delayed config update.
        }
        catch (Exception exception)
        {
            SetError("应用实时设置失败", exception);
        }
    }

    private async Task ApplyDeviceSelectionAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(180, cancellationToken);
            if (cancellationToken.IsCancellationRequested || _applyingDeviceSelection || SelectedInput is null || SelectedOutput is null)
            {
                return;
            }
            if (SelectedInput.Id == _lastAppliedInputId && SelectedOutput.Id == _lastAppliedOutputId)
            {
                return;
            }
            _applyingDeviceSelection = true;
            var wasRunning = IsRunning;
            if (wasRunning)
            {
                StatusText = "正在切换音频设备…";
                StatusBrush = new SolidColorBrush(Color.Parse("#E7C66A"));
                await _engine.StopConversionAsync(cancellationToken);
            }
            await _engine.UpdateConfigAsync(BuildConfig(), cancellationToken);
            if (wasRunning)
            {
                ApplyStatus(await _engine.StartConversionAsync(cancellationToken));
                SetReady("正在实时变声", "音频设备已切换，实时流已重新启动。", active: true);
            }
            else
            {
                RememberAppliedDeviceSelection();
                SetReady("设备已应用", "音频路由已保存，将在开始实时变声时使用。");
            }
            RememberAppliedDeviceSelection();
        }
        catch (OperationCanceledException)
        {
            // A second selection superseded this request before it was applied.
        }
        catch (Exception exception)
        {
            SetError("切换音频设备失败", exception);
        }
        finally
        {
            _applyingDeviceSelection = false;
        }
    }

    private Dictionary<string, object?> BuildConfig() => new()
    {
        ["pth_path"] = ModelPath,
        ["index_path"] = IndexPath,
        ["pitch"] = (int)Math.Round(Pitch),
        ["formant"] = Formant,
        ["index_rate"] = IndexRate,
        ["rms_mix_rate"] = RmsMixRate,
        ["threshold"] = (int)Math.Round(Threshold),
        ["block_time"] = BlockTime,
        ["crossfade_length"] = CrossfadeLength,
        ["extra_time"] = ExtraTime,
        ["input_noise_reduce"] = InputNoiseReduce,
        ["output_noise_reduce"] = OutputNoiseReduce,
        ["f0method"] = PitchMethod,
        ["wasapi_exclusive"] = WasapiExclusive,
        ["sr_type"] = UseDeviceSampleRate ? "sr_device" : "sr_model",
        ["hostapi"] = SelectedInput?.Hostapi ?? string.Empty,
        ["input_device_id"] = SelectedInput?.Id ?? string.Empty,
        ["input_device_name"] = SelectedInput?.Name ?? string.Empty,
        ["output_device_id"] = SelectedOutput?.Id ?? string.Empty,
        ["output_device_name"] = SelectedOutput?.Name ?? string.Empty,
    };

    private async Task PollStatusAsync()
    {
        if (_polling || _isDisposing) return;
        _polling = true;
        try
        {
            var status = await _engine.GetStatusAsync();
            ApplyStatus(status);
            await UpdateUsageQuotaAsync(status.Running);
        }
        catch (Exception exception)
        {
            _statusTimer.Stop();
            SetError("与 RVC 引擎的连接已断开", exception);
        }
        finally
        {
            _polling = false;
        }
    }

    private void ApplyStatus(EngineStatus status, bool applySavedConfig = false)
    {
        IsRunning = status.Running;
        InputMeter = Math.Clamp(status.InputLevel * 240, 0, 100);
        OutputMeter = Math.Clamp(status.OutputLevel * 240, 0, 100);
        TimingText = status.Running
            ? $"延迟约 {status.DelayMs} ms · 推理 {status.InferMs:0.0} ms · {status.Samplerate} Hz"
            : "延迟 -- ms · 推理 -- ms";
        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            SetError("实时引擎报告错误", new InvalidOperationException(status.LastError));
        }
        if (status.RestartRequired && status.Running)
        {
            StatusText = "参数已修改；停止后再次开始即可应用设备/缓冲设置。";
        }
        // The control timer must never copy stale engine values over a field
        // that the user has just selected or typed.  Configuration is loaded
        // once during initialization; later calls only report live status.
        if (applySavedConfig)
        {
            ApplySavedConfig(status.Config);
        }
    }

    private bool IsFreeUser
    {
        get
        {
            var account = Account.Account;
            if (account?.IsMember != true) return true;
            return account.MembershipExpireDate is not null && account.MembershipExpireDate.Value <= DateTime.Now;
        }
    }

    private async Task RefreshQuotaAfterAccountChangeAsync()
    {
        try
        {
            await UpdateUsageQuotaAsync(IsRunning);
        }
        catch (Exception exception)
        {
            ApplyQuotaStorageFailure(exception);
        }
    }

    private async Task UpdateUsageQuotaAsync(bool engineRunning, bool allowAutomaticStop = true)
    {
        if (_isDisposing) return;
        await _quotaUpdateGate.WaitAsync();
        try
        {
            var isFree = IsFreeUser;
            var snapshot = await _usageQuota.UpdateAsync(isFree && engineRunning);
            ApplyQuotaSnapshot(snapshot, isFree);

            if (allowAutomaticStop && isFree && engineRunning && snapshot.Exhausted)
            {
                var stoppedStatus = await _engine.StopConversionAsync();
                ApplyStatus(stoppedStatus);
                snapshot = await _usageQuota.StopTrackingAsync();
                ApplyQuotaSnapshot(snapshot, isFree: true);
                SetReady(
                    "今日免费额度已用完",
                    "实时变声已自动关闭；明天 00:00 后可再次使用。",
                    active: false);
                ShowToast("今天的 1 小时免费额度已用完，实时变声已自动关闭。", "#4A241D");
            }
        }
        catch (Exception exception)
        {
            ApplyQuotaStorageFailure(exception);
            if (allowAutomaticStop && IsFreeUser && engineRunning)
            {
                try
                {
                    ApplyStatus(await _engine.StopConversionAsync());
                }
                catch (Exception stopException)
                {
                    AppendLog($"额度校验失败后关闭实时变声失败：{stopException.Message}");
                }
            }
        }
        finally
        {
            _quotaUpdateGate.Release();
        }
    }

    private void ApplyQuotaSnapshot(DailyQuotaSnapshot snapshot, bool isFree)
    {
        var wasExhausted = _quotaExhausted;
        _quotaIntegrityIssue = snapshot.IntegrityIssue;
        _quotaExhausted = isFree && snapshot.Exhausted;

        if (!isFree)
        {
            UsageQuotaText = "会员 · 不限时";
            UsageQuotaLabel = "会员使用权益";
            UsageQuotaValue = "无限制";
            UsageQuotaHint = "实时变声不限时使用";
            UsageQuotaBrush = new SolidColorBrush(Color.Parse("#B8F36A"));
        }
        else if (snapshot.IntegrityIssue)
        {
            UsageQuotaText = "免费额度 · 数据异常";
            UsageQuotaLabel = "免费额度状态";
            UsageQuotaValue = "校验失败";
            UsageQuotaHint = "今天无法开启实时变声";
            UsageQuotaBrush = new SolidColorBrush(Color.Parse("#FF9B91"));
        }
        else if (snapshot.Exhausted)
        {
            UsageQuotaText = "今日免费额度已用完";
            UsageQuotaLabel = "今日免费剩余";
            UsageQuotaValue = "00:00:00";
            UsageQuotaHint = "明日 00:00 自动恢复";
            UsageQuotaBrush = new SolidColorBrush(Color.Parse("#FF9B91"));
        }
        else
        {
            var totalSeconds = Math.Max(0, (int)Math.Ceiling(snapshot.Remaining.TotalSeconds));
            UsageQuotaText = $"免费额度 · {totalSeconds / 60:00}:{totalSeconds % 60:00}";
            UsageQuotaLabel = "今日免费剩余";
            UsageQuotaValue = $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}";
            UsageQuotaHint = "仅开启期间计时 · 00:00 刷新";
            UsageQuotaBrush = new SolidColorBrush(Color.Parse("#E7C66A"));
        }

        if (snapshot.DayRefreshed)
        {
            AppendLog("已跨过 00:00，今日免费额度已自动刷新。");
        }
        if (wasExhausted != _quotaExhausted)
        {
            OnPropertyChanged(nameof(StartButtonText));
            OnPropertyChanged(nameof(StartButtonGlyph));
            OnPropertyChanged(nameof(CanToggleConversion));
        }
    }

    private void ApplyQuotaStorageFailure(Exception exception)
    {
        _quotaIntegrityIssue = true;
        _quotaExhausted = IsFreeUser;
        UsageQuotaText = IsFreeUser ? "免费额度 · 校验失败" : "会员 · 不限时";
        UsageQuotaLabel = IsFreeUser ? "免费额度状态" : "会员使用权益";
        UsageQuotaValue = IsFreeUser ? "校验失败" : "无限制";
        UsageQuotaHint = IsFreeUser ? "今天无法开启实时变声" : "实时变声不限时使用";
        UsageQuotaBrush = new SolidColorBrush(Color.Parse(IsFreeUser ? "#FF9B91" : "#B8F36A"));
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(StartButtonGlyph));
        OnPropertyChanged(nameof(CanToggleConversion));
        AppendLog($"免费额度记录校验失败：{exception.Message}");
    }

    private void ApplySavedConfig(Dictionary<string, object?> config)
    {
        if (config.Count == 0) return;
        _suppressConfigApply = true;
        try
        {
            ModelPath = GetString(config, "pth_path", ModelPath);
            IndexPath = GetString(config, "index_path", IndexPath);
            PitchMethod = GetString(config, "f0method", PitchMethod);
            Pitch = GetDouble(config, "pitch", Pitch);
            Formant = GetDouble(config, "formant", Formant);
            IndexRate = GetDouble(config, "index_rate", IndexRate);
            RmsMixRate = GetDouble(config, "rms_mix_rate", RmsMixRate);
            Threshold = GetDouble(config, "threshold", Threshold);
            BlockTime = GetDouble(config, "block_time", BlockTime);
            CrossfadeLength = GetDouble(config, "crossfade_length", CrossfadeLength);
            ExtraTime = GetDouble(config, "extra_time", ExtraTime);
            InputNoiseReduce = GetBool(config, "input_noise_reduce", InputNoiseReduce);
            OutputNoiseReduce = GetBool(config, "output_noise_reduce", OutputNoiseReduce);
            WasapiExclusive = GetBool(config, "wasapi_exclusive", WasapiExclusive);
            UseDeviceSampleRate = GetString(config, "sr_type", "sr_model") == "sr_device";
            var inputId = GetString(config, "input_device_id", string.Empty);
            var outputId = GetString(config, "output_device_id", string.Empty);
            var inputName = GetString(config, "input_device_name", string.Empty);
            var outputName = GetString(config, "output_device_name", string.Empty);
            SelectedInput = Inputs.FirstOrDefault(item => item.Id == inputId) ?? Inputs.FirstOrDefault(item => item.Name == inputName) ?? SelectedInput;
            SelectedOutput = Outputs.FirstOrDefault(item => item.Id == outputId) ?? Outputs.FirstOrDefault(item => item.Name == outputName) ?? SelectedOutput;
        }
        finally
        {
            _suppressConfigApply = false;
        }
    }

    private void RememberAppliedDeviceSelection()
    {
        _lastAppliedInputId = SelectedInput?.Id ?? string.Empty;
        _lastAppliedOutputId = SelectedOutput?.Id ?? string.Empty;
    }

    private static bool IsStandardVbCableInput(AudioDevice device)
    {
        return device.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveBrowseDirectory(string selectedPath, params string[] bundledDirectoryParts)
    {
        var baseDirectories = new[]
        {
            _engine.RvcRoot,
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            try
            {
                if (Path.IsPathFullyQualified(selectedPath))
                {
                    var selectedDirectory = Directory.Exists(selectedPath)
                        ? selectedPath
                        : Path.GetDirectoryName(selectedPath);
                    if (!string.IsNullOrWhiteSpace(selectedDirectory) && Directory.Exists(selectedDirectory))
                    {
                        return selectedDirectory;
                    }
                }
                else
                {
                    foreach (var baseDirectory in baseDirectories)
                    {
                        var resolvedPath = Path.GetFullPath(selectedPath, baseDirectory);
                        var selectedDirectory = Directory.Exists(resolvedPath)
                            ? resolvedPath
                            : Path.GetDirectoryName(resolvedPath);
                        if (!string.IsNullOrWhiteSpace(selectedDirectory) && Directory.Exists(selectedDirectory))
                        {
                            return selectedDirectory;
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // An invalid manually entered path falls through to the bundled resource folder.
            }
        }

        foreach (var baseDirectory in baseDirectories)
        {
            var bundledDirectory = Path.Combine([baseDirectory, .. bundledDirectoryParts]);
            if (Directory.Exists(bundledDirectory)) return bundledDirectory;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private static string GetString(Dictionary<string, object?> values, string name, string fallback)
    {
        if (!values.TryGetValue(name, out var value) || value is null) return fallback;
        if (value is JsonElement element) return element.ValueKind == JsonValueKind.String ? element.GetString() ?? fallback : element.ToString();
        return Convert.ToString(value) ?? fallback;
    }

    private static double GetDouble(Dictionary<string, object?> values, string name, double fallback)
    {
        if (!values.TryGetValue(name, out var value) || value is null) return fallback;
        if (value is JsonElement element && element.TryGetDouble(out var result)) return result;
        return double.TryParse(Convert.ToString(value), out var parsed) ? parsed : fallback;
    }

    private static bool GetBool(Dictionary<string, object?> values, string name, bool fallback)
    {
        if (!values.TryGetValue(name, out var value) || value is null) return fallback;
        if (value is JsonElement element && element.ValueKind is JsonValueKind.True or JsonValueKind.False) return element.GetBoolean();
        return bool.TryParse(Convert.ToString(value), out var parsed) ? parsed : fallback;
    }

    private void SetReady(string status, string detail, bool active = false)
    {
        StatusText = status;
        StatusBrush = new SolidColorBrush(Color.Parse(active ? "#B8F36A" : "#C4CEC2"));
        AppendLog(detail);
    }

    private void SetError(string title, Exception exception)
    {
        StatusText = title;
        StatusBrush = new SolidColorBrush(Color.Parse("#FF9B91"));
        AppendLog($"{title}：{exception.Message}");
    }

    private void ShowToast(string message, string color)
    {
        ToastMessage = message;
        ToastBrush = new SolidColorBrush(Color.Parse(color));
        IsToastVisible = true;
        _toastCts?.Cancel();
        _toastCts?.Dispose();
        _toastCts = new CancellationTokenSource();
        _ = HideToastAfterDelayAsync(_toastCts.Token);
    }

    private async Task HideToastAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            IsToastVisible = false;
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer toast.
        }
    }

    private void AppendLog(string line)
    {
        var lines = (LogText + Environment.NewLine + $"[{DateTime.Now:HH:mm:ss}] {line}").Split(Environment.NewLine);
        LogText = string.Join(Environment.NewLine, lines.TakeLast(250));
    }

    public ValueTask DisposeAsync()
    {
        if (_disposeTask is null)
        {
            _isDisposing = true;
            _disposeTask = DisposeCoreAsync();
        }
        return new ValueTask(_disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        _statusTimer.Stop();
        _deviceSelectionCts?.Cancel();
        _deviceSelectionCts?.Dispose();
        _configApplyCts?.Cancel();
        _configApplyCts?.Dispose();
        _toastCts?.Cancel();
        _toastCts?.Dispose();
        Account.Changed -= Account_Changed;
        // Let an in-flight quota enforcement/automatic stop finish, then keep
        // new quota work out of the engine shutdown sequence.
        await _quotaUpdateGate.WaitAsync();

        var conversionRunning = IsRunning;
        try
        {
            using var statusTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var actualStatus = await _engine.GetStatusAsync(statusTimeout.Token);
            conversionRunning = actualStatus.Running;
            ApplyStatus(actualStatus);
        }
        catch (Exception exception)
        {
            // Fall back to the last known state. Engine disposal below still
            // performs a shutdown/kill if the control channel is unavailable.
            AppendLog($"关闭前检测实时变声状态失败：{exception.Message}");
        }

        if (conversionRunning)
        {
            try
            {
                StatusText = "正在关闭实时变声…";
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var stoppedStatus = await _engine.StopConversionAsync(stopTimeout.Token);
                ApplyStatus(stoppedStatus);
                AppendLog("退出程序前已自动关闭实时变声。");
            }
            catch (Exception exception)
            {
                AppendLog($"自动关闭实时变声失败，将强制关闭后台引擎：{exception.Message}");
            }
        }

        try
        {
            await _usageQuota.StopTrackingAsync();
        }
        catch (Exception exception)
        {
            AppendLog($"保存免费额度记录失败：{exception.Message}");
        }
        Account.Dispose();
        try
        {
            await _engine.DisposeAsync();
        }
        catch (Exception exception)
        {
            AppendLog($"关闭后台引擎失败：{exception.Message}");
        }
        try
        {
            await _usageQuota.DisposeAsync();
        }
        catch (Exception exception)
        {
            AppendLog($"释放免费额度记录失败：{exception.Message}");
        }
        _quotaUpdateGate.Release();
        _quotaUpdateGate.Dispose();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
