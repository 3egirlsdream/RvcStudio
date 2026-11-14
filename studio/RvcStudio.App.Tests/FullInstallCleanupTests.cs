using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RvcStudio.App;
using Xunit;

namespace RvcStudio.App.Tests;

public sealed class FullInstallCleanupTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "rvc-full-cleanup-test-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_temporary, "installation");
    private string PlanPath => Path.Combine(_temporary, "cleanup-plan.json");
    private string Incoming => Path.Combine(_temporary, "new-release.json");
    private string At(string path) => Path.Combine(Root, path);
    public FullInstallCleanupTests() => Directory.CreateDirectory(Root);
    private UpdateFile Put(string path, string value)
    {
        var data = Encoding.UTF8.GetBytes(value);
        Directory.CreateDirectory(Path.GetDirectoryName(At(path))!);
        File.WriteAllBytes(At(path), data);
        return new(path, data.Length, Convert.ToHexString(SHA256.HashData(data)));
    }
    private static string Inventory(params UpdateFile[] files) => JsonSerializer.Serialize(new { file_count = files.Length, files }, UpdatePackage.Json);
    private void Old(params UpdateFile[] files) => File.WriteAllText(At(FullInstallCleanup.InventoryName), Inventory(files));
    private FullCleanupPlan Prepare(params UpdateFile[] files)
    {
        File.WriteAllText(Incoming, Inventory(files));
        return FullInstallCleanup.Prepare(Root, Incoming, PlanPath);
    }

    [Fact]
    public void Cleanup_RemovesRetiredRuntimeAndResourcesButPreservesUnknownAndProtectedFiles()
    {
        var exe = Put(UpdatePackage.ExeName, "new-app");
        var runtime = Put("runtime/Lib/site-packages/removed/dependency.dll", "old-library");
        var code = Put("engine/removed.py", "old-code");
        var resource = Put("i18n/locale/removed.json", "old-resource");
        var baseModel = Put("assets/hubert_base/retired.bin", "retired-system-model");
        var voice = Put("assets/weights/custom.pth", "user-voice");
        var index = Put("assets/indices/custom.index", "user-index");
        var parameters = Put("assets/weights/custom.rvcstudio.json", "user-tuning");
        var config = Put("configs/config.json", "user-settings");
        Put("runtime/Lib/site-packages/untracked.py", "user-added");
        Old(exe, runtime, code, resource, baseModel, voice, index, parameters, config);
        var oldInventory = File.ReadAllText(At(FullInstallCleanup.InventoryName));
        var plan = Prepare(exe);
        Assert.Equal(4, plan.Retired.Length);
        Assert.True(File.Exists(At(runtime.Path))); // preparation is read-only for installed files
        Assert.Equal(oldInventory, File.ReadAllText(At(FullInstallCleanup.InventoryName)));
        var result = FullInstallCleanup.Apply(PlanPath);
        Assert.Equal(4, result.Removed);
        Assert.False(Directory.Exists(At("runtime/Lib/site-packages/removed")));
        Assert.False(File.Exists(At(resource.Path)));
        foreach (var file in new[] { voice, index, parameters, config }) Assert.True(File.Exists(At(file.Path)));
        Assert.Equal("user-added", File.ReadAllText(At("runtime/Lib/site-packages/untracked.py")));
        Assert.Equal(Inventory(exe), File.ReadAllText(At(FullInstallCleanup.InventoryName)));
        Assert.Contains("REMOVED", File.ReadAllText(At(FullInstallCleanup.LogName)));
    }

    [Fact]
    public void Cleanup_PreservesModifiedRetiredFilesAndLogsTheirNames()
    {
        var exe = Put(UpdatePackage.ExeName, "app");
        var old = Put("engine/edited.py", "original");
        Old(exe, old);
        Put(old.Path, "user-edited");
        Prepare(exe);
        var result = FullInstallCleanup.Apply(PlanPath);
        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.PreservedModified);
        Assert.Equal("user-edited", File.ReadAllText(At(old.Path)));
        Assert.Contains("PRESERVED MODIFIED engine/edited.py", File.ReadAllText(At(FullInstallCleanup.LogName)));
    }

    [Fact]
    public void Cleanup_UsesLatestSmallUpdateBaselineForIntroducedAndModifiedCode()
    {
        var exe = Put(UpdatePackage.ExeName, "app");
        var previous = Put("engine/changed.py", "full-install-code");
        Old(exe, previous);
        var latest = Put(previous.Path, "small-update-code");
        var added = Put("engine/added-by-small-update.py", "new-code");
        File.WriteAllText(At(UpdatePackage.BaselineName), JsonSerializer.Serialize(new UpdateBaseline("1.2.0", new string('A', 64), [exe, latest, added]), UpdatePackage.Json));
        Prepare(exe);
        Assert.Equal(2, FullInstallCleanup.Apply(PlanPath).Removed);
        Assert.False(File.Exists(At(latest.Path)));
        Assert.False(File.Exists(At(added.Path)));
    }

    [Fact]
    public void Cleanup_KeepsCaseRenamedFilesInTheNewInventory()
    {
        var exe = Put(UpdatePackage.ExeName, "app");
        var old = Put("engine/Keep.py", "code");
        Old(exe, old);
        var plan = Prepare(exe, old with { Path = "engine/keep.py" });
        Assert.Empty(plan.Retired);
        FullInstallCleanup.Apply(PlanPath);
        Assert.True(File.Exists(At(old.Path)));
    }

    [Fact]
    public void Cleanup_VerifiesNewFilesBeforeDeletingOldFilesOrCommittingInventory()
    {
        var exe = Put(UpdatePackage.ExeName, "app");
        var retired = Put("runtime/removed.dll", "old-runtime");
        Old(exe, retired);
        var oldJson = File.ReadAllText(At(FullInstallCleanup.InventoryName));
        Prepare(exe);
        Put(UpdatePackage.ExeName, "corrupted-new-app");
        Assert.Throws<InvalidDataException>(() => FullInstallCleanup.Apply(PlanPath));
        Assert.True(File.Exists(At(retired.Path)));
        Assert.Equal(oldJson, File.ReadAllText(At(FullInstallCleanup.InventoryName)));
    }

    [Fact]
    public void Cleanup_InterruptedDeletionCanRetryWithOriginalInventory()
    {
        var exe = Put(UpdatePackage.ExeName, "app");
        var first = Put("runtime/first.dll", "old1");
        var second = Put("runtime/second.dll", "old2");
        Old(exe, first, second);
        var oldJson = File.ReadAllText(At(FullInstallCleanup.InventoryName));
        Prepare(exe);
        Assert.Throws<IOException>(() => FullInstallCleanup.Apply(PlanPath, _ => throw new IOException("interruption")));
        Assert.Equal(oldJson, File.ReadAllText(At(FullInstallCleanup.InventoryName)));
        Assert.False(File.Exists(PlanPath + ".complete"));
        Assert.Equal(1, FullInstallCleanup.Apply(PlanPath).Removed);
        Assert.False(File.Exists(At(second.Path)));
        Assert.Equal(0, FullInstallCleanup.Apply(PlanPath).Removed);
    }

    [Fact]
    public void Cleanup_LockedFilesFailWithoutAdvancingInventoryAndCanRetry()
    {
        var exe = Put(UpdatePackage.ExeName, "app");
        var retired = Put("runtime/locked.dll", "old-runtime");
        Old(exe, retired); Prepare(exe);
        var oldJson = File.ReadAllText(At(FullInstallCleanup.InventoryName));
        using (File.Open(At(retired.Path), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => FullInstallCleanup.Apply(PlanPath));
        Assert.True(File.Exists(At(retired.Path)));
        Assert.Equal(oldJson, File.ReadAllText(At(FullInstallCleanup.InventoryName)));
        Assert.Equal(1, FullInstallCleanup.Apply(PlanPath).Removed);
    }

    [Fact]
    public void Cleanup_ReadOnlyOwnedFilesCanBeRemoved()
    {
        var exe = Put(UpdatePackage.ExeName, "app");
        var retired = Put("licenses/old.txt", "notice");
        Old(exe, retired); Prepare(exe);
        File.SetAttributes(At(retired.Path), FileAttributes.ReadOnly);
        Assert.Equal(1, FullInstallCleanup.Apply(PlanPath).Removed);
    }

    [Fact]
    public void Cleanup_NoOldInventoryDoesNotGuessOwnership()
    {
        var exe = Put(UpdatePackage.ExeName, "app");
        Put("runtime/unknown.dll", "unknown-owner");
        Assert.Empty(Prepare(exe).Retired);
        FullInstallCleanup.Apply(PlanPath);
        Assert.True(File.Exists(At("runtime/unknown.dll")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("runtime/../outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("runtime/CON.dll")]
    [InlineData("runtime/name.dll:stream")]
    public void Cleanup_RejectsUnsafeManifestPathsBeforeAnyMutation(string path)
    {
        var exe = Put(UpdatePackage.ExeName, "app");
        var malicious = exe with { Path = path };
        Old(exe, malicious);
        Assert.Throws<InvalidDataException>(() => Prepare(exe));
        Assert.False(File.Exists(PlanPath));
    }

    [Fact]
    public void Cleanup_RejectsCorruptOldInventoryAndDuplicateCaseAliases()
    {
        var exe = Put(UpdatePackage.ExeName, "app");
        File.WriteAllText(At(FullInstallCleanup.InventoryName), "{broken");
        Assert.ThrowsAny<JsonException>(() => Prepare(exe));
        Old(exe, exe with { Path = "rvc studio.exe" });
        Assert.Throws<InvalidDataException>(() => Prepare(exe));
    }

    [Theory]
    [InlineData("assets/weights/custom.pth")]
    [InlineData("configs/config.json")]
    [InlineData("runtime/keep.dll")]
    public void Cleanup_RejectsTamperedPlanTargetingUserDataOrNewFiles(string path)
    {
        var exe = Put(UpdatePackage.ExeName, "app");
        var keep = Put(path, "keep");
        Old(exe, keep);
        var plan = Prepare(exe, keep);
        File.WriteAllText(PlanPath, JsonSerializer.Serialize(plan with { Retired = [keep] }, UpdatePackage.Json));
        Assert.Throws<InvalidDataException>(() => FullInstallCleanup.Apply(PlanPath));
        Assert.Equal("keep", File.ReadAllText(At(keep.Path)));
    }

    [Fact]
    public void Cleanup_RejectsDirectoryJunctionsWithoutTouchingOutsideFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var exe = Put(UpdatePackage.ExeName, "app");
        var outside = Path.Combine(_temporary, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "old.dll"), "outside");
        var junction = At("runtime");
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{junction}\" \"{outside}\"", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
        try
        {
            Old(exe, new("runtime/old.dll", 7, Convert.ToHexString(SHA256.HashData("outside"u8.ToArray()))));
            Assert.Throws<InvalidDataException>(() => Prepare(exe));
            Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "old.dll")));
        }
        finally { Directory.Delete(junction); }
    }

    public void Dispose() => Directory.Delete(_temporary, true);
}
