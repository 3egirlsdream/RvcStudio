using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace RvcStudio.App;

public readonly record struct DailyQuotaSnapshot(
    TimeSpan Remaining,
    bool Exhausted,
    bool DayRefreshed,
    bool IntegrityIssue);

/// <summary>
/// Tracks free-account usage against a monotonic clock and persists it with
/// DPAPI.  A protected registry anchor makes deleting or replacing only the
/// quota file fail closed for the current day.
/// </summary>
public sealed class DailyUsageQuotaService : IAsyncDisposable
{
    public static readonly TimeSpan DailyLimit = TimeSpan.FromHours(1);

    private const int StateVersion = 1;
    private const string RegistryPath = @"Software\RvcStudio";
    private const string RegistryValueName = "UsageAnchorV1";
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdlePersistInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ClockRollbackTolerance = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RvcStudio",
        "usage.dat");

    private StoredQuotaState _state = new();
    private bool _initialized;
    private bool _dirty;
    private string? _activeSubject;
    private long _activeTimestamp;
    private long _lastPersistTimestamp;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            var today = TodayText();
            var fileExists = File.Exists(_statePath);
            ProtectedAnchor? anchor = null;
            var anchorExists = false;
            var integrityFailure = false;

            try
            {
                anchor = ReadAnchor();
                anchorExists = anchor is not null;
            }
            catch (Exception exception) when (exception is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
            {
                integrityFailure = true;
            }

            try
            {
                if (fileExists)
                {
                    _state = await ProtectedLocalStorage.ReadJsonAsync<StoredQuotaState>(_statePath, cancellationToken)
                        ?? throw new CryptographicException("用量记录为空。");
                }
            }
            catch (Exception exception) when (exception is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
            {
                integrityFailure = true;
            }

            if (!fileExists && !anchorExists && !integrityFailure)
            {
                _state = CreateNewState(today);
                _dirty = true;
            }
            else if (!fileExists || !anchorExists ||
                     _state.Version != StateVersion ||
                     string.IsNullOrWhiteSpace(_state.InstallationId) ||
                     anchor?.InstallationId != _state.InstallationId ||
                     !GenerationsMatch(_state.Generation, anchor?.Generation ?? -1) ||
                     !ValidateState(_state))
            {
                integrityFailure = true;
            }
            else if (_state.Generation != anchor!.Generation)
            {
                // The file is written before the anchor. A one-generation lead
                // means the prior process ended between those two atomic writes.
                _dirty = true;
            }

            if (integrityFailure)
            {
                var installationId = !string.IsNullOrWhiteSpace(_state.InstallationId)
                    ? _state.InstallationId
                    : anchor?.InstallationId ?? Guid.NewGuid().ToString("N");
                _state = CreateNewState(today, installationId);
                _state.BlockedDate = today;
                _dirty = true;
            }

            _initialized = true;
            _activeTimestamp = Stopwatch.GetTimestamp();
            _lastPersistTimestamp = _activeTimestamp;
            await PersistIfNeededAsync(force: _dirty, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DailyQuotaSnapshot> UpdateAsync(
        string subject,
        bool trackUsage,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var timestamp = Stopwatch.GetTimestamp();
            var nowUtc = DateTime.UtcNow;
            var today = TodayText();
            var dayRefreshed = ObserveDateAndClock(today, nowUtc);
            var wasTracking = _activeSubject is not null;
            var contextChanged = !string.Equals(_activeSubject, trackUsage ? subject : null, StringComparison.Ordinal);

            SettleActiveUsage(timestamp, today);

            var integrityIssue = HasIntegrityIssue(today, nowUtc);
            var remaining = integrityIssue ? TimeSpan.Zero : GetRemaining(subject, today);
            var exhausted = remaining <= TimeSpan.Zero;
            _activeSubject = trackUsage && !exhausted ? subject : null;
            _activeTimestamp = timestamp;

            await PersistIfNeededAsync(
                force: contextChanged || dayRefreshed || (wasTracking && exhausted),
                cancellationToken);

            return new DailyQuotaSnapshot(remaining, exhausted, dayRefreshed, integrityIssue);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DailyQuotaSnapshot> StopTrackingAsync(
        string subject,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await UpdateAsync(subject, trackUsage: false, cancellationToken);
        await FlushAsync(cancellationToken);
        return snapshot;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var timestamp = Stopwatch.GetTimestamp();
            var today = TodayText();
            ObserveDateAndClock(today, DateTime.UtcNow);
            SettleActiveUsage(timestamp, today);
            await PersistIfNeededAsync(force: true, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SettleActiveUsage(long timestamp, string today)
    {
        if (_activeSubject is null)
        {
            _activeTimestamp = timestamp;
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_activeTimestamp, timestamp);
        _activeTimestamp = timestamp;
        if (elapsed <= TimeSpan.Zero || HasIntegrityIssue(today, DateTime.UtcNow)) return;

        var record = GetOrCreateRecord(_activeSubject, today);
        record.UsedTicks = Math.Min(DailyLimit.Ticks, checked(record.UsedTicks + elapsed.Ticks));
        _dirty = true;
    }

    private bool ObserveDateAndClock(string today, DateTime nowUtc)
    {
        var refreshed = false;
        if (string.IsNullOrWhiteSpace(_state.LatestLocalDate))
        {
            _state.LatestLocalDate = today;
            refreshed = true;
            _dirty = true;
        }
        else if (CompareDates(today, _state.LatestLocalDate) > 0)
        {
            _state.LatestLocalDate = today;
            refreshed = true;
            _dirty = true;
        }

        if (nowUtc > _state.LastSeenUtc)
        {
            _state.LastSeenUtc = nowUtc;
            _dirty = true;
        }
        return refreshed;
    }

    private bool HasIntegrityIssue(string today, DateTime nowUtc)
    {
        if (string.Equals(_state.BlockedDate, today, StringComparison.Ordinal)) return true;
        if (CompareDates(today, _state.LatestLocalDate) < 0) return true;
        return nowUtc + ClockRollbackTolerance < _state.LastSeenUtc;
    }

    private TimeSpan GetRemaining(string subject, string today)
    {
        var record = GetOrCreateRecord(subject, today);
        return TimeSpan.FromTicks(Math.Max(0, DailyLimit.Ticks - record.UsedTicks));
    }

    private StoredQuotaDay GetOrCreateRecord(string subject, string today)
    {
        if (!_state.Subjects.TryGetValue(subject, out var record) ||
            !string.Equals(record.Date, today, StringComparison.Ordinal))
        {
            record = new StoredQuotaDay { Date = today };
            _state.Subjects[subject] = record;
            _dirty = true;
        }
        return record;
    }

    private async Task PersistIfNeededAsync(bool force, CancellationToken cancellationToken)
    {
        if (!_dirty) return;
        var timestamp = Stopwatch.GetTimestamp();
        var interval = _activeSubject is null ? IdlePersistInterval : PersistInterval;
        if (!force && Stopwatch.GetElapsedTime(_lastPersistTimestamp, timestamp) < interval) return;

        if (_state.Generation == long.MaxValue)
        {
            throw new CryptographicException("用量记录版本无效。");
        }
        _state.Generation++;
        await ProtectedLocalStorage.WriteJsonAsync(_statePath, _state, cancellationToken);
        WriteAnchor(new ProtectedAnchor
        {
            InstallationId = _state.InstallationId,
            Generation = _state.Generation,
        });
        try
        {
            File.SetAttributes(_statePath, File.GetAttributes(_statePath) | FileAttributes.Hidden);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Encryption and the registry anchor provide integrity even if the
            // optional hidden attribute cannot be applied.
        }
        _lastPersistTimestamp = timestamp;
        _dirty = false;
    }

    private static StoredQuotaState CreateNewState(string today, string? installationId = null) => new()
    {
        Version = StateVersion,
        InstallationId = installationId ?? Guid.NewGuid().ToString("N"),
        LatestLocalDate = today,
        LastSeenUtc = DateTime.UtcNow,
    };

    private static bool ValidateState(StoredQuotaState state)
    {
        if (!TryParseDate(state.LatestLocalDate, out _)) return false;
        if (!string.IsNullOrEmpty(state.BlockedDate) && !TryParseDate(state.BlockedDate, out _)) return false;
        if (state.LastSeenUtc.Kind != DateTimeKind.Utc) return false;
        if (state.Generation < 0 || state.Subjects is null) return false;
        return state.Subjects.All(pair =>
            !string.IsNullOrWhiteSpace(pair.Key) &&
            pair.Value is not null &&
            TryParseDate(pair.Value.Date, out _) &&
            pair.Value.UsedTicks >= 0 &&
            pair.Value.UsedTicks <= DailyLimit.Ticks);
    }

    private static bool GenerationsMatch(long stateGeneration, long anchorGeneration) =>
        stateGeneration == anchorGeneration ||
        (anchorGeneration >= 0 && anchorGeneration < long.MaxValue && stateGeneration == anchorGeneration + 1);

    private static int CompareDates(string left, string right)
    {
        if (!TryParseDate(left, out var leftDate) || !TryParseDate(right, out var rightDate)) return 0;
        return leftDate.CompareTo(rightDate);
    }

    private static bool TryParseDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static string TodayText() => DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ProtectedAnchor? ReadAnchor()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("RVC Studio 的用量记录锚点需要 Windows 注册表。");
        }
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
        var protectedBytes = key?.GetValue(RegistryValueName) as byte[];
        return protectedBytes is null ? null : ProtectedLocalStorage.UnprotectJson<ProtectedAnchor>(protectedBytes);
    }

    private static void WriteAnchor(ProtectedAnchor anchor)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("RVC Studio 的用量记录锚点需要 Windows 注册表。");
        }
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new IOException("无法创建用量记录锚点。");
        key.SetValue(RegistryValueName, ProtectedLocalStorage.ProtectJson(anchor), RegistryValueKind.Binary);
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync();
        _gate.Dispose();
    }

    private sealed class ProtectedAnchor
    {
        public string InstallationId { get; set; } = string.Empty;
        public long Generation { get; set; }
    }

    private sealed class StoredQuotaState
    {
        public int Version { get; set; } = StateVersion;
        public string InstallationId { get; set; } = string.Empty;
        public long Generation { get; set; }
        public string LatestLocalDate { get; set; } = string.Empty;
        public string BlockedDate { get; set; } = string.Empty;
        public DateTime LastSeenUtc { get; set; }
        public Dictionary<string, StoredQuotaDay> Subjects { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class StoredQuotaDay
    {
        public string Date { get; set; } = string.Empty;
        public long UsedTicks { get; set; }
    }
}
