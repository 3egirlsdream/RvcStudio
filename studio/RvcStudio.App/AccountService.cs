using System.Security.Cryptography;
using System.Text.Json;

namespace RvcStudio.App;

public sealed class AccountService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly RvcStudioApiClient _api = new();
    private readonly string _sessionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RvcStudio",
        "account.dat");
    private readonly string _legacySessionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RvcStudio",
        "account.json");

    public event Action? Changed;
    public AccountProfile? Account { get; private set; }
    public string Token { get; private set; } = string.Empty;
    public bool IsAuthenticated => Account is not null && !string.IsNullOrWhiteSpace(Token);

    public async Task InitializeAsync(CancellationToken token = default)
    {
        if (!File.Exists(_sessionPath) && !File.Exists(_legacySessionPath)) return;
        try
        {
            AccountSession? session;
            var migratedLegacySession = !File.Exists(_sessionPath) && File.Exists(_legacySessionPath);
            if (migratedLegacySession)
            {
                var json = await File.ReadAllTextAsync(_legacySessionPath, token);
                session = JsonSerializer.Deserialize<AccountSession>(json, JsonOptions);
            }
            else
            {
                session = await ProtectedLocalStorage.ReadJsonAsync<AccountSession>(_sessionPath, token);
            }
            if (session is null || string.IsNullOrWhiteSpace(session.Token)) return;
            Token = session.Token;
            Account = session.Account;
            if (migratedLegacySession)
            {
                await SaveSessionAsync();
            }
            TryDeleteLegacySession();
            Changed?.Invoke();
            await RefreshAsync(token);
        }
        catch (RvcStudioApiException)
        {
            await ClearSessionAsync();
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException)
        {
            await ClearSessionAsync();
        }
        catch (IOException)
        {
            // Keep the app usable when local storage is temporarily unavailable.
        }
        catch (HttpRequestException)
        {
            // Preserve the cached session while the service is temporarily offline.
        }
    }

    public async Task LoginAsync(string account, string password, CancellationToken token = default)
    {
        var result = await _api.LoginAsync(account.Trim(), password, token);
        await ApplyAuthAsync(result);
    }

    public async Task RegisterAsync(
        string email,
        string displayName,
        string password,
        string verificationCode,
        CancellationToken token = default)
    {
        var result = await _api.RegisterAsync(
            email.Trim(),
            displayName.Trim(),
            password,
            verificationCode.Trim(),
            token);
        await ApplyAuthAsync(result);
    }

    public Task SendVerificationCodeAsync(string email, CancellationToken token = default) =>
        _api.SendVerificationCodeAsync(email.Trim(), token);

    public async Task RefreshAsync(CancellationToken token = default)
    {
        if (!IsAuthenticated) return;
        Account = await _api.GetAccountAsync(Token, token);
        await SaveSessionAsync();
        Changed?.Invoke();
    }

    public async Task DeleteAccountAsync(string password, string verificationCode, CancellationToken token = default)
    {
        EnsureAuthenticated();
        await _api.DeleteAccountAsync(Token, password, verificationCode.Trim(), token);
        await ClearSessionAsync();
    }

    public Task<IReadOnlyList<MembershipPlan>> GetMembershipPlansAsync(CancellationToken token = default) =>
        _api.GetMembershipPlansAsync(token);

    public Task<MembershipOrder> CreateMembershipOrderAsync(string planCode, CancellationToken token = default)
    {
        EnsureAuthenticated();
        return _api.CreateMembershipOrderAsync(Token, planCode, token);
    }

    public Task<MembershipOrderStatus> QueryMembershipOrderAsync(string orderId, CancellationToken token = default)
    {
        EnsureAuthenticated();
        return _api.QueryMembershipOrderAsync(Token, orderId, token);
    }

    public Task LogoutAsync() => ClearSessionAsync();

    private async Task ApplyAuthAsync(AuthResult result)
    {
        Token = result.Token;
        Account = result.Account;
        await SaveSessionAsync();
        Changed?.Invoke();
    }

    private async Task SaveSessionAsync()
    {
        if (!IsAuthenticated) return;
        await ProtectedLocalStorage.WriteJsonAsync(
            _sessionPath,
            new AccountSession { Token = Token, Account = Account! });
        TryDeleteLegacySession();
    }

    private async Task ClearSessionAsync()
    {
        Token = string.Empty;
        Account = null;
        try
        {
            TryDeleteSessionFile(_sessionPath);
            TryDeleteSessionFile(_legacySessionPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The in-memory session is already cleared; a locked cache can be retried next launch.
        }
        Changed?.Invoke();
        await Task.CompletedTask;
    }

    private void EnsureAuthenticated()
    {
        if (!IsAuthenticated) throw new RvcStudioApiException("请先登录 RVC Studio 账号。");
    }

    private static void TryDeleteSessionFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private void TryDeleteLegacySession()
    {
        try
        {
            TryDeleteSessionFile(_legacySessionPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The encrypted session is already authoritative. Retry removing
            // the obsolete plaintext cache on a later save or launch.
        }
    }

    public void Dispose() => _api.Dispose();
}
