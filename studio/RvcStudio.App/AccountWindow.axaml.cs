using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RvcStudio.App;

public partial class AccountWindow : Window, INotifyPropertyChanged
{
    private readonly AccountService _accounts;
    private readonly bool _ownsAccountService;
    private AccountMode _mode;
    private bool _busy;
    private string _statusText = string.Empty;
    private IBrush _statusBrush = new SolidColorBrush(Color.Parse("#B6D89B"));
    private int _countdown;
    private CancellationTokenSource? _countdownCts;

    public AccountWindow() : this(new AccountService(), true)
    {
    }

    public AccountWindow(AccountService accounts) : this(accounts, false)
    {
    }

    private AccountWindow(AccountService accounts, bool ownsAccountService)
    {
        InitializeComponent();
        _accounts = accounts;
        _ownsAccountService = ownsAccountService;
        _mode = accounts.IsAuthenticated ? AccountMode.Profile : AccountMode.Login;
        DataContext = this;
        RefreshAll();
    }

    public bool OpenMembershipRequested { get; private set; }
    public bool IsProfileMode => _mode == AccountMode.Profile;
    public bool IsLoginMode => _mode == AccountMode.Login;
    public bool IsRegisterMode => _mode == AccountMode.Register;
    public bool IsDeleteMode => _mode == AccountMode.Delete;
    public bool IsAuthMode => IsLoginMode || IsRegisterMode;
    public string HeaderTitle => _mode switch
    {
        AccountMode.Register => "注册账号",
        AccountMode.Delete => "注销账号",
        AccountMode.Profile => "账号中心",
        _ => "登录"
    };
    public string HeaderDescription => _mode switch
    {
        AccountMode.Register => "创建独立的 RVC Studio 账号。",
        AccountMode.Delete => "永久删除 RVC Studio 账号和会员权益。",
        AccountMode.Profile => "管理账号、会员和登录状态。",
        _ => "登录后开通并同步 RVC Studio 会员。"
    };
    public string HeaderGlyph => _mode switch
    {
        AccountMode.Register => "+",
        AccountMode.Delete => "!",
        AccountMode.Profile => ProfileInitial,
        _ => "→"
    };
    public string SubmitText => IsRegisterMode ? "注册并登录" : "登录";
    public string AccountFieldLabel => IsRegisterMode ? "邮箱" : "账号 / 邮箱";
    public string AccountPlaceholder => IsRegisterMode ? "name@example.com" : "用户名或 name@example.com";
    public bool CanSubmit => !_busy;
    public bool CanSendCode => !_busy && _countdown == 0;
    public string CodeButtonText => _countdown > 0 ? $"{_countdown}s 后重试" : "发送验证码";
    public bool HasStatus => !string.IsNullOrWhiteSpace(_statusText);
    public string StatusText => _statusText;
    public IBrush StatusBrush => _statusBrush;
    public string ProfileInitial => _accounts.Account?.Initial ?? "R";
    public string ProfileName => _accounts.Account?.DisplayName ?? "RVC 用户";
    public string ProfileEmail
    {
        get
        {
            var account = _accounts.Account;
            if (account is null) return string.Empty;
            return string.IsNullOrWhiteSpace(account.Username)
                ? account.Email
                : $"{account.Username} · {account.Email}";
        }
    }
    public string MembershipTitle => _accounts.Account?.IsMember == true ? "RVC Studio 会员" : "尚未开通会员";
    public string MembershipBadge => _accounts.Account?.IsMember == true ? "ACTIVE" : "FREE";
    public string MembershipDescription
    {
        get
        {
            var account = _accounts.Account;
            if (account?.IsMember != true) return "开通后即可使用 RVC Studio 会员权益。";
            var expire = account.MembershipExpireDate?.ToString("yyyy-MM-dd HH:mm") ?? "--";
            return $"{FormatMembershipType(account.MembershipType)} · 有效期至 {expire}";
        }
    }

    private void LoginMode_Click(object? sender, RoutedEventArgs e) => SetMode(AccountMode.Login);
    private void RegisterMode_Click(object? sender, RoutedEventArgs e) => SetMode(AccountMode.Register);
    private void DeleteMode_Click(object? sender, RoutedEventArgs e) => SetMode(AccountMode.Delete);
    private void BackToProfile_Click(object? sender, RoutedEventArgs e) => SetMode(AccountMode.Profile);

