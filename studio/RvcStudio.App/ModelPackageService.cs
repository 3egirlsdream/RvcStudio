using System.IO.Compression;

namespace RvcStudio.App;

internal sealed record ManagedModel(
    string Name,
    string ModelPath,
    string? IndexPath,
    string ConfigPath,
    bool HasCustomSettings)
{
    public bool CanExport => IndexPath is not null;
}

internal sealed record ModelImportPreview(
    string ZipPath,
    string ModelEntryName,
    string IndexEntryName,
    string? JsonEntryName,
    string SuggestedName,
    ModelTuningSettings Settings);

internal sealed record ModelImportResult(
    string Name,
    string ModelPath,
    string IndexPath,
    bool ReplacedExisting,
    bool HasCustomSettings);

internal sealed record ModelExportResult(string Name, string ZipPath);

internal sealed record ModelTransferProgress(int Completed, int Total, string Message);

internal sealed class ModelPackageService
{
    private static readonly HashSet<string> ReservedWindowsNames = new(
        new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        },
        StringComparer.OrdinalIgnoreCase);

    private readonly ModelProfileService _profiles;

    public ModelPackageService(string rvcRoot, ModelProfileService profiles)
    {
        if (string.IsNullOrWhiteSpace(rvcRoot)) throw new ArgumentException("RVC 根目录不能为空。", nameof(rvcRoot));
        RvcRoot = Path.GetFullPath(rvcRoot);
        WeightsDirectory = Path.Combine(RvcRoot, "assets", "weights");
        IndicesDirectory = Path.Combine(RvcRoot, "assets", "indices");
        _profiles = profiles;
    }

    public string RvcRoot { get; }
    public string WeightsDirectory { get; }
    public string IndicesDirectory { get; }

    public IReadOnlyList<ManagedModel> ScanModels()
    {
        if (!Directory.Exists(WeightsDirectory)) return [];
        try
        {
            return Directory.EnumerateFiles(WeightsDirectory, "*.pth", SearchOption.TopDirectoryOnly)
                .Select(modelPath =>
                {
                    var name = Path.GetFileNameWithoutExtension(modelPath);
                    var indexPath = Path.Combine(IndicesDirectory, name + ".index");
                    var configPath = ModelProfileService.GetSidecarPath(modelPath);
                    return new ManagedModel(
                        name,
                        Path.GetFullPath(modelPath),
                        File.Exists(indexPath) ? Path.GetFullPath(indexPath) : null,
                        configPath,
                        File.Exists(configPath) || File.Exists(modelPath + ".rvcstudio.json"));
                })
                .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"无法读取模型目录：{WeightsDirectory}", exception);
        }
    }

    public async Task<ModelImportPreview> PreviewImportAsync(
        string zipPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("找不到要导入的 ZIP 压缩包。", zipPath);
        if (!string.Equals(Path.GetExtension(zipPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("请选择 .zip 压缩包。");
        }

        await using var file = new FileStream(
            zipPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        var models = files.Where(entry => HasExtension(entry.Name, ".pth")).ToList();
        var indices = files.Where(entry => HasExtension(entry.Name, ".index")).ToList();
        var jsonFiles = files.Where(entry => HasExtension(entry.Name, ".json")).ToList();

        if (models.Count != 1 || indices.Count != 1)
        {
            throw new InvalidDataException("ZIP 中必须恰好包含一个 .pth 和一个 .index 文件。");
        }
        if (jsonFiles.Count > 1)
        {
            throw new InvalidDataException("ZIP 中最多只能包含一个配置 JSON。");
        }
        if (models[0].Length <= 0 || indices[0].Length <= 0)
        {
            throw new InvalidDataException("ZIP 中的模型或索引文件为空。");
        }

        var settings = ModelTuningSettings.AppDefaults;
        if (jsonFiles.Count == 1 && jsonFiles[0].Length > 0)
        {
            await using var jsonStream = jsonFiles[0].Open();
            settings = await ModelProfileService.TryReadSettingsFromJsonAsync(jsonStream, cancellationToken)
                       ?? ModelTuningSettings.AppDefaults;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ModelImportPreview(
            Path.GetFullPath(zipPath),
            models[0].FullName,
            indices[0].FullName,
            jsonFiles.FirstOrDefault()?.FullName,
            Path.GetFileNameWithoutExtension(models[0].Name),
            settings);
    }

    public bool HasConflict(string modelName)
    {
        var paths = GetTargetPaths(ValidateModelName(modelName));
        return File.Exists(paths.ModelPath) || File.Exists(paths.IndexPath) ||
               File.Exists(paths.ConfigPath) || File.Exists(paths.LegacyConfigPath);
    }

    public string GetModelPath(string modelName) =>
        GetTargetPaths(ValidateModelName(modelName)).ModelPath;

    public async Task<ModelImportResult> ImportAsync(
        ModelImportPreview preview,
        string requestedName,
        ModelTuningSettings settings,
        CancellationToken cancellationToken = default)
    {
        var modelName = ValidateModelName(requestedName);
        if (!ModelTuningSettings.TryValidate(settings, out var normalized, out var validationError))
        {
            throw new InvalidDataException(validationError);
        }

        Directory.CreateDirectory(WeightsDirectory);
        Directory.CreateDirectory(IndicesDirectory);
        var paths = GetTargetPaths(modelName);
        var existed = File.Exists(paths.ModelPath) || File.Exists(paths.IndexPath) ||
                      File.Exists(paths.ConfigPath) || File.Exists(paths.LegacyConfigPath);
        var transactionId = Guid.NewGuid().ToString("N");
        var modelTemp = paths.ModelPath + $".{transactionId}.import";
        var indexTemp = paths.IndexPath + $".{transactionId}.import";
        var configTemp = paths.ConfigPath + $".{transactionId}.import";
        var shouldWriteConfig = !normalized.IsAppDefault;

        try
        {
            await CopyArchiveEntriesAsync(preview, modelTemp, indexTemp, cancellationToken);
            if (shouldWriteConfig)
            {
                await File.WriteAllBytesAsync(
                    configTemp,
                    ModelProfileService.SerializeSettings(normalized),
                    cancellationToken);
            }

            await CommitImportTransactionAsync(
                paths,
                modelTemp,
                indexTemp,
                shouldWriteConfig ? configTemp : null,
                transactionId,
                cancellationToken);
        }
        finally
        {
            DeleteIfExists(modelTemp);
            DeleteIfExists(indexTemp);
            DeleteIfExists(configTemp);
        }

        return new ModelImportResult(
            modelName,
            paths.ModelPath,
            paths.IndexPath,
            existed,
            shouldWriteConfig);
    }

    public async Task<IReadOnlyList<ModelExportResult>> ExportAsync(
        IReadOnlyList<ManagedModel> models,
        string destinationDirectory,
        bool overwrite,
        IProgress<ModelTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (models.Count == 0) return [];
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException("请选择导出目录。", nameof(destinationDirectory));
        }
        Directory.CreateDirectory(destinationDirectory);

        var results = new List<ModelExportResult>(models.Count);
        for (var index = 0; index < models.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = models[index];
            if (model.IndexPath is null || !File.Exists(model.IndexPath))
            {
                throw new FileNotFoundException($"模型 {model.Name} 缺少同名索引文件。");
            }
            if (!File.Exists(model.ModelPath))
            {
                throw new FileNotFoundException($"模型文件已不存在：{model.ModelPath}");
            }

            var outputPath = Path.Combine(destinationDirectory, model.Name + ".zip");
            if (File.Exists(outputPath) && !overwrite)
            {
                throw new IOException($"导出文件已存在：{outputPath}");
            }
            var temporaryPath = outputPath + $".{Guid.NewGuid():N}.tmp";
            progress?.Report(new ModelTransferProgress(index, models.Count, $"正在导出 {model.Name}…"));
            try
            {
                await CreateModelArchiveAsync(model, temporaryPath, cancellationToken);
                File.Move(temporaryPath, outputPath, overwrite);
            }
            finally
            {
                DeleteIfExists(temporaryPath);
            }
            results.Add(new ModelExportResult(model.Name, outputPath));
            progress?.Report(new ModelTransferProgress(index + 1, models.Count, $"已导出 {model.Name}"));
        }
        return results;
    }

    public IReadOnlyList<string> GetExistingExportPaths(
        IEnumerable<ManagedModel> models,
        string destinationDirectory) =>
        models.Select(model => Path.Combine(destinationDirectory, model.Name + ".zip"))
            .Where(File.Exists)
            .ToList();

    internal static string ValidateModelName(string value)
    {
        var name = value.Trim();
        if (name.EndsWith(".pth", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".index", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileNameWithoutExtension(name);
        }
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("模型名称不能为空。");
        if (name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.'))
        {
            throw new InvalidDataException("模型名称不能以空格或句点结尾。");
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
        {
            throw new InvalidDataException("模型名称包含 Windows 不允许的字符。");
        }
        if (ReservedWindowsNames.Contains(name.Split('.')[0]))
        {
            throw new InvalidDataException("该名称是 Windows 保留名称，请使用其他模型名。");
        }
        return name;
    }

    private async Task CopyArchiveEntriesAsync(
        ModelImportPreview preview,
        string modelDestination,
        string indexDestination,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            preview.ZipPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        var modelEntry = archive.GetEntry(preview.ModelEntryName)
                         ?? throw new InvalidDataException("ZIP 内容在预览后已发生变化，请重新选择文件。");
        var indexEntry = archive.GetEntry(preview.IndexEntryName)
                         ?? throw new InvalidDataException("ZIP 内容在预览后已发生变化，请重新选择文件。");
        await CopyEntryAsync(modelEntry, modelDestination, cancellationToken);
        await CopyEntryAsync(indexEntry, indexDestination, cancellationToken);
    }

    private static async Task CopyEntryAsync(
        ZipArchiveEntry entry,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var source = entry.Open();
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(target, 128 * 1024, cancellationToken);
        await target.FlushAsync(cancellationToken);
    }

    private static async Task CommitImportTransactionAsync(
        TargetPaths paths,
        string modelTemp,
        string indexTemp,
        string? configTemp,
        string transactionId,
        CancellationToken cancellationToken)
    {
        var targets = new[]
        {
            paths.ModelPath,
            paths.IndexPath,
            paths.ConfigPath,
            paths.LegacyConfigPath,
        }.Distinct(StringComparer.OrdinalIgnoreCase);
        var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var installed = new List<string>();
        var committed = false;
        try
        {
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(target)) continue;
                var backup = target + $".{transactionId}.backup";
                File.Move(target, backup);
                backups[target] = backup;
            }

            File.Move(modelTemp, paths.ModelPath);
            installed.Add(paths.ModelPath);
            File.Move(indexTemp, paths.IndexPath);
            installed.Add(paths.IndexPath);
            if (configTemp is not null)
            {
                File.Move(configTemp, paths.ConfigPath);
                installed.Add(paths.ConfigPath);
            }
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                foreach (var target in installed.AsEnumerable().Reverse()) DeleteIfExists(target);
                foreach (var backup in backups)
                {
                    if (File.Exists(backup.Value)) File.Move(backup.Value, backup.Key, overwrite: true);
                }
            }
            if (committed)
            {
                foreach (var backup in backups.Values) DeleteIfExists(backup);
            }
        }
        await Task.CompletedTask;
    }

    private async Task CreateModelArchiveAsync(
        ManagedModel model,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await AddFileToArchiveAsync(archive, model.ModelPath, model.Name + ".pth", cancellationToken);
        await AddFileToArchiveAsync(archive, model.IndexPath!, model.Name + ".index", cancellationToken);
        var settings = await _profiles.ReadSettingsAsync(model.ModelPath, cancellationToken);
        if (!settings.IsAppDefault)
        {
            var entry = archive.CreateEntry(model.Name + ".rvcstudio.json", CompressionLevel.Fastest);
            await using var target = entry.Open();
            var bytes = ModelProfileService.SerializeSettings(settings);
            await target.WriteAsync(bytes, cancellationToken);
        }
    }

    private static async Task AddFileToArchiveAsync(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var target = entry.Open();
        await source.CopyToAsync(target, 128 * 1024, cancellationToken);
    }

    private TargetPaths GetTargetPaths(string modelName) => new(
        Path.Combine(WeightsDirectory, modelName + ".pth"),
        Path.Combine(IndicesDirectory, modelName + ".index"),
        Path.Combine(WeightsDirectory, modelName + ".rvcstudio.json"),
        Path.Combine(WeightsDirectory, modelName + ".pth.rvcstudio.json"));

    private static bool HasExtension(string path, string extension) =>
        string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed record TargetPaths(
        string ModelPath,
        string IndexPath,
        string ConfigPath,
        string LegacyConfigPath);
}
