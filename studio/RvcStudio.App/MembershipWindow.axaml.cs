using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace RvcStudio.App;

public partial class MembershipWindow : Window, INotifyPropertyChanged
{
    private readonly AccountService _accounts;
    private readonly bool _ownsAccountService;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private MembershipPlan? _selectedPlan;
    private MembershipOrder? _order;
    private bool _busy;
    private bool _polling;
    private bool _paid;
    private string _statusTitle = "等待选择套餐";
    private string _statusMessage = "选择套餐后，将在系统浏览器打开支付宝网页收银台。";

    public MembershipWindow() : this(new AccountService(), true)
    {
    }

    public MembershipWindow(AccountService accounts) : this(accounts, false)
    {
    }

    private MembershipWindow(AccountService accounts, bool ownsAccountService)
    {
        InitializeComponent();
        _accounts = accounts;
        _ownsAccountService = ownsAccountService;
        DataContext = this;
    }

    public ObservableCollection<MembershipPlan> Plans { get; } = [];
    public MembershipPlan? SelectedPlan
    {
        get => _selectedPlan;
        set
        {
            if (_selectedPlan == value) return;
            _selectedPlan = value;
            _order = null;
            _paid = false;
            StatusTitle = value is null ? "等待选择套餐" : value.Name;
            StatusMessage = value is null
                ? "选择套餐后，将在系统浏览器打开支付宝网页收银台。"
                : $"{value.Description}，价格 {value.PriceText}。";
            RefreshAll();
        }
    }
    public string StatusTitle { get => _statusTitle; private set { _statusTitle = value; OnPropertyChanged(); } }
    public string StatusMessage { get => _statusMessage; private set { _statusMessage = value; OnPropertyChanged(); } }
    public string AccountEmail => _accounts.Account?.Email ?? "未登录";
    public string OrderIdText => string.IsNullOrWhiteSpace(_order?.OutTradeNo) ? "尚未创建" : _order.OutTradeNo;
    public bool CanSelectPlan => !_busy && !_polling;
    public bool CanOpenPayment => !_busy && !string.IsNullOrWhiteSpace(_order?.PayUrl);
    public bool CanRefresh => !_busy && !_polling && !string.IsNullOrWhiteSpace(_order?.OutTradeNo) && !_paid;
    public bool CanPurchase => !_busy && !_polling && SelectedPlan is not null && !_paid;
    public string PurchaseButtonText => _busy ? "正在创建订单…" : _polling ? "等待支付…" : _paid ? "支付成功" : "前往支付宝付款";
    public string PaymentGlyph => _paid ? "✓" : _polling ? "…" : "↗";
    public string PaymentPanelTitle => _paid ? "会员已开通" : _polling ? "支付宝收银台已打开" : "网页安全支付";
    public string PaymentPanelDescription => _paid
        ? "RVC Studio 账号权益已经刷新。"
        : _polling ? "请在浏览器中扫码或登录完成付款。" : "订单创建后会调用系统默认浏览器。";

    private async void Window_Opened(object? sender, EventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var plans = await _accounts.GetMembershipPlansAsync(_lifetimeCts.Token);
            Plans.Clear();
            foreach (var plan in plans) Plans.Add(plan);
            SelectedPlan = Plans.FirstOrDefault(item => item.Recommended) ?? Plans.FirstOrDefault();
        }, "加载会员套餐失败");
    }

    private async void Purchase_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanPurchase || SelectedPlan is null) return;
        await RunBusyAsync(async () =>
        {
            _order = await _accounts.CreateMembershipOrderAsync(SelectedPlan.Code, _lifetimeCts.Token);
            StatusTitle = "等待网页支付";
            StatusMessage = _order.Message;
            OpenUrl(_order.PayUrl);
            StartPolling();
        }, "创建订单失败");
    }

    private void OpenPayment_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_order?.PayUrl)) OpenUrl(_order.PayUrl);
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        await QueryStatusAsync(_lifetimeCts.Token);
    }

    private void StartPolling()
    {
        if (_polling) return;
        _polling = true;
        RefreshAll();
        _ = PollAsync(_lifetimeCts.Token);
    }

    private async Task PollAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _polling)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                await QueryStatusAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task QueryStatusAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_order?.OutTradeNo)) return;
        try
        {
            var result = await _accounts.QueryMembershipOrderAsync(_order.OutTradeNo, token);
            StatusMessage = result.Message;
            if (string.Equals(result.Status, "PAID", StringComparison.OrdinalIgnoreCase))
            {
                _polling = false;
                _paid = true;
                StatusTitle = "支付成功";
                StatusMessage = result.MembershipExpireDate.HasValue
                    ? $"RVC Studio 会员已开通，有效期至 {result.MembershipExpireDate:yyyy-MM-dd HH:mm}。"
                    : "RVC Studio 会员已开通，账号权益已经刷新。";
                await _accounts.RefreshAsync(token);
            }
            else if (string.Equals(result.Status, "CLOSED", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(result.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                _polling = false;
                StatusTitle = result.Status == "CLOSED" ? "订单已关闭" : "支付未完成";
            }
            RefreshAll();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _polling = false;
            StatusTitle = "查询支付状态失败";
            StatusMessage = exception.Message;
            RefreshAll();
        }
    }

    private async Task RunBusyAsync(Func<Task> action, string failureTitle)
    {
        _busy = true;
        RefreshAll();
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusTitle = failureTitle;
            StatusMessage = exception.Message;
        }
        finally
        {
            _busy = false;
            RefreshAll();
        }
    }

    private void OpenAgreement_Click(object? sender, RoutedEventArgs e) =>
        OpenUrl("https://thankful.top/protocol");

    private static void OpenUrl(string url)
    {
        var normalized = (url ?? string.Empty).Trim().TrimEnd(',', '，', '。');
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var parsed)
            && string.Equals(parsed.Host, "thankful.top", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(parsed) { Scheme = Uri.UriSchemeHttps };
            if (builder.Port is 80 or 4396) builder.Port = -1;
            normalized = builder.Uri.AbsoluteUri;
        }
        Process.Start(new ProcessStartInfo { FileName = normalized, UseShellExecute = true });
    }

    private void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        _polling = false;
        _lifetimeCts.Cancel();
        if (_ownsAccountService) _accounts.Dispose();
    }

    private void RefreshAll()
    {
        foreach (var property in new[]
        {
            nameof(SelectedPlan), nameof(AccountEmail), nameof(OrderIdText), nameof(CanSelectPlan),
            nameof(CanOpenPayment), nameof(CanRefresh), nameof(CanPurchase), nameof(PurchaseButtonText),
            nameof(PaymentGlyph), nameof(PaymentPanelTitle), nameof(PaymentPanelDescription)
        })
        {
            OnPropertyChanged(property);
        }
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
