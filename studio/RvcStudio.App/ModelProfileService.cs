using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RvcStudio.App;

public sealed record ModelTuningSettings(
    string PitchMethod,
    double Pitch,
    double Formant,
    double IndexRate,
    double RmsMixRate,
    double Threshold,
    double BlockTime,
    double CrossfadeLength,
    double ExtraTime)
{
    public static ModelTuningSettings AppDefaults { get; } = new(
        "fcpe", 12, 0, 0, 0.5, -60, 0.13, 0.08, 2.01);

    public bool NearlyEquals(ModelTuningSettings other) =>
        string.Equals(PitchMethod, other.PitchMethod, StringComparison.OrdinalIgnoreCase) &&
        Close(Pitch, other.Pitch, 0.01) &&
        Close(Formant, other.Formant, 0.005) &&
        Close(IndexRate, other.IndexRate, 0.005) &&
        Close(RmsMixRate, other.RmsMixRate, 0.005) &&
        Close(Threshold, other.Threshold, 0.5) &&
        Close(BlockTime, other.BlockTime, 0.005) &&
        Close(CrossfadeLength, other.CrossfadeLength, 0.005) &&
        Close(ExtraTime, other.ExtraTime, 0.005);

    public bool IsAppDefault =>
        string.Equals(PitchMethod, AppDefaults.PitchMethod, StringComparison.OrdinalIgnoreCase) &&
        Pitch.Equals(AppDefaults.Pitch) &&
        Formant.Equals(AppDefaults.Formant) &&
        IndexRate.Equals(AppDefaults.IndexRate) &&
        RmsMixRate.Equals(AppDefaults.RmsMixRate) &&
        Threshold.Equals(AppDefaults.Threshold) &&
        BlockTime.Equals(AppDefaults.BlockTime) &&
        CrossfadeLength.Equals(AppDefaults.CrossfadeLength) &&
        ExtraTime.Equals(AppDefaults.ExtraTime);

    public static bool TryValidate(
        ModelTuningSettings value,
        out ModelTuningSettings normalized,
        out string error)
    {
        var method = value.PitchMethod.Trim().ToLowerInvariant();
        if (method is not ("fcpe" or "rmvpe" or "pm"))
        {
            normalized = AppDefaults;
            error = "音高算法必须是 fcpe、rmvpe 或 pm。";
            return false;
        }

        var checks = new (string Name, double Value, double Minimum, double Maximum)[]
        {
            ("Pitch", value.Pitch, -16, 16),
            ("Formant", value.Formant, -2, 2),
            ("Index", value.IndexRate, 0, 1),
            ("RMS", value.RmsMixRate, 0, 1),
            ("门限", value.Threshold, -80, -20),
            ("分块", value.BlockTime, 0.02, 1.5),
            ("交叉淡化", value.CrossfadeLength, 0.01, 0.15),
            ("额外推理", value.ExtraTime, 0.05, 5),
        };
        foreach (var check in checks)
        {
            if (!double.IsFinite(check.Value) || check.Value < check.Minimum || check.Value > check.Maximum)
            {
                normalized = AppDefaults;
                error = $"{check.Name} 必须在 {check.Minimum.ToString(CultureInfo.InvariantCulture)} 到 {check.Maximum.ToString(CultureInfo.InvariantCulture)} 之间。";
                return false;
            }
        }

        normalized = value with { PitchMethod = method };
        error = string.Empty;
        return true;
    }

    private static bool Close(double left, double right, double tolerance) =>
        Math.Abs(left - right) <= tolerance;
}

public sealed record ModelMetadata(
    string Version,
    int SampleRate,
    bool SupportsPitch,
    string Language,
    string Hubert,
    string SupportStatus);

public sealed record ModelProfileSession(
    string ModelId,
    ModelTuningSettings Current,
    ModelMetadata Metadata);

internal sealed record ModelSidecarData(
    ModelTuningSettings Settings,
    string Version,
    int SampleRate,
    string Language,
    string Hubert,
    string SupportStatus);

