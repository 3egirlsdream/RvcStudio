using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RvcStudio.App;

public sealed class EngineClient : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(90);
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private Process? _process;
    private int _port;
    private string _token = string.Empty;
    private string _rvcRoot = string.Empty;
    private string _bootstrapPath = string.Empty;

    public string EngineLogPath { get; private set; } = string.Empty;
    public string RvcRoot => _rvcRoot;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        _rvcRoot = FindRvcRoot();
        var python = Path.Combine(_rvcRoot, "runtime", "pythonw.exe");
        var service = Path.Combine(_rvcRoot, "realtime_service.py");
        if (!File.Exists(python) || !File.Exists(service))
        {
            throw new FileNotFoundException("找不到 RVC Studio 后台引擎。请确认 RVC Studio.exe 位于 RVC 整合包根目录。", service);
        }

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RvcStudio");
        var logDirectory = Path.Combine(appData, "logs");
        Directory.CreateDirectory(logDirectory);
        EngineLogPath = Path.Combine(logDirectory, "rvc-studio-engine.log");
        _port = ReserveLoopbackPort();
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _bootstrapPath = Path.Combine(Path.GetTempPath(), $"rvc-studio-{Guid.NewGuid():N}.json");
        var bootstrap = new
        {
            port = _port,
            token = _token,
            log_dir = logDirectory,
            app_config = Path.Combine(appData, "config.json"),
        };
        try
        {
            await File.WriteAllTextAsync(_bootstrapPath, JsonSerializer.Serialize(bootstrap), cancellationToken);

            _process = Process.Start(new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"-I \"{service}\" --bootstrap \"{_bootstrapPath}\"",
                WorkingDirectory = _rvcRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException("无法启动 RVC 后台引擎。");

            var deadline = DateTime.UtcNow.Add(StartupTimeout);
            Exception? lastException = null;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_process.HasExited)
                {
                    throw new InvalidOperationException($"RVC 后台引擎提前退出（退出码 {_process.ExitCode}）。日志：{EngineLogPath}");
                }
                try
                {
                    await CallAsync("hello", null, cancellationToken);
                    return;
                }
                catch (Exception exception) when (exception is SocketException or IOException or InvalidOperationException)
                {
                    lastException = exception;
                    await Task.Delay(300, cancellationToken);
                }
            }
            throw new TimeoutException($"等待 RVC 后台引擎超时（{StartupTimeout.TotalSeconds:0} 秒）。日志：{EngineLogPath}\n{lastException?.Message}");
        }
        catch
        {
            CleanupFailedStart();
            throw;
        }
    }

    public async Task<EngineCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var payload = await CallAsync("get_capabilities", null, cancellationToken);
        return new EngineCapabilities(
            payload["cuda_available"]?.GetValue<bool>() ?? false,
            payload["cuda_version"]?.GetValue<string>() ?? string.Empty,
            payload["torch_version"]?.GetValue<string>() ?? string.Empty,
            payload["gpu_name"]?.GetValue<string>() ?? string.Empty,
            payload["fcpe_available"]?.GetValue<bool>() ?? false,
            payload["cuda_graph_enabled"]?.GetValue<bool>() ?? false);
    }

    public async Task<(IReadOnlyList<AudioDevice> Inputs, IReadOnlyList<AudioDevice> Outputs)> GetDevicesAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        var payload = await CallAsync(refresh ? "refresh_devices" : "get_devices", null, cancellationToken);
        return (ReadDevices(payload["inputs"]), ReadDevices(payload["outputs"]));
    }

    public async Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ParseStatus(await CallAsync("get_status", null, cancellationToken));

    public async Task<EngineStatus> UpdateConfigAsync(Dictionary<string, object?> config, CancellationToken cancellationToken = default) =>
        ParseStatus(await CallAsync("update_config", config, cancellationToken));

    public async Task<EngineStatus> StartConversionAsync(CancellationToken cancellationToken = default) =>
        ParseStatus(await CallAsync("start", null, cancellationToken));

    public async Task<EngineStatus> StopConversionAsync(CancellationToken cancellationToken = default) =>
        ParseStatus(await CallAsync("stop", null, cancellationToken));

    private async Task<JsonObject> CallAsync(string command, object? payload, CancellationToken cancellationToken)
    {
        if (_port == 0 || string.IsNullOrEmpty(_token))
        {
            throw new InvalidOperationException("RVC 后台引擎尚未启动。");
        }
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port, cancellationToken);
        await using var stream = client.GetStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
        var request = new JsonObject
        {
            ["token"] = _token,
            ["command"] = command,
            ["payload"] = payload is null ? new JsonObject() : JsonSerializer.SerializeToNode(payload, _jsonOptions),
        };
        await writer.WriteLineAsync(request.ToJsonString(_jsonOptions).AsMemory(), cancellationToken);
        var responseLine = await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("RVC 引擎没有返回响应。");
        var response = JsonNode.Parse(responseLine)?.AsObject() ?? throw new IOException("RVC 引擎返回了无效响应。");
        if (response["ok"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException(response["error"]?.GetValue<string>() ?? "RVC 引擎执行失败。");
        }
        return response["result"]?.AsObject() ?? new JsonObject();
    }

    private static IReadOnlyList<AudioDevice> ReadDevices(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }
        return array.OfType<JsonObject>().Select(item => new AudioDevice(
            item["id"]?.GetValue<string>() ?? string.Empty,
            item["name"]?.GetValue<string>() ?? string.Empty,
            item["hostapi"]?.GetValue<string>() ?? string.Empty,
            item["max_input_channels"]?.GetValue<int>() ?? 0,
            item["max_output_channels"]?.GetValue<int>() ?? 0,
            item["default_samplerate"]?.GetValue<int>() ?? 0,
            item["is_default"]?.GetValue<bool>() ?? false)).ToList();
    }

    private static EngineStatus ParseStatus(JsonObject payload)
    {
        var config = payload["config"] is JsonObject configObject
            ? configObject.ToDictionary(item => item.Key, item => item.Value is null ? null : item.Value.Deserialize<object?>())
            : new Dictionary<string, object?>();
        return new EngineStatus(
            payload["running"]?.GetValue<bool>() ?? false,
            payload["restart_required"]?.GetValue<bool>() ?? false,
            payload["model_loaded"]?.GetValue<bool>() ?? false,
            payload["model_path"]?.GetValue<string>() ?? string.Empty,
            payload["input_level"]?.GetValue<double>() ?? 0,
            payload["output_level"]?.GetValue<double>() ?? 0,
            payload["infer_ms"]?.GetValue<double>() ?? 0,
            payload["delay_ms"]?.GetValue<int>() ?? 0,
            payload["samplerate"]?.GetValue<int>() ?? 0,
            payload["channels"]?.GetValue<int>() ?? 0,
            payload["last_error"]?.GetValue<string>() ?? string.Empty,
            payload["audio_status"]?.GetValue<string>() ?? string.Empty,
            config);
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRvcRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 12; depth++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "runtime", "pythonw.exe")) &&
                File.Exists(Path.Combine(current.FullName, "realtime_service.py")))
            {
                return current.FullName;
            }
        }
        throw new DirectoryNotFoundException("未找到包含 runtime\\pythonw.exe 的 RVC 整合包根目录。");
    }

    private void CleanupFailedStart()
    {
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Preserve the original startup exception if cleanup itself fails.
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        _port = 0;
        _token = string.Empty;
        DeleteBootstrapFile();
    }

    private void DeleteBootstrapFile()
    {
        if (string.IsNullOrEmpty(_bootstrapPath))
        {
            return;
        }
        try
        {
            File.Delete(_bootstrapPath);
        }
        catch (IOException)
        {
            // The engine may still be releasing the one-time startup file.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup must not hide the original engine result.
        }
        finally
        {
            _bootstrapPath = string.Empty;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                try
                {
                    using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await CallAsync("shutdown", null, shutdownTimeout.Token);
                    await _process.WaitForExitAsync(shutdownTimeout.Token);
                }
                catch
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
            }
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _port = 0;
            _token = string.Empty;
            DeleteBootstrapFile();
        }
    }
}
