namespace RvcStudio.App;

public sealed class AppUpdateService : IDisposable
{
    public const string Channel = "RvcStudio";
    private readonly HttpClient _client;
    private readonly UpdateTrust _trust;
    private readonly string _root;
    public AppUpdateService() : this(new HttpClient(), UpdateTrust.Load(), AppContext.BaseDirectory) { }
    public AppUpdateService(HttpClient client, UpdateTrust trust, string root)
    {
        _client = client;
        _client.Timeout = Timeout.InfiniteTimeSpan;
        _trust = trust;
        _root = root;
    }

    public async Task<AppUpdateResult> CheckAsync(CancellationToken token = default)
    {
        var url = Environment.GetEnvironmentVariable("RVC_STUDIO_UPDATE_MANIFEST_URL") ?? _trust.ManifestUrl;
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(_trust.PublicKeyPem))
            throw new InvalidOperationException("此构建未配置签名更新源，请安装正式完整版。");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new InvalidDataException("更新源必须使用 HTTPS。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var data = new MemoryStream();
        await CopyLimitedAsync(await response.Content.ReadAsStreamAsync(timeout.Token), data, 4 * 1024 * 1024, null, timeout.Token);
        var envelope = System.Text.Encoding.UTF8.GetString(data.ToArray());
        var manifest = UpdatePackage.Verify(envelope, _trust.PublicKeyPem);
        UpdateBaseline? baseline;
        try { baseline = UpdatePackage.ReadBaseline(_root); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) { baseline = null; }
        return new AppUpdateResult(Normalize(Version.Parse(manifest.Version)) > Normalize(CurrentVersion), CurrentVersion,
            Version.Parse(manifest.Version), manifest.Notes, manifest, envelope, UpdatePackage.Compatible(manifest, baseline));
    }

    public async Task<string> DownloadAsync(AppUpdateResult update, IProgress<double>? progress, CancellationToken token)
    {
        if (!update.CanUseSmallPackage || update.Envelope is null) throw new InvalidOperationException("需要完整安装包。");
        var manifest = UpdatePackage.Verify(update.Envelope, _trust.PublicKeyPem);
        var directory = Path.Combine(UpdatePackage.CacheRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory, "package.part");
        try
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            requestTimeout.CancelAfter(TimeSpan.FromMinutes(30));
            using var response = await _client.GetAsync(manifest.Package.Url, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long size && size != manifest.Package.Size) throw new InvalidDataException("下载文件大小不符。");
            await using (var output = File.Create(partial))
                await CopyLimitedAsync(await response.Content.ReadAsStreamAsync(requestTimeout.Token), output, manifest.Package.Size, progress, requestTimeout.Token);
            UpdatePackage.VerifyFile(partial, manifest.Package.Size, manifest.Package.Sha256);
            var extracted = Path.Combine(directory, "preview");
            await Task.Run(() => UpdatePackage.Extract(partial, extracted, manifest), token);
            Directory.Delete(extracted, true);
            File.Move(partial, Path.Combine(directory, "package.zip"));
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), update.Envelope, token);
            return directory;
        }
        catch
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            throw;
        }
    }

    private static async Task CopyLimitedAsync(Stream input, Stream output, long limit, IProgress<double>? progress, CancellationToken token)
    {
        using (input)
        {
            var buffer = new byte[128 * 1024];
            long total = 0;
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
                stall.CancelAfter(TimeSpan.FromSeconds(30));
                var count = await input.ReadAsync(buffer, stall.Token);
                if (count == 0) break;
                total += count;
                if (total > limit) throw new InvalidDataException("下载数据超过声明大小。");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                progress?.Report((double)total / limit * 100);
            }
        }
    }

    public static Version CurrentVersion => typeof(AppUpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    public void Dispose() => _client.Dispose();
}

public sealed record AppUpdateResult(bool IsAvailable, Version CurrentVersion, Version AvailableVersion, string Memo,
    UpdateManifest? Manifest = null, string? Envelope = null, bool CanUseSmallPackage = false)
{
    public string CurrentVersionText => FormatVersion(CurrentVersion);
    public string AvailableVersionText => FormatVersion(AvailableVersion);
    public static AppUpdateResult NotAvailable(Version currentVersion) => new(false, currentVersion, currentVersion, string.Empty);
    private static string FormatVersion(Version version) => version.Revision > 0 ? version.ToString(4) : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
}