public sealed class ModelProfileService
{
    private const int SchemaVersion = 1;
    private readonly string _legacyStorePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _pendingLock = new();
    private readonly Dictionary<string, ModelTuningSettings> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    public ModelProfileService(string? legacyStorePath = null)
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RvcStudio");
        _legacyStorePath = legacyStorePath ?? Path.Combine(appData, "model-profiles.json");
    }

    public string MigrationWarning { get; private set; } = string.Empty;

    public async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        await MigrateLegacyProfilesAsync(cancellationToken);

    public async Task<ModelProfileSession> OpenAsync(
        string modelPath,
        ModelInspection inspection,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(modelPath);
        var sidecar = await ReadSidecarAsync(fullPath, cancellationToken);
        var current = sidecar?.Settings ?? ModelTuningSettings.AppDefaults;
        var metadata = MergeMetadata(inspection, sidecar);
        return new ModelProfileSession(
            fullPath,
            current,
            metadata);
    }

    public void UpdateCurrent(string modelId, ModelTuningSettings settings)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return;
        if (!ModelTuningSettings.TryValidate(settings, out var normalized, out _)) return;
        lock (_pendingLock)
        {
            _pending[Path.GetFullPath(modelId)] = normalized;
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        lock (_pendingLock)
        {
            if (_pending.Count == 0) return;
        }

        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, ModelTuningSettings> snapshot;
            lock (_pendingLock)
            {
                snapshot = new Dictionary<string, ModelTuningSettings>(_pending, StringComparer.OrdinalIgnoreCase);
            }
            foreach (var item in snapshot)
            {
                await WriteSettingsAsync(item.Key, item.Value, cancellationToken);
                lock (_pendingLock)
                {
                    if (_pending.TryGetValue(item.Key, out var pending) && pending == item.Value)
                    {
                        _pending.Remove(item.Key);
                    }
                }
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    internal async Task<ModelTuningSettings> ReadSettingsAsync(
        string modelPath,
        CancellationToken cancellationToken = default) =>
        (await ReadSidecarAsync(Path.GetFullPath(modelPath), cancellationToken))?.Settings
        ?? ModelTuningSettings.AppDefaults;

    internal async Task WriteSettingsAsync(
        string modelPath,
        ModelTuningSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!ModelTuningSettings.TryValidate(settings, out var normalized, out var error))
        {
            throw new InvalidDataException(error);
        }

        var fullPath = Path.GetFullPath(modelPath);
        var canonicalPath = GetSidecarPath(fullPath);
        var legacyPath = fullPath + ".rvcstudio.json";
        if (normalized.IsAppDefault)
        {
            DeleteIfExists(canonicalPath);
            if (!string.Equals(canonicalPath, legacyPath, StringComparison.OrdinalIgnoreCase))
            {
                DeleteIfExists(legacyPath);
            }
            return;
        }

        var directory = Path.GetDirectoryName(canonicalPath)
                        ?? throw new InvalidOperationException("模型配置路径无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = canonicalPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, SerializeSettings(normalized), cancellationToken);
            File.Move(temporaryPath, canonicalPath, overwrite: true);
            if (!string.Equals(canonicalPath, legacyPath, StringComparison.OrdinalIgnoreCase))
            {
                DeleteIfExists(legacyPath);
            }
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    internal static string GetSidecarPath(string modelPath) =>
        Path.ChangeExtension(modelPath, ".rvcstudio.json");

    internal static byte[] SerializeSettings(ModelTuningSettings settings)
    {
        var payload = new
        {
            schemaVersion = SchemaVersion,
            recommended = new
            {
                f0method = settings.PitchMethod,
                pitch = settings.Pitch,
                formant = settings.Formant,
                indexRate = settings.IndexRate,
                rmsMixRate = settings.RmsMixRate,
                threshold = settings.Threshold,
                blockTime = settings.BlockTime,
                crossfadeLength = settings.CrossfadeLength,
                extraTime = settings.ExtraTime,
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
    }

    internal static async Task<ModelTuningSettings?> TryReadSettingsFromJsonAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return TryParseSettings(document.RootElement, out var settings) ? settings : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<ModelSidecarData?> ReadSidecarAsync(
        string modelPath,
        CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            GetSidecarPath(modelPath),
            modelPath + ".rvcstudio.json",
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                await using var stream = File.OpenRead(candidate);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (!TryParseSettings(document.RootElement, out var settings)) continue;
                var metadataRoot = TryGetObject(document.RootElement, "model", "metadata")
                                   ?? document.RootElement;
                return new ModelSidecarData(
                    settings,
                    ReadString(metadataRoot, string.Empty, "version", "baseModel", "base_model"),
                    ReadSampleRate(metadataRoot, "sampleRate", "sample_rate", "samplerate"),
                    ReadString(metadataRoot, string.Empty, "language"),
                    ReadString(metadataRoot, string.Empty, "hubert", "hubertModel", "hubert_model"),
                    ReadSupportStatus(metadataRoot, "supportStatus", "support_status", "supported"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // An invalid optional sidecar silently falls back to application defaults.
            }
        }
        return null;
    }

    private static bool TryParseSettings(JsonElement root, out ModelTuningSettings settings)
    {
        var configuration = TryGetObject(root, "recommended", "recommendation", "defaults", "settings") ?? root;
        var fallback = ModelTuningSettings.AppDefaults;

        if (!TryReadString(configuration, fallback.PitchMethod, out var method,
                "f0method", "pitchMethod", "baseAlgorithm", "algorithm") ||
            !TryReadDouble(configuration, fallback.Pitch, out var pitch, "pitch") ||
            !TryReadDouble(configuration, fallback.Formant, out var formant, "formant") ||
            !TryReadDouble(configuration, fallback.IndexRate, out var indexRate, "indexRate", "index_rate") ||
            !TryReadDouble(configuration, fallback.RmsMixRate, out var rmsMixRate,
                "rmsMixRate", "rms_mix_rate", "volumeFactor", "loudnessFactor") ||
            !TryReadDouble(configuration, fallback.Threshold, out var threshold, "threshold", "threhold") ||
            !TryReadDouble(configuration, fallback.BlockTime, out var blockTime,
                "blockTime", "block_time", "sampleLength") ||
            !TryReadDouble(configuration, fallback.CrossfadeLength, out var crossfadeLength,
                "crossfadeLength", "crossfade_length", "fadeLength") ||
            !TryReadDouble(configuration, fallback.ExtraTime, out var extraTime,
                "extraTime", "extra_time", "extraInferenceTime"))
        {
            settings = fallback;
            return false;
        }

        return ModelTuningSettings.TryValidate(new ModelTuningSettings(
            method,
            pitch,
            formant,
            indexRate,
            rmsMixRate,
            threshold,
            blockTime,
            crossfadeLength,
            extraTime), out settings, out _);
    }

    private async Task MigrateLegacyProfilesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_legacyStorePath)) return;
        LegacyProfileStore? store;
        try
        {
            await using var stream = File.OpenRead(_legacyStorePath);
            store = await JsonSerializer.DeserializeAsync<LegacyProfileStore>(stream, _jsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            MigrationWarning = $"旧模型参数文件无法读取，已保留原文件：{exception.Message}";
            return;
        }

        var failures = new List<string>();
        foreach (var profile in store?.Profiles.Values ?? Enumerable.Empty<LegacyStoredModelProfile>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(profile.LastKnownPath) || profile.Current is null) continue;
            try
            {
                if (!File.Exists(profile.LastKnownPath)) continue;
                await WriteSettingsAsync(profile.LastKnownPath, profile.Current, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or ArgumentException or NotSupportedException)
            {
                failures.Add($"{Path.GetFileName(profile.LastKnownPath)}：{exception.Message}");
            }
        }

        if (failures.Count == 0)
        {
            try
            {
                File.Delete(_legacyStorePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                MigrationWarning = $"旧模型参数已迁移，但旧文件无法删除：{exception.Message}";
            }
        }
        else
        {
            MigrationWarning = "部分旧模型参数未能迁移，原文件已保留：" + string.Join("；", failures);
        }
    }

    private static ModelMetadata MergeMetadata(ModelInspection inspection, ModelSidecarData? sidecar)
    {
        var version = string.IsNullOrWhiteSpace(inspection.Version)
            ? FirstNotBlank(sidecar?.Version, "未知版本")
            : inspection.Version;
        var sampleRate = inspection.SampleRate > 0 ? inspection.SampleRate : sidecar?.SampleRate ?? 0;
        return new ModelMetadata(
            version,
            sampleRate,
            inspection.SupportsPitch,
            FirstNotBlank(sidecar?.Language, "未标注"),
            FirstNotBlank(sidecar?.Hubert, "内置英文 HuBERT / ContentVec"),
            FirstNotBlank(sidecar?.SupportStatus, "支持"));
    }

    private static JsonElement? TryGetObject(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
        {
            if (names.Any(name => SameName(property.Name, name)) && property.Value.ValueKind == JsonValueKind.Object)
            {
                return property.Value;
            }
        }
        return null;
    }

    private static JsonElement? FindProperty(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
        {
            if (names.Any(name => SameName(property.Name, name))) return property.Value;
        }
        return null;
    }

    private static bool TryReadString(
        JsonElement root,
        string fallback,
        out string value,
        params string[] names)
    {
        var property = FindProperty(root, names);
        if (property is null)
        {
            value = fallback;
            return true;
        }
        if (property.Value.ValueKind != JsonValueKind.String)
        {
            value = fallback;
            return false;
        }
        value = property.Value.GetString() ?? fallback;
        return true;
    }

    private static bool TryReadDouble(
        JsonElement root,
        double fallback,
        out double value,
        params string[] names)
    {
        var property = FindProperty(root, names);
        if (property is null)
        {
            value = fallback;
            return true;
        }
        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out value))
        {
            return double.IsFinite(value);
        }
        if (property.Value.ValueKind == JsonValueKind.String &&
            TryParseNumber(property.Value.GetString(), out value))
        {
            return true;
        }
        value = fallback;
        return false;
    }

    private static bool TryParseNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private static string ReadString(JsonElement root, string fallback, params string[] names)
    {
        var value = FindProperty(root, names);
        if (value is null) return fallback;
        return value.Value.ValueKind == JsonValueKind.String
            ? value.Value.GetString() ?? fallback
            : value.Value.ToString();
    }

    private static int ReadSampleRate(JsonElement root, params string[] names)
    {
        var value = FindProperty(root, names);
        if (value is null) return 0;
        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out var number)) return number;
        var text = value.Value.ToString().Trim();
        if (text.EndsWith("k", StringComparison.OrdinalIgnoreCase) &&
            TryParseNumber(text[..^1], out var kilohertz))
        {
            return (int)Math.Round(kilohertz * 1000);
        }
        return int.TryParse(text, out number) ? number : 0;
    }

    private static string ReadSupportStatus(JsonElement root, params string[] names)
    {
        var value = FindProperty(root, names);
        if (value is null) return string.Empty;
        if (value.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.Value.GetBoolean() ? "支持" : "不支持";
        }
        return value.Value.ToString();
    }

    private static bool SameName(string left, string right) =>
        NormalizeName(left) == NormalizeName(right);

    private static string NormalizeName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed class LegacyProfileStore
    {
        public Dictionary<string, LegacyStoredModelProfile> Profiles { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class LegacyStoredModelProfile
    {
        public string LastKnownPath { get; set; } = string.Empty;
        public ModelTuningSettings Current { get; set; } = ModelTuningSettings.AppDefaults;
    }
}
