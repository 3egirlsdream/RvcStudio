using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RvcStudio.App;

public sealed class RvcStudioApiClient : IDisposable
{
    private const string DefaultApiUrl = "https://thankful.top";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly HttpClient _client;

    public RvcStudioApiClient()
    {
        var configuredUrl = Environment.GetEnvironmentVariable("RVC_STUDIO_API_URL");
        var baseUrl = string.IsNullOrWhiteSpace(configuredUrl) ? DefaultApiUrl : configuredUrl.Trim();
        _client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(25)
        };
    }

    public Task<bool> SendVerificationCodeAsync(string email, CancellationToken token = default) =>
        GetAsync<bool>($"api/RvcStudio/SendVerificationCode?email={Uri.EscapeDataString(email)}", null, token);

    public Task<AuthResult> RegisterAsync(
        string email,
        string displayName,
        string password,
        string verificationCode,
        CancellationToken token = default) =>
        PostAsync<AuthResult>("api/RvcStudio/Register", new
        {
            Email = email,
            DisplayName = displayName,
            Password = password,
            VerificationCode = verificationCode
        }, null, token);

    public Task<AuthResult> LoginAsync(string account, string password, CancellationToken token = default) =>
        PostAsync<AuthResult>("api/RvcStudio/Login", new { Account = account, Password = password }, null, token);

    public Task<AccountProfile> GetAccountAsync(string authToken, CancellationToken token = default) =>
        GetAsync<AccountProfile>("api/RvcStudio/Account", authToken, token);

    public Task<bool> DeleteAccountAsync(
        string authToken,
        string password,
        string verificationCode,
        CancellationToken token = default) =>
        PostAsync<bool>("api/RvcStudio/DeleteAccount", new
        {
            Password = password,
            VerificationCode = verificationCode
        }, authToken, token);

    public Task<IReadOnlyList<MembershipPlan>> GetMembershipPlansAsync(CancellationToken token = default) =>
        GetAsync<IReadOnlyList<MembershipPlan>>("api/RvcStudio/MembershipPlans", null, token);

    public Task<MembershipOrder> CreateMembershipOrderAsync(
        string authToken,
        string planCode,
        CancellationToken token = default) =>
        PostAsync<MembershipOrder>(
            "api/RvcStudio/CreateMembershipOrder",
            new { PlanCode = planCode },
            authToken,
            token);

    public Task<MembershipOrderStatus> QueryMembershipOrderAsync(
        string authToken,
        string outTradeNo,
        CancellationToken token = default) =>
        GetAsync<MembershipOrderStatus>(
            $"api/RvcStudio/QueryMembershipOrder?outTradeNo={Uri.EscapeDataString(outTradeNo)}",
            authToken,
            token);

    private async Task<T> GetAsync<T>(string path, string? authToken, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        AddAuthorization(request, authToken);
        return await SendAsync<T>(request, token);
    }

    private async Task<T> PostAsync<T>(string path, object body, string? authToken, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        AddAuthorization(request, authToken);
        return await SendAsync<T>(request, token);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken token)
    {
        using var response = await _client.SendAsync(request, token);
        var json = await response.Content.ReadAsStringAsync(token);
        ApiEnvelope<T>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ApiEnvelope<T>>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new RvcStudioApiException($"服务端返回了无法识别的数据：{exception.Message}");
        }
        if (!response.IsSuccessStatusCode || envelope?.Success != true || envelope.Data is null)
        {
            var message = envelope?.Message?.Content;
            throw new RvcStudioApiException(string.IsNullOrWhiteSpace(message)
                ? $"请求失败（HTTP {(int)response.StatusCode}）"
                : message);
        }
        return envelope.Data;
    }

    private static void AddAuthorization(HttpRequestMessage request, string? authToken)
    {
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        }
    }

    public void Dispose() => _client.Dispose();
}
