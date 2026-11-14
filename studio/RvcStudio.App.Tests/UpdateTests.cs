using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RvcStudio.App;
using Xunit;

namespace RvcStudio.App.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rvc-update-test-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _key = RSA.Create(2048);
    private const string EnvironmentId = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    public UpdateTests() => Directory.CreateDirectory(_root);
    private string At(string path) => Path.Combine(_root, path);
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private UpdateFile FileRecord(string name, string contents) => new(name, Encoding.UTF8.GetByteCount(contents), Hash(Encoding.UTF8.GetBytes(contents)));
    private UpdateArtifact Artifact(byte[] data) => new("https://example.test/update.zip", data.Length, Hash(data));
    private UpdateManifest Manifest(params UpdateFile[] files) => new(1, "2.0.0", "test", EnvironmentId, Artifact([1]), Artifact([2]), files.Length == 0 ? [FileRecord(UpdatePackage.ExeName, "new")] : files);
    private string Sign(UpdateManifest manifest)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdatePackage.Json);
        return JsonSerializer.Serialize(new SignedUpdate(Convert.ToBase64String(data), Convert.ToBase64String(_key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))), UpdatePackage.Json);
    }
    private void Write(string path, string value) { Directory.CreateDirectory(Path.GetDirectoryName(At(path))!); System.IO.File.WriteAllText(At(path), value); }
    private void Baseline(params UpdateFile[] files) => Write(UpdatePackage.BaselineName, JsonSerializer.Serialize(new UpdateBaseline("1.1.0", EnvironmentId, files), UpdatePackage.Json));
    private byte[] Zip(params (string path, string contents)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (path, contents) in files) { using var writer = new StreamWriter(archive.CreateEntry(path).Open()); writer.Write(contents); }
        return output.ToArray();
    }

    [Fact]
    public void SignedManifest_RequiresTrustedKeyAndUnmodifiedPayload()
    {
        var signed = Sign(Manifest());
        Assert.Equal("2.0.0", UpdatePackage.Verify(signed, _key.ExportSubjectPublicKeyInfoPem()).Version);
        using var other = RSA.Create(2048);
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Verify(signed, other.ExportSubjectPublicKeyInfoPem()));
        var envelope = JsonSerializer.Deserialize<SignedUpdate>(signed, UpdatePackage.Json)!;
        var tampered = envelope with { Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")) };
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Verify(JsonSerializer.Serialize(tampered, UpdatePackage.Json), _key.ExportSubjectPublicKeyInfoPem()));
    }

    [Theory]
    [InlineData("../LICENSE")]
    [InlineData("engine/../../LICENSE")]
    [InlineData("engine\\evil.py")]
    [InlineData("engine/evil.py:stream")]
    [InlineData("engine/CON.py")]
    [InlineData("engine/evil.py.")]
    [InlineData("assets/weights/custom.pth")]
    [InlineData("configs/user.json")]
    [InlineData("configs/config.json")]
    [InlineData("runtime/python.exe")]
    [InlineData("update-baseline.json")]
    public void SignedManifest_CannotAuthorizeProtectedOrEscapingPaths(string path)
    {
        Assert.False(UpdatePackage.IsManagedPath(path));
        var manifest = Manifest(FileRecord(UpdatePackage.ExeName, "new"), FileRecord(path, "evil"));
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Verify(Sign(manifest), _key.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void SignedManifest_RejectsDuplicateCaseAliasesAndBadSchema()
    {
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Verify(Sign(Manifest(FileRecord(UpdatePackage.ExeName, "x"), FileRecord("engine/a.py", "a"), FileRecord("engine/A.py", "b"))), _key.ExportSubjectPublicKeyInfoPem()));
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Verify(Sign(Manifest() with { Schema = 9 }), _key.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void ManagedConfiguration_OnlyIncludesKnownArchitectureFiles()
    {
        Assert.True(UpdatePackage.IsManagedPath("configs/v1/32k.json"));
        Assert.True(UpdatePackage.IsManagedPath("configs/v2/48k.json"));
        Assert.False(UpdatePackage.IsManagedPath("configs/v1/custom.json"));
        Assert.False(UpdatePackage.IsManagedPath("configs/config.json"));
    }

    [Fact]
    public void Package_ValidatesWholeArchiveAndEachFile()
    {
        var bytes = Zip((UpdatePackage.ExeName, "new"), ("engine/main.py", "code"));
        System.IO.File.WriteAllBytes(At("update.zip"), bytes);
        var manifest = Manifest(FileRecord(UpdatePackage.ExeName, "new"), FileRecord("engine/main.py", "code")) with { Package = Artifact(bytes) };
        UpdatePackage.Extract(At("update.zip"), At("prepared"), manifest);
        Assert.Equal("code", System.IO.File.ReadAllText(At("prepared/engine/main.py")));
        var badFiles = manifest with { Files = [FileRecord(UpdatePackage.ExeName, "bad"), FileRecord("engine/main.py", "code")] };
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Extract(At("update.zip"), At("bad-files"), badFiles));
        bytes[^1] ^= 1;
        System.IO.File.WriteAllBytes(At("update.zip"), bytes);
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Extract(At("update.zip"), At("corrupt"), manifest));
    }

    [Theory]
    [InlineData("../escape.py")]
    [InlineData("assets/weights/custom.pth")]
    [InlineData("engine/extra.py")]
    public void Package_RejectsUnexpectedEntriesEvenWhenArchiveHashMatches(string extra)
    {
        var bytes = Zip((extra, "evil"));
        System.IO.File.WriteAllBytes(At("update.zip"), bytes);
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Extract(At("update.zip"), At("prepared"), Manifest() with { Package = Artifact(bytes) }));
        Assert.False(System.IO.File.Exists(At("escape.py")));
    }

    [Fact]
    public void Transaction_CrossVersionUpgradeRemovesOnlyRetiredManagedFiles()
    {
        Write(UpdatePackage.ExeName, "old"); Write("engine/retired.py", "retired"); Write("engine/unmanaged.py", "user");
        Write("assets/weights/custom.pth", "model"); Write("configs/user.json", "settings");
        Baseline(FileRecord(UpdatePackage.ExeName, "old"), FileRecord("engine/retired.py", "retired"));
        Write("prepared/" + UpdatePackage.ExeName, "new"); Write("prepared/engine/current.py", "current");
        UpdateTransaction.Apply(_root, At("prepared"), Manifest(FileRecord(UpdatePackage.ExeName, "new"), FileRecord("engine/current.py", "current")));
        Assert.Equal("new", System.IO.File.ReadAllText(At(UpdatePackage.ExeName)));
        Assert.False(System.IO.File.Exists(At("engine/retired.py")));
        Assert.Equal("user", System.IO.File.ReadAllText(At("engine/unmanaged.py")));
        Assert.Equal("model", System.IO.File.ReadAllText(At("assets/weights/custom.pth")));
        Assert.Equal("settings", System.IO.File.ReadAllText(At("configs/user.json")));
        Assert.Equal("2.0.0", UpdatePackage.ReadBaseline(_root)!.Version);
        Assert.False(UpdateTransaction.NeedsRecovery(_root));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Transaction_WriteOrHealthCheckFailureRestoresEveryFileAndBaseline(bool writeFailure)
    {
        Write(UpdatePackage.ExeName, "old"); Write("engine/retired.py", "retired");
        Baseline(FileRecord(UpdatePackage.ExeName, "old"), FileRecord("engine/retired.py", "retired"));
        var before = System.IO.File.ReadAllText(At(UpdatePackage.BaselineName));
        Write("prepared/engine/added.py", "added"); Write("prepared/" + UpdatePackage.ExeName, "new");
        var manifest = Manifest(FileRecord("engine/added.py", "added"), FileRecord(UpdatePackage.ExeName, "new"));
        Assert.Throws<IOException>(() => UpdateTransaction.Apply(_root, At("prepared"), manifest,
            _ => throw new IOException("health failed"), writeFailure ? _ => throw new IOException("write failed") : null));
        Assert.Equal("old", System.IO.File.ReadAllText(At(UpdatePackage.ExeName)));
        Assert.Equal("retired", System.IO.File.ReadAllText(At("engine/retired.py")));
        Assert.False(System.IO.File.Exists(At("engine/added.py")));
        Assert.Equal(before, System.IO.File.ReadAllText(At(UpdatePackage.BaselineName)));
    }

    [Fact]
    public void Transaction_RejectsEnvironmentChangeDowngradeAndLockedFilesBeforeMutation()
    {
        Write(UpdatePackage.ExeName, "old"); Baseline(FileRecord(UpdatePackage.ExeName, "old"));
        Write("prepared/" + UpdatePackage.ExeName, "new");
        Assert.Throws<InvalidDataException>(() => UpdateTransaction.Apply(_root, At("prepared"), Manifest() with { EnvironmentId = new string('B', 64) }));
        Assert.Throws<InvalidDataException>(() => UpdateTransaction.Apply(_root, At("prepared"), Manifest() with { Version = "1.0.0" }));
        using (System.IO.File.Open(At(UpdatePackage.ExeName), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => UpdateTransaction.Apply(_root, At("prepared"), Manifest()));
        Assert.Equal("old", System.IO.File.ReadAllText(At(UpdatePackage.ExeName)));
        Assert.False(UpdateTransaction.NeedsRecovery(_root));
    }

    [Fact]
    public void Recovery_RestoresInterruptedTransactionAndCanRepeatSafely()
    {
        Write(UpdatePackage.ExeName, "partially installed");
        Write("engine/added.py", "new");
        Write(".rvc-update/backup/" + UpdatePackage.ExeName, "old");
        var journal = new UpdateJournal("applying", [new(UpdatePackage.ExeName, true, 3, Hash("old"u8.ToArray())), new("engine/added.py", false, 0, "")]);
        Write(".rvc-update/journal.json", JsonSerializer.Serialize(journal, UpdatePackage.Json));
        Assert.True(UpdateTransaction.NeedsRecovery(_root));
        UpdateTransaction.Recover(_root); UpdateTransaction.Recover(_root);
        Assert.Equal("old", System.IO.File.ReadAllText(At(UpdatePackage.ExeName)));
        Assert.False(System.IO.File.Exists(At("engine/added.py")));
    }

    [Fact]
    public void Recovery_ValidatesAllBackupsBeforeRestoringAnything()
    {
        Write(UpdatePackage.ExeName, "new");
        Write(".rvc-update/backup/" + UpdatePackage.ExeName, "damaged");
        Write(".rvc-update/journal.json", JsonSerializer.Serialize(new UpdateJournal("applying", [new(UpdatePackage.ExeName, true, 3, Hash("old"u8.ToArray()))]), UpdatePackage.Json));
        Assert.Throws<InvalidDataException>(() => UpdateTransaction.Recover(_root));
        Assert.Equal("new", System.IO.File.ReadAllText(At(UpdatePackage.ExeName)));
        Assert.True(UpdateTransaction.NeedsRecovery(_root));
    }

    [Fact]
    public void Recovery_CommittedJournalOnlyCleansUp()
    {
        Write(UpdatePackage.ExeName, "new");
        Write(".rvc-update/journal.json", JsonSerializer.Serialize(new UpdateJournal("committed", []), UpdatePackage.Json));
        UpdateTransaction.Recover(_root);
        Assert.Equal("new", System.IO.File.ReadAllText(At(UpdatePackage.ExeName)));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Check_ChoosesSmallPackageOnlyForCompatibleBaseline(bool compatible, bool expected)
    {
        Baseline(FileRecord(UpdatePackage.ExeName, "old"));
        var manifest = Manifest() with { EnvironmentId = compatible ? EnvironmentId : new string('B', 64) };
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sign(manifest)) }));
        using var service = new AppUpdateService(client, new("https://example.test/stable.json", _key.ExportSubjectPublicKeyInfoPem()), _root);
        var result = await service.CheckAsync();
        Assert.True(result.IsAvailable);
        Assert.Equal(expected, result.CanUseSmallPackage);
    }

    [Fact]
    public async Task Check_MissingBaselineUsesFullInstallerAndInvalidSignatureFailsClosed()
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sign(Manifest())) }));
        using var service = new AppUpdateService(client, new("https://example.test/stable.json", _key.ExportSubjectPublicKeyInfoPem()), _root);
        Assert.False((await service.CheckAsync()).CanUseSmallPackage);
        using var other = RSA.Create(2048);
        using var untrusted = new AppUpdateService(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sign(Manifest())) })), new("https://example.test/stable.json", other.ExportSubjectPublicKeyInfoPem()), _root);
        await Assert.ThrowsAsync<InvalidDataException>(() => untrusted.CheckAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{broken")]
    public async Task Check_DamagedBaselineFallsBackToFullInstaller(string baseline)
    {
        Write(UpdatePackage.BaselineName, baseline);
        using var service = new AppUpdateService(new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Sign(Manifest())) })),
            new("https://example.test/stable.json", _key.ExportSubjectPublicKeyInfoPem()), _root);
        Assert.False((await service.CheckAsync()).CanUseSmallPackage);
    }

    [Fact]
    public async Task Check_CurrentVersionDoesNotOfferAnUpdate()
    {
        var manifest = Manifest() with { Version = AppUpdateService.CurrentVersion.ToString() };
        using var service = new AppUpdateService(new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Sign(manifest)) })),
            new("https://example.test/stable.json", _key.ExportSubjectPublicKeyInfoPem()), _root);
        Assert.False((await service.CheckAsync()).IsAvailable);
    }

    [Fact]
    public async Task Download_RequestsOnlySmallPackageThenRevalidatesArchive()
    {
        var bytes = Zip((UpdatePackage.ExeName, "new"));
        var manifest = Manifest() with { Package = Artifact(bytes) };
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(request => { requests.Add(request.RequestUri!.ToString()); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; }));
        using var service = new AppUpdateService(client, new("https://example.test/stable.json", _key.ExportSubjectPublicKeyInfoPem()), _root);
        var result = new AppUpdateResult(true, new(1, 1, 0), new(2, 0, 0), "", manifest, Sign(manifest), true);
        var directory = await service.DownloadAsync(result, null, CancellationToken.None);
        try { Assert.Single(requests); Assert.Equal(manifest.Package.Url, requests[0]); Assert.True(System.IO.File.Exists(Path.Combine(directory, "manifest.json"))); }
        finally { Directory.Delete(directory, true); }
        bytes[0] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(result, null, CancellationToken.None));
    }

    [Fact]
    public async Task Download_CanCancelAndRetryWithoutChangingInstallation()
    {
        var bytes = Zip((UpdatePackage.ExeName, "new"));
        var manifest = Manifest() with { Package = Artifact(bytes) };
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        using var service = new AppUpdateService(client, new("https://example.test/stable.json", _key.ExportSubjectPublicKeyInfoPem()), _root);
        var result = new AppUpdateResult(true, new(1, 1, 0), new(2, 0, 0), "", manifest, Sign(manifest), true);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(result, null, cancel.Token));
        var directory = await service.DownloadAsync(result, null, CancellationToken.None);
        Directory.Delete(directory, true);
        Assert.False(System.IO.File.Exists(At(UpdatePackage.ExeName)));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    public void Dispose() { _key.Dispose(); Directory.Delete(_root, true); }
}
