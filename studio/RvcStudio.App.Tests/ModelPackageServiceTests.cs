using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RvcStudio.App;
using Xunit;

namespace RvcStudio.App.Tests;

public sealed class ModelPackageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rvcstudio-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _legacyStore;
    private readonly ModelProfileService _profiles;
    private readonly ModelPackageService _packages;

    public ModelPackageServiceTests()
    {
        Directory.CreateDirectory(_root);
        _legacyStore = Path.Combine(_root, "model-profiles.json");
        _profiles = new ModelProfileService(_legacyStore);
        _packages = new ModelPackageService(_root, _profiles);
    }

    [Fact]
    public async Task PreviewImport_ReadsNestedFilesAndCompatibleJson()
    {
        var zip = CreateZip("nested/model/source.pth", "nested/index/source.index", "config.json", """
            {
              "recommended": {
                "f0method": "rmvpe",
                "pitch": 7,
                "volumeFactor": 0.75,
                "sampleLength": 0.2
              }
            }
            """);

        var preview = await _packages.PreviewImportAsync(zip);

        Assert.Equal("source", preview.SuggestedName);
        Assert.Equal("rmvpe", preview.Settings.PitchMethod);
        Assert.Equal(7, preview.Settings.Pitch);
        Assert.Equal(0.75, preview.Settings.RmsMixRate);
        Assert.Equal(0.2, preview.Settings.BlockTime);
        Assert.Equal(ModelTuningSettings.AppDefaults.ExtraTime, preview.Settings.ExtraTime);
    }

    [Fact]
    public async Task PreviewImport_InvalidJsonSilentlyUsesGlobalDefaults()
    {
        var zip = CreateZip("voice.pth", "voice.index", "voice.json", "{ damaged");

        var preview = await _packages.PreviewImportAsync(zip);

        Assert.True(preview.Settings.NearlyEquals(ModelTuningSettings.AppDefaults));
    }

    [Fact]
    public async Task PreviewImport_RejectsMissingOrDuplicateRequiredFiles()
    {
        var missing = CreateZip("voice.pth", null, null, null);
        await Assert.ThrowsAsync<InvalidDataException>(() => _packages.PreviewImportAsync(missing));

        var duplicate = Path.Combine(_root, "duplicate.zip");
        using (var archive = ZipFile.Open(duplicate, ZipArchiveMode.Create))
        {
            AddEntry(archive, "one.pth", "one");
            AddEntry(archive, "two.pth", "two");
            AddEntry(archive, "one.index", "index");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => _packages.PreviewImportAsync(duplicate));
    }

    [Fact]
    public async Task Import_RenamesPairAndOnlyWritesNonDefaultConfiguration()
    {
        var zip = CreateZip("source.pth", "other.index", null, null);
        var preview = await _packages.PreviewImportAsync(zip);
        var custom = ModelTuningSettings.AppDefaults with { Pitch = 5, IndexRate = 0.42 };

        var result = await _packages.ImportAsync(preview, "renamed", custom);

        Assert.Equal("model-bytes", await File.ReadAllTextAsync(result.ModelPath));
        Assert.Equal("index-bytes", await File.ReadAllTextAsync(result.IndexPath));
        var configPath = Path.Combine(_packages.WeightsDirectory, "renamed.rvcstudio.json");
        Assert.True(File.Exists(configPath));
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath)))
        {
            Assert.Equal(9, document.RootElement.GetProperty("recommended").EnumerateObject().Count());
        }
        Assert.True((await _profiles.ReadSettingsAsync(result.ModelPath)).NearlyEquals(custom));

        await _packages.ImportAsync(preview, "renamed", ModelTuningSettings.AppDefaults);
        Assert.False(File.Exists(Path.Combine(_packages.WeightsDirectory, "renamed.rvcstudio.json")));
    }

    [Fact]
    public async Task Import_RollsBackAllFilesWhenCommitFails()
    {
        var zip = CreateZip("source.pth", "source.index", null, null);
        var preview = await _packages.PreviewImportAsync(zip);
        Directory.CreateDirectory(_packages.WeightsDirectory);
        Directory.CreateDirectory(_packages.IndicesDirectory);
        var modelPath = Path.Combine(_packages.WeightsDirectory, "voice.pth");
        await File.WriteAllTextAsync(modelPath, "old-model");
        Directory.CreateDirectory(Path.Combine(_packages.IndicesDirectory, "voice.index"));

        await Assert.ThrowsAnyAsync<IOException>(() =>
            _packages.ImportAsync(preview, "voice", ModelTuningSettings.AppDefaults));

        Assert.Equal("old-model", await File.ReadAllTextAsync(modelPath));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_packages.WeightsDirectory),
            path => path.EndsWith(".backup", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".import", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SidecarSave_IsAtomicAndDeletesWhenRestoredToGlobalDefaults()
    {
        var modelPath = Path.Combine(_root, "external.pth");
        await File.WriteAllTextAsync(modelPath, "model");
        var custom = ModelTuningSettings.AppDefaults with { PitchMethod = "pm", ExtraTime = 3.2 };

        _profiles.UpdateCurrent(modelPath, custom);
        await _profiles.SaveAsync();
        Assert.True(File.Exists(ModelProfileService.GetSidecarPath(modelPath)));
        Assert.True((await _profiles.ReadSettingsAsync(modelPath)).NearlyEquals(custom));

        _profiles.UpdateCurrent(modelPath, ModelTuningSettings.AppDefaults);
        await _profiles.SaveAsync();
        Assert.False(File.Exists(ModelProfileService.GetSidecarPath(modelPath)));
    }

    [Fact]
    public async Task SidecarSave_DoesNotDropAValueThatIsOnlyCloseToGlobalDefault()
    {
        var modelPath = Path.Combine(_root, "precise.pth");
        await File.WriteAllTextAsync(modelPath, "model");
        var custom = ModelTuningSettings.AppDefaults with { BlockTime = 0.131 };

        await _profiles.WriteSettingsAsync(modelPath, custom);

        Assert.True(File.Exists(ModelProfileService.GetSidecarPath(modelPath)));
        Assert.Equal(0.131, (await _profiles.ReadSettingsAsync(modelPath)).BlockTime);
    }

    [Fact]
    public async Task Export_CreatesFlatNamedArchiveAndOmitsGlobalJson()
    {
        Directory.CreateDirectory(_packages.WeightsDirectory);
        Directory.CreateDirectory(_packages.IndicesDirectory);
        var modelPath = Path.Combine(_packages.WeightsDirectory, "voice.pth");
        var indexPath = Path.Combine(_packages.IndicesDirectory, "voice.index");
        await File.WriteAllTextAsync(modelPath, "model");
        await File.WriteAllTextAsync(indexPath, "index");
        var destination = Path.Combine(_root, "exports");

        var model = Assert.Single(_packages.ScanModels());
        await _packages.ExportAsync([model], destination, overwrite: false);
        using (var archive = ZipFile.OpenRead(Path.Combine(destination, "voice.zip")))
        {
            Assert.Equal(new[] { "voice.index", "voice.pth" },
                archive.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToArray());
        }

        await _profiles.WriteSettingsAsync(modelPath, ModelTuningSettings.AppDefaults with { Pitch = -4 });
        model = Assert.Single(_packages.ScanModels());
        await _packages.ExportAsync([model], destination, overwrite: true);
        using var configuredArchive = ZipFile.OpenRead(Path.Combine(destination, "voice.zip"));
        Assert.Contains(configuredArchive.Entries, entry => entry.FullName == "voice.rvcstudio.json");
    }

    [Fact]
    public void ScanModels_ShowsButDisablesModelWithoutIndex()
    {
        Directory.CreateDirectory(_packages.WeightsDirectory);
        File.WriteAllText(Path.Combine(_packages.WeightsDirectory, "no-index.pth"), "model");

        var model = Assert.Single(_packages.ScanModels());

        Assert.False(model.CanExport);
        Assert.Null(model.IndexPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("CON")]
    [InlineData("CON.backup")]
    [InlineData("bad/name")]
    [InlineData("trailing.")]
    public void ValidateModelName_RejectsUnsafeNames(string name)
    {
        Assert.Throws<InvalidDataException>(() => ModelPackageService.ValidateModelName(name));
    }

    [Fact]
    public async Task Initialize_MigratesLegacyProfilesAndDeletesCentralStore()
    {
        var modelPath = Path.Combine(_root, "legacy.pth");
        await File.WriteAllTextAsync(modelPath, "legacy-model");
        var current = ModelTuningSettings.AppDefaults with { Pitch = 3 };
        var legacy = new
        {
            schemaVersion = 1,
            profiles = new Dictionary<string, object>
            {
                ["HASH"] = new { lastKnownPath = modelPath, current },
            },
        };
        await File.WriteAllTextAsync(_legacyStore, JsonSerializer.Serialize(legacy));

        await _profiles.InitializeAsync();

        Assert.False(File.Exists(_legacyStore));
        Assert.True((await _profiles.ReadSettingsAsync(modelPath)).NearlyEquals(current));
    }

    private string CreateZip(
        string modelEntry,
        string? indexEntry,
        string? jsonEntry,
        string? json)
    {
        var zip = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        AddEntry(archive, modelEntry, "model-bytes");
        if (indexEntry is not null) AddEntry(archive, indexEntry, "index-bytes");
        if (jsonEntry is not null) AddEntry(archive, jsonEntry, json ?? string.Empty);
        return zip;
    }

    private static void AddEntry(ZipArchive archive, string name, string contents)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(contents);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A failed cleanup must not hide the assertion result.
        }
    }
}
