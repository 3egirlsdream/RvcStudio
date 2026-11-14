using System.Security.Cryptography;
using System.Text.Json;

namespace RvcStudio.App;

public sealed record FullCleanupPlan(string Root, string NewInventory, UpdateFile[] Retired);
public sealed record FullCleanupResult(int Removed, int PreservedModified);

/// <summary>Full-installer inventory reconciliation. Never sweeps a directory or touches user model stores.</summary>
public static class FullInstallCleanup
{
    public const string InventoryName = "release-manifest.json";
    public const string LogName = "installer-cleanup.log";
    private const int MaxInventoryBytes = 64 * 1024 * 1024;

    public static bool Owns(string path) => UpdatePackage.IsSafeRelativePath(path) &&
        (UpdatePackage.IsManagedPath(path) || path.StartsWith("runtime/", StringComparison.Ordinal) ||
         path.StartsWith("engine/", StringComparison.Ordinal) || path.StartsWith("infer/", StringComparison.Ordinal) ||
         path.StartsWith("i18n/", StringComparison.Ordinal) || path.StartsWith("tools/", StringComparison.Ordinal) ||
         path.StartsWith("assets/hubert_base/", StringComparison.Ordinal) || path.StartsWith("assets/rmvpe/", StringComparison.Ordinal));

