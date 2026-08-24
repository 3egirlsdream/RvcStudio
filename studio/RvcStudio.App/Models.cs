namespace RvcStudio.App;

public sealed record AudioDevice(
    string Id,
    string Name,
    string Hostapi,
    int MaxInputChannels,
    int MaxOutputChannels,
    int DefaultSamplerate,
    bool IsDefault)
{
    public string DisplayName => Name;
}

public sealed record EngineCapabilities(
    bool CudaAvailable,
    string CudaVersion,
    string TorchVersion,
    string GpuName,
    bool FcpeAvailable,
    bool CudaGraphEnabled);

public sealed record ModelInspection(
    string Version,
    int SampleRate,
    bool SupportsPitch,
    string Info);

public sealed record ModelChoice(
    string Name,
    string ModelPath,
    string IndexPath,
    bool IsExternal)
{
    public string DisplayName => IsExternal ? $"外部 · {Name}" : Name;
}

public sealed record EngineStatus(
    bool Running,
    bool RestartRequired,
    bool ModelLoaded,
    string ModelPath,
    double InputLevel,
    double OutputLevel,
    double InferMs,
    int DelayMs,
    int Samplerate,
    int Channels,
    string LastError,
    string AudioStatus,
    Dictionary<string, object?> Config);