    private async void SubmitAuth_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var account = EmailBox.Text?.Trim() ?? string.Empty;
        var password = PasswordBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
        {
            ShowStatus(IsRegisterMode ? "请输入邮箱和密码。" : "请输入账号和密码。", true);
            return;
        }
        if (IsRegisterMode)
        {
            if (password != ConfirmPasswordBox.Text)
            {
                ShowStatus("两次输入的密码不一致。", true);
                return;
            }
            if (AgreementCheck.IsChecked != true)
            {
                ShowStatus("请先同意用户协议和隐私协议。", true);
                return;
            }
        }
        await RunBusyAsync(async () =>
        {
            if (IsRegisterMode)
            {
                await _accounts.RegisterAsync(
                    account,
                    DisplayNameBox.Text ?? string.Empty,
                    password,
                    RegisterCodeBox.Text ?? string.Empty);
                ShowStatus("注册并登录成功。", false);
            }
            else
            {
                await _accounts.LoginAsync(account, password);
                ShowStatus("登录成功。", false);
            }
            SetMode(AccountMode.Profile, clearStatus: false);
        });
    }

    private async void SendRegisterCode_Click(object? sender, RoutedEventArgs e) =>
        await SendCodeAsync(EmailBox.Text ?? string.Empty);

    private async void SendDeleteCode_Click(object? sender, RoutedEventArgs e) =>
        await SendCodeAsync(_accounts.Account?.Email ?? string.Empty);

    private async Task SendCodeAsync(string email)
    {
        if (_busy || _countdown > 0) return;
        if (string.IsNullOrWhiteSpace(email))
        {
            ShowStatus("请先填写邮箱。", true);
            return;
        }
        await RunBusyAsync(async () =>
        {
            await _accounts.SendVerificationCodeAsync(email);
            ShowStatus("验证码已发送，请检查邮箱和垃圾邮件目录。", false);
            StartCountdown();
        });
    }

    private async void Logout_Click(object? sender, RoutedEventArgs e)
    {
        await _accounts.LogoutAsync();
        Close();
    }

    private void OpenMembership_Click(object? sender, RoutedEventArgs e)
    {
        OpenMembershipRequested = true;
        Close();
    }

    private async void DeleteAccount_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (DeleteConfirmCheck.IsChecked != true)
        {
            ShowStatus("请确认永久注销当前账号。", true);
            return;
        }
        await RunBusyAsync(async () =>
        {
            await _accounts.DeleteAccountAsync(
                DeletePasswordBox.Text ?? string.Empty,
                DeleteCodeBox.Text ?? string.Empty);
            Close();
        });
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        _busy = true;
        RefreshAll();
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, true);
        }
        finally
        {
            _busy = false;
            RefreshAll();
        }
    }

    private void SetMode(AccountMode mode, bool clearStatus = true)
    {
        _mode = mode;
        if (clearStatus) _statusText = string.Empty;
        PasswordBox.Text = string.Empty;
        ConfirmPasswordBox.Text = string.Empty;
        DeletePasswordBox.Text = string.Empty;
        DeleteCodeBox.Text = string.Empty;
        if (_accounts.Account is not null) EmailBox.Text = _accounts.Account.Email;
        RefreshAll();
    }

    private void ShowStatus(string message, bool error)
    {
        _statusText = message;
        _statusBrush = new SolidColorBrush(Color.Parse(error ? "#FFAAA2" : "#B6E58F"));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(HasStatus));
    }

    private void StartCountdown()
    {
        _countdownCts?.Cancel();
        _countdownCts?.Dispose();
        _countdownCts = new CancellationTokenSource();
        _countdown = 60;
        _ = RunCountdownAsync(_countdownCts.Token);
        RefreshAll();
    }

    private async Task RunCountdownAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (_countdown > 0 && await timer.WaitForNextTickAsync(token))
            {
                _countdown--;
                OnPropertyChanged(nameof(CanSendCode));
                OnPropertyChanged(nameof(CodeButtonText));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RefreshAll()
    {
        foreach (var property in new[]
        {
            nameof(IsProfileMode), nameof(IsLoginMode), nameof(IsRegisterMode), nameof(IsDeleteMode), nameof(IsAuthMode),
            nameof(HeaderTitle), nameof(HeaderDescription), nameof(HeaderGlyph), nameof(SubmitText),
            nameof(AccountFieldLabel), nameof(AccountPlaceholder), nameof(CanSubmit),
            nameof(CanSendCode), nameof(CodeButtonText), nameof(HasStatus), nameof(StatusText), nameof(StatusBrush),
            nameof(ProfileInitial), nameof(ProfileName), nameof(ProfileEmail), nameof(MembershipTitle),
            nameof(MembershipBadge), nameof(MembershipDescription)
        })
        {
            OnPropertyChanged(property);
        }
    }

    private static string FormatMembershipType(string type) => type switch
    {
        "RVC_STUDIO_MONTHLY" => "月度会员",
        "RVC_STUDIO_QUARTERLY" => "季度会员",
        "RVC_STUDIO_YEARLY" => "年度会员",
        _ => "RVC Studio 会员"
    };

    protected override void OnClosed(EventArgs e)
    {
        _countdownCts?.Cancel();
        _countdownCts?.Dispose();
        if (_ownsAccountService) _accounts.Dispose();
        base.OnClosed(e);
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private enum AccountMode
    {
        Login,
        Register,
        Profile,
        Delete
    }
}
