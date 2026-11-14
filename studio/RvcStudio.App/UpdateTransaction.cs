using System.Diagnostics;
using System.Text.Json;

namespace RvcStudio.App;

public sealed record UpdateBackup(string Path, bool Existed, long Size, string Sha256);
public sealed record UpdateJournal(string State, UpdateBackup[] Backups);

// All mutation is confined to managed paths. The journal is durable before the first replacement.
public static class UpdateTransaction
{
    public const string DirectoryName = ".rvc-update";
    private static string Work(string root) => Path.Combine(root, DirectoryName);
    public static bool NeedsRecovery(string root) => File.Exists(Path.Combine(Work(root), "journal.json"));

    public static void Apply(string root, string prepared, UpdateManifest manifest, Action<string>? healthCheck = null, Action<int>? afterWrite = null)
    {
        var baseline = UpdatePackage.ReadBaseline(root) ?? throw new InvalidDataException("缺少安装基线，需要完整安装。");
        if (!UpdatePackage.Compatible(manifest, baseline) || AppUpdateService.Normalize(Version.Parse(manifest.Version)) <= AppUpdateService.Normalize(Version.Parse(baseline.Version)))
            throw new InvalidDataException("更新版本或运行环境不兼容。");
        UpdatePackage.ValidateFiles(manifest.Files);
        foreach (var file in manifest.Files) UpdatePackage.VerifyFile(UpdatePackage.SafePath(prepared, file.Path), file.Size, file.Sha256);
        var work = Work(root);
        if (NeedsRecovery(root)) throw new IOException("存在尚未恢复的更新事务。");
        AssertWorkPath(root);
        Directory.CreateDirectory(work);
        var backupRoot = Path.Combine(work, "backup");
        // Never reuse remnants from an incomplete preparation.
        if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true);
        Directory.CreateDirectory(backupRoot);
        var paths = baseline.Files.Select(f => f.Path).Union(manifest.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase)
            .Append(UpdatePackage.BaselineName).ToArray();
        long required = manifest.Files.Sum(f => f.Size) + paths.Sum(p => File.Exists(UpdatePackage.SafePath(root, p)) ? new FileInfo(UpdatePackage.SafePath(root, p)).Length : 0) + 32 * 1024 * 1024;
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace < required) throw new IOException("安装磁盘空间不足。");
        var backups = new List<UpdateBackup>();
        foreach (var path in paths)
        {
            var target = UpdatePackage.SafePath(root, path);
            if (File.Exists(target))
            {
                // A live second instance must block the entire transaction before mutation.
                using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                var backup = UpdatePackage.SafePath(backupRoot, path);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, true);
                using (var durable = new FileStream(backup, FileMode.Open, FileAccess.Write)) durable.Flush(true);
                using var stream = File.OpenRead(backup);
                backups.Add(new(path, true, stream.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))));
            }
            else backups.Add(new(path, false, 0, ""));
        }
        var journal = new UpdateJournal("applying", backups.ToArray());
        WriteDurable(Path.Combine(work, "journal.json"), journal);
        try
        {
            var index = 0;
            foreach (var file in manifest.Files)
            {
                var target = UpdatePackage.SafePath(root, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                ReplaceFile(UpdatePackage.SafePath(prepared, file.Path), target);
                afterWrite?.Invoke(++index);
            }
            var newPaths = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var old in baseline.Files.Where(f => !newPaths.Contains(f.Path))) File.Delete(UpdatePackage.SafePath(root, old.Path));
            foreach (var file in manifest.Files) UpdatePackage.VerifyFile(UpdatePackage.SafePath(root, file.Path), file.Size, file.Sha256);
            healthCheck?.Invoke(root);
            WriteDurable(UpdatePackage.SafePath(root, UpdatePackage.BaselineName), new UpdateBaseline(manifest.Version, manifest.EnvironmentId, manifest.Files));
            WriteDurable(Path.Combine(work, "journal.json"), journal with { State = "committed" });
        }
        catch
        {
            Recover(root);
            throw;
        }
        // A cleanup error must never roll back a committed version or report it as failed.
        try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static void Recover(string root)
    {
        AssertWorkPath(root);
        var work = Work(root);
        var path = Path.Combine(work, "journal.json");
        if (!File.Exists(path)) return;
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(path), UpdatePackage.Json) ?? throw new InvalidDataException("更新恢复日志损坏。");
        if (journal.State is not ("applying" or "committed")) throw new InvalidDataException("更新恢复状态无效。");
        if (journal.State == "applying")
        {
            // Validate every backup before restoring anything. Recovery is idempotent.
            foreach (var entry in journal.Backups)
            {
                _ = UpdatePackage.SafePath(root, entry.Path);
                if (entry.Existed) UpdatePackage.VerifyFile(UpdatePackage.SafePath(Path.Combine(work, "backup"), entry.Path), entry.Size, entry.Sha256);
            }
            foreach (var entry in journal.Backups)
            {
                var target = UpdatePackage.SafePath(root, entry.Path);
                if (entry.Existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    ReplaceFile(UpdatePackage.SafePath(Path.Combine(work, "backup"), entry.Path), target);
                }
                else File.Delete(target);
            }
            // Mark recovery complete before removing backup files, so interrupted cleanup is safe.
            WriteDurable(path, journal with { State = "committed" });
        }
        Directory.Delete(work, true);
    }

    private static void AssertWorkPath(string root)
    {
        // SafePath also examines ancestors; use a managed dummy beneath the work directory.
        _ = UpdatePackage.SafePath(Work(root), "LICENSE");
        if (!Directory.Exists(Work(root))) return;
        var pending = new Stack<string>();
        pending.Push(Work(root));
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新事务目录包含链接。");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
    }

    private static void ReplaceFile(string source, string target)
    {
        var pending = target + ".rvc-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, pending);
            using (var stream = new FileStream(pending, FileMode.Open, FileAccess.Write)) stream.Flush(true);
            File.Move(pending, target, true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    public static void WriteDurable<T>(string path, T value)
    {
        var temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value, UpdatePackage.Json);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    public static void HealthCheck(string root)
    {
        var start = new ProcessStartInfo(Path.Combine(root, "runtime", "python.exe")) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(Path.Combine(root, "tools", "package_healthcheck.py"));
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add("--update-only");
        using var process = Process.Start(start) ?? throw new IOException("无法启动引擎自检。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000)) { process.Kill(true); throw new IOException("引擎自检超时。"); }
        UpdateRunner.Log("引擎自检：" + output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
        if (process.ExitCode != 0) throw new IOException("引擎自检失败，已恢复旧版本。");
    }
}
