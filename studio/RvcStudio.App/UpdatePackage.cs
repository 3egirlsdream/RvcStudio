using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace RvcStudio.App;

public sealed record UpdateArtifact(string Url, long Size, string Sha256);
public sealed record UpdateFile(string Path, long Size, string Sha256);
public sealed record UpdateBaseline(string Version, string EnvironmentId, UpdateFile[] Files);
public sealed record UpdateManifest(int Schema, string Version, string Notes, string EnvironmentId,
    UpdateArtifact Package, UpdateArtifact Installer, UpdateFile[] Files);
public sealed record SignedUpdate(string Payload, string Signature);
public sealed record UpdateTrust(string ManifestUrl, string PublicKeyPem)
{
    public static UpdateTrust Load()
    {
        using var stream = typeof(UpdateTrust).Assembly.GetManifestResourceStream("RvcStudio.UpdateTrust.json")!;
        return JsonSerializer.Deserialize<UpdateTrust>(stream, UpdatePackage.Json)!;
    }
}

public static class UpdatePackage
{
    public const string BaselineName = "update-baseline.json";
    public const string ExeName = "RVC Studio.exe";
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string CacheRoot => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RvcStudio", "Updates");

    public static UpdateManifest Verify(string envelope, string publicKey)
    {
        var signed = JsonSerializer.Deserialize<SignedUpdate>(envelope, Json) ?? throw new InvalidDataException("更新清单为空。");
        var payload = Convert.FromBase64String(signed.Payload);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKey);
        if (rsa.KeySize < 2048 || !rsa.VerifyData(payload, Convert.FromBase64String(signed.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("更新清单签名验证失败。");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(payload, Json) ?? throw new InvalidDataException("更新清单为空。");
        if (manifest.Schema != 1 || !Version.TryParse(manifest.Version, out _) || !HashValid(manifest.EnvironmentId))
            throw new InvalidDataException("更新清单版本无效。");
        ValidateArtifact(manifest.Package);
        ValidateArtifact(manifest.Installer);
        ValidateFiles(manifest.Files);
        return manifest;
    }

    private static bool HashValid(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static void ValidateArtifact(UpdateArtifact artifact)
    {
        if (artifact is null || artifact.Size <= 0 || !HashValid(artifact.Sha256) || !Uri.TryCreate(artifact.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0)
            throw new InvalidDataException("更新文件信息无效。");
    }

    // This allowlist is also enforced on signed packages: user data is never an update target.
    public static bool IsManagedPath(string path)
    {
        if (!IsSafeRelativePath(path) || path.Length > 220) return false;
        var parts = path.Split('/');
        if (path is ExeName or "realtime_service.py" or "LICENSE" or "THIRD-PARTY-NOTICES.txt") return true;
        if (parts.Length == 1 && path.StartsWith("MIT", StringComparison.Ordinal) && !path.Contains('.')) return true;
        if (parts.Length < 2) return false;
        return parts[0] switch
        {
            "engine" or "infer" or "i18n" or "tools" => path.EndsWith(".py", StringComparison.Ordinal),
            "configs" => path.EndsWith(".py", StringComparison.Ordinal) || path is "configs/v1/32k.json" or "configs/v1/40k.json" or "configs/v1/48k.json" or "configs/v2/32k.json" or "configs/v2/48k.json",
            "licenses" => true,
            _ => false
        };
    }

    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.EndsWith('/')) return false;
        var parts = path.Split('/');
        return !parts.Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
            p.Any(c => c < 32 || "<>\"|?*".Contains(c)) || IsDeviceName(p.Split('.')[0]));
    }

    private static bool IsDeviceName(string value) => new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static void ValidateFiles(UpdateFile[] files)
    {
        if (files is null || files.Length is 0 or > 20000 || !files.Any(f => f?.Path == ExeName)) throw new InvalidDataException("更新包缺少主程序。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            if (file is null || !IsManagedPath(file.Path) || file.Size < 0 || file.Size > 2L * 1024 * 1024 * 1024 || !HashValid(file.Sha256) || !names.Add(file.Path))
                throw new InvalidDataException($"更新路径或文件信息无效：{file?.Path}");
    }

    public static string SafePath(string root, string relative)
    {
        if (!IsManagedPath(relative) && relative != BaselineName) throw new InvalidDataException("不允许更新该路径。");
        var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新路径越界。");
        for (var current = full; current is not null; current = System.IO.Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新路径包含符号链接或目录联接。");
        return full;
    }

    public static UpdateBaseline? ReadBaseline(string root)
    {
        var path = SafePath(root, BaselineName);
        if (!File.Exists(path)) return null;
        var baseline = JsonSerializer.Deserialize<UpdateBaseline>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("安装基线为空。");
        ValidateFiles(baseline.Files);
        if (!Version.TryParse(baseline.Version, out _) || !HashValid(baseline.EnvironmentId)) throw new InvalidDataException("安装基线无效。");
        return baseline;
    }

    public static bool Compatible(UpdateManifest manifest, UpdateBaseline? baseline) => baseline is not null && manifest.EnvironmentId == baseline.EnvironmentId;
    public static void VerifyFile(string path, long size, string hash)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length != size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"文件校验失败：{System.IO.Path.GetFileName(path)}");
    }

    public static void Extract(string package, string destination, UpdateManifest manifest)
    {
        ValidateFiles(manifest.Files);
        VerifyFile(package, manifest.Package.Size, manifest.Package.Sha256);
        if (new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(destination))!).AvailableFreeSpace < manifest.Files.Sum(f => f.Size) + 32 * 1024 * 1024)
            throw new IOException("解压磁盘空间不足。");
        using var zip = ZipFile.OpenRead(package);
        var expected = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (zip.Entries.Count != expected.Count) throw new InvalidDataException("更新包文件数量不符。");
        foreach (var entry in zip.Entries)
        {
            if (!expected.TryGetValue(entry.FullName, out var file) || !seen.Add(entry.FullName) || entry.Length != file.Size || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException($"更新包包含意外文件：{entry.FullName}");
            var target = SafePath(destination, file.Path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, false);
            VerifyFile(target, file.Size, file.Sha256);
        }
    }

    public static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):F2} GiB" : $"{bytes / (1024d * 1024):F1} MiB";
}