    private static string WithinRoot(string root, string relative)
    {
        if (!UpdatePackage.IsSafeRelativePath(relative)) throw new InvalidDataException($"安装清单路径无效：{relative}");
        var directory = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (directory.Length == 0 || directory.Equals(Path.GetPathRoot(directory)?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装目录不能是磁盘根目录。");
        var path = Path.GetFullPath(Path.Combine(directory, relative));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装清单路径越界。");
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"安装路径包含链接，已停止清理：{relative}");
        return path;
    }

    private static string ReadInventory(string path)
    {
        if (new FileInfo(path).Length > MaxInventoryBytes) throw new InvalidDataException("安装清单过大。");
        return File.ReadAllText(path);
    }

    public static UpdateFile[] ParseInventory(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var files = root.GetProperty("files").Deserialize<UpdateFile[]>(UpdatePackage.Json);
        ValidateRecords(files);
        if (root.TryGetProperty("file_count", out var count) && count.GetInt32() != files!.Length)
            throw new InvalidDataException("安装清单文件数量不符。");
        return files!;
    }

    private static void ValidateRecords(UpdateFile[]? files)
    {
        if (files is null || files.Length is 0 or > 250000 || !files.Any(f => f?.Path == UpdatePackage.ExeName))
            throw new InvalidDataException("安装清单缺少主程序。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            if (file is null || !UpdatePackage.IsSafeRelativePath(file.Path) || !paths.Add(file.Path) || file.Size < 0 ||
                file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("安装清单包含无效或重复文件。");
    }

    // Run before Inno replaces the old inventories. Preparing does not remove or modify installed files.
    public static FullCleanupPlan Prepare(string root, string newInventoryPath, string planPath)
    {
        var inventoryPath = WithinRoot(root, InventoryName);
        var newJson = ReadInventory(newInventoryPath);
        var next = ParseInventory(newJson).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<string, UpdateFile>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(inventoryPath))
            foreach (var file in ParseInventory(ReadInventory(inventoryPath))) previous.Add(file.Path, file);
        // Small updates can introduce code absent from the last full-install inventory.
        var baseline = UpdatePackage.ReadBaseline(root);
        if (baseline is not null)
            foreach (var file in baseline.Files) previous[file.Path] = file;
        var retired = previous.Values.Where(f => Owns(f.Path) && !next.Contains(f.Path)).ToArray();
        foreach (var file in retired) _ = WithinRoot(root, file.Path);
        var plan = new FullCleanupPlan(Path.GetFullPath(root), newJson, retired);
        UpdateTransaction.WriteDurable(planPath, plan);
        return plan;
    }

    // Run as the first [Run] action, after files have copied and before Python self-test/launch.
    // Keep the old inventory until cleanup succeeds, making a failed/interrupted cleanup retryable.
    public static FullCleanupResult Apply(string planPath, Action<int>? afterDelete = null)
    {
        var plan = JsonSerializer.Deserialize<FullCleanupPlan>(ReadInventory(planPath), UpdatePackage.Json)
            ?? throw new InvalidDataException("清理计划为空。");
        var next = ParseInventory(plan.NewInventory);
        var newNames = next.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inventoryPath = WithinRoot(plan.Root, InventoryName);
        if (plan.Retired is null || plan.Retired.Length > 250000) throw new InvalidDataException("清理计划无效。");
        var retiredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in plan.Retired)
            if (file is null || !Owns(file.Path) || newNames.Contains(file.Path) || !retiredNames.Add(file.Path) || file.Size < 0 ||
                file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("清理计划试图删除保留文件或用户数据。");
        // Verify the new installation before deleting anything. User models/settings may deliberately differ.
        foreach (var file in next.Where(f => Owns(f.Path)))
            UpdatePackage.VerifyFile(WithinRoot(plan.Root, file.Path), file.Size, file.Sha256);
        foreach (var file in plan.Retired) _ = WithinRoot(plan.Root, file.Path);
        var logPath = WithinRoot(plan.Root, LogName);
        using var log = new StreamWriter(logPath, true) { AutoFlush = true };
        log.WriteLine($"{DateTimeOffset.Now:O} Full installer cleanup: {plan.Retired.Length} candidates");
        var removed = 0;
        var modified = 0;
        foreach (var file in plan.Retired)
        {
            var path = WithinRoot(plan.Root, file.Path);
            if (!File.Exists(path)) continue;
            // Do not remove user edits even if an old installer originally owned the filename.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (stream.Length != file.Size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    log.WriteLine($"PRESERVED MODIFIED {file.Path}");
                    modified++;
                    continue;
                }
            }
            var attributes = File.GetAttributes(path);
            try
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                File.Delete(path);
            }
            finally { if (File.Exists(path)) File.SetAttributes(path, attributes); }
            log.WriteLine($"REMOVED {file.Path}");
            removed++;
            afterDelete?.Invoke(removed);
            PruneEmptyParents(plan.Root, Path.GetDirectoryName(path)!);
        }
        WriteInventory(inventoryPath, plan.NewInventory);
        log.WriteLine($"Completed: removed={removed}, preservedModified={modified}");
        UpdateTransaction.WriteDurable(planPath + ".complete", new FullCleanupResult(removed, modified));
        return new(removed, modified);
    }

    private static void WriteInventory(string path, string json)
    {
        var temporary = path + ".new";
        // Use a checked pathname even for our temporary metadata.
        _ = WithinRoot(Path.GetDirectoryName(path)!, Path.GetFileName(temporary));
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            stream.Write(bytes); stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    private static void PruneEmptyParents(string root, string directory)
    {
        var stop = Path.GetFullPath(root).TrimEnd('\\', '/');
        while (!directory.Equals(stop, StringComparison.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(stop, directory).Replace('\\', '/');
            _ = WithinRoot(stop, relative);
            if (Directory.EnumerateFileSystemEntries(directory).Any()) return;
            Directory.Delete(directory, false);
            directory = Path.GetDirectoryName(directory)!;
        }
    }

    public static bool Handle(string[] args)
    {
        if (args.Length == 0 || args[0] != "--installer-cleanup") return false;
        try
        {
            if (args.Length == 5 && args[1] == "prepare") Prepare(args[2], args[3], args[4]);
            else if (args.Length == 3 && args[1] == "apply") Apply(args[2]);
            else throw new ArgumentException("安装清理参数无效。");
        }
        catch (Exception ex)
        {
            UpdateRunner.Log("完整安装包清理失败：" + ex);
            Environment.ExitCode = 1;
        }
        return true;
    }
}
