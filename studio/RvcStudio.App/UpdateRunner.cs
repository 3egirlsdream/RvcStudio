using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace RvcStudio.App;

public static class UpdateRunner
{
    public static string MutexName(string root) => "Local\\RvcStudio-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\', '/').ToUpperInvariant())));
    private static string ResultPath(string root) => Path.Combine(UpdatePackage.CacheRoot, MutexName(root).Split('-')[1] + ".result.json");
    public static string? ConsumeResult(string root)
    {
        var path = ResultPath(root);
        try
        {
            if (!File.Exists(path)) return null;
            var message = JsonSerializer.Deserialize<string>(File.ReadAllText(path));
            File.Delete(path);
            return message;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }
    public static void CleanupRunners()
    {
        try
        {
            if (!Directory.Exists(UpdatePackage.CacheRoot)) return;
            foreach (var directory in Directory.EnumerateDirectories(UpdatePackage.CacheRoot, "runner-*"))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                // Only remove our flat runner directories; a running runner will remain locked.
                if (Directory.EnumerateDirectories(directory).Any()) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(directory))
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.Delete(file);
                    Directory.Delete(directory);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // The UI waits for the elevated worker to be ready before stopping the engine or closing.
    public static async Task BeginAsync(string cache, bool recovery = false)
    {
        var directory = Path.Combine(UpdatePackage.CacheRoot, "runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, UpdatePackage.ExeName);
        File.Copy(Environment.ProcessPath!, executable);
        var start = Start(executable, "--update-coordinate", AppContext.BaseDirectory, cache, Environment.ProcessId.ToString(), directory, recovery ? "recover" : "install");
        using var process = Process.Start(start) ?? throw new IOException("无法启动更新协调进程。");
        var status = Path.Combine(directory, "ready.json");
        while (!File.Exists(status))
        {
            if (process.HasExited) throw new IOException("更新未启动。请重试并允许管理员权限。");
            await Task.Delay(200);
        }
        var message = JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(status));
        if (message != "ready") throw new IOException(message ?? "更新未启动。");
    }

    public static bool Handle(string[] args)
    {
        if (args.Length == 0 || !args[0].StartsWith("--update-", StringComparison.Ordinal)) return false;
        try
        {
            if (args.Length != 6) throw new ArgumentException("更新参数无效。");
            if (args[0] == "--update-coordinate") Coordinate(args);
            else if (args[0] == "--update-apply") Apply(args);
            else throw new ArgumentException("未知更新命令。");
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            Environment.ExitCode = 1;
        }
        return true;
    }

    private static void Coordinate(string[] args)
    {
        var root = Path.GetFullPath(args[1]);
        var status = Path.Combine(args[4], "ready.json");
        var start = Start(Environment.ProcessPath!, "--update-apply", root, args[2], args[3], args[4], args[5]);
        start.UseShellExecute = true;
        start.Verb = "runas";
        try
        {
            using var worker = Process.Start(start) ?? throw new IOException("无法请求管理员权限。");
            worker.WaitForExit();
            if (!File.Exists(status)) WriteStatus(status, "更新准备失败，原程序保持运行。详情见更新日志。");
            var parentExited = false;
            try { using var parent = Process.GetProcessById(int.Parse(args[3])); parentExited = parent.HasExited; } catch (ArgumentException) { parentExited = true; }
            if (parentExited)
            {
                WriteStatus(ResultPath(root), worker.ExitCode == 0 ? (args[5] == "recover" ? "恢复完成。" : "更新完成。") : "更新未成功，已尝试恢复旧版本。详情请查看更新日志。");
                Process.Start(new ProcessStartInfo(Path.Combine(root, UpdatePackage.ExeName)) { UseShellExecute = true, WorkingDirectory = root });
            }
        }
        catch (Exception ex)
        {
            WriteStatus(status, ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } ? "已取消管理员权限，程序保持运行。" : "更新启动失败：" + ex.Message);
            Log(ex.ToString());
        }
    }

    private static void Apply(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("更新需要管理员权限。");
        var root = Path.GetFullPath(args[1]);
        // Only update the installation from which this exact runner was copied.
        using (var source = File.OpenRead(Path.Combine(root, UpdatePackage.ExeName)))
        using (var runner = File.OpenRead(Environment.ProcessPath!))
            if (!System.Security.Cryptography.SHA256.HashData(source).SequenceEqual(System.Security.Cryptography.SHA256.HashData(runner)))
                throw new InvalidDataException("更新器与安装目录不匹配。");
        var status = Path.Combine(args[4], "ready.json");
        var secureRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RvcStudio-Update-" + Guid.NewGuid().ToString("N"));
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(secureRoot).Create(security);
        try
        {
            UpdateManifest? manifest = null;
            var prepared = Path.Combine(secureRoot, "prepared");
            if (args[5] != "recover")
            {
                // Copy then verify inside the admin-only directory, closing the download-cache race.
                File.Copy(Path.Combine(args[2], "manifest.json"), Path.Combine(secureRoot, "manifest.json"));
                manifest = UpdatePackage.Verify(File.ReadAllText(Path.Combine(secureRoot, "manifest.json")), UpdateTrust.Load().PublicKeyPem);
                if (!UpdatePackage.Compatible(manifest, UpdatePackage.ReadBaseline(root))) throw new InvalidDataException("运行环境已改变，请下载完整安装包。");
                if (AppUpdateService.Normalize(Version.Parse(manifest.Version)) <= AppUpdateService.Normalize(AppUpdateService.CurrentVersion)) throw new InvalidDataException("拒绝安装旧版本。");
                File.Copy(Path.Combine(args[2], "package.zip"), Path.Combine(secureRoot, "package.zip"));
                UpdatePackage.Extract(Path.Combine(secureRoot, "package.zip"), prepared, manifest);
                var fileVersion = FileVersionInfo.GetVersionInfo(Path.Combine(prepared, UpdatePackage.ExeName)).FileVersion;
                if (!Version.TryParse(fileVersion, out var actual) || AppUpdateService.Normalize(actual) != AppUpdateService.Normalize(Version.Parse(manifest.Version))) throw new InvalidDataException("程序版本与清单不一致。");
            }
            WriteStatus(status, "ready");
            try
            {
                using var parent = Process.GetProcessById(int.Parse(args[3]));
                if (!parent.WaitForExit(90000)) throw new IOException("等待程序退出超时，未安装更新。");
            }
            catch (ArgumentException) { }
            using var gate = new Mutex(false, MutexName(root));
            bool locked;
            try { locked = gate.WaitOne(TimeSpan.FromSeconds(15)); } catch (AbandonedMutexException) { locked = true; }
            if (!locked) throw new IOException("另一个程序实例正在运行，未安装更新。");
            try
            {
                UpdateTransaction.Recover(root);
                if (manifest is not null) UpdateTransaction.Apply(root, prepared, manifest, UpdateTransaction.HealthCheck);
            }
            finally { gate.ReleaseMutex(); }
        }
        finally { Directory.Delete(secureRoot, true); }
    }

    private static ProcessStartInfo Start(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }
    private static void WriteStatus(string path, string message) => UpdateTransaction.WriteDurable(path, message);
    public static void Log(string message)
    {
        try { Directory.CreateDirectory(UpdatePackage.CacheRoot); File.AppendAllText(Path.Combine(UpdatePackage.CacheRoot, "update.log"), $"{DateTimeOffset.Now:O} {message}\n"); } catch { }
    }
}
