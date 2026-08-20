using System.Reflection;
using System.Text.Json;

namespace RvcStudio.App;

public sealed class AppUpdateService : IDisposable
{
    public const string Channel = "RvcStudio";
    public const string QqGroupNumber = "791129392";

    private const string DefaultApiUrl = "https://thankful.top";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly HttpClient _client;

    public AppUpdateService()
    {
        var configuredUrl = Environment.GetEnvironmentVariable("RVC_STUDIO_UPDATE_API_URL");
        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            configuredUrl = Environment.GetEnvironmentVariable("RVC_STUDIO_API_URL");
        }
        var baseUrl = string.IsNullOrWhiteSpace(configuredUrl) ? DefaultApiUrl : configuredUrl.Trim();
        _client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<AppUpdateResult> CheckAsync(CancellationToken token = default)
    {
        var currentVersion = CurrentVersion;
        var path = $"api/CloudSync/GetVersion?Client={Uri.EscapeDataString(Channel)}";
        using var response = await _client.GetAsync(path, token);
        var json = await response.Content.ReadAsStringAsync(token);

        ApiEnvelope<AppUpdateVersion>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ApiEnvelope<AppUpdateVersion>>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new RvcStudioApiException($"更新服务器返回了无法识别的数据：{exception.Message}");
        }

        if (!response.IsSuccessStatusCode || envelope?.Success != true)
        {
            var message = envelope?.Message?.Content;
            throw new RvcStudioApiException(string.IsNullOrWhiteSpace(message)
                ? $"检查更新失败（HTTP {(int)response.StatusCode}）"
                : message);
        }

        // A new product channel legitimately has no row until the first successful package is built.
        if (envelope.Data is null || string.IsNullOrWhiteSpace(envelope.Data.Version))
        {
            return AppUpdateResult.NotAvailable(currentVersion);
        }
        if (!Version.TryParse(envelope.Data.Version.Trim(), out var availableVersion))
        {
            throw new RvcStudioApiException($"更新服务器返回了无效版本号：{envelope.Data.Version}");
        }

        return new AppUpdateResult(
            Normalize(availableVersion) > Normalize(currentVersion),
            currentVersion,
            availableVersion,
            envelope.Data.Memo?.Trim() ?? string.Empty);
    }

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version
        ?? Assembly.GetExecutingAssembly().GetName().Version
        ?? new Version(1, 0, 0);

    private static Version Normalize(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    public void Dispose() => _client.Dispose();
}

public sealed class AppUpdateVersion
{
    public string Version { get; set; } = string.Empty;
    public string Memo { get; set; } = string.Empty;
}

public sealed record AppUpdateResult(
    bool IsAvailable,
    Version CurrentVersion,
    Version AvailableVersion,
    string Memo)
{
    public string CurrentVersionText => FormatVersion(CurrentVersion);
    public string AvailableVersionText => FormatVersion(AvailableVersion);

    public static AppUpdateResult NotAvailable(Version currentVersion) =>
        new(false, currentVersion, currentVersion, string.Empty);

    private static string FormatVersion(Version version) =>
        version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}.{version.Revision}"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
}
