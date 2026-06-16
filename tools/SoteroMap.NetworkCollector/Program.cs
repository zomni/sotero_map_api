using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Este colector debe ejecutarse en Windows.");
    return 1;
}

var configPath = ResolveConfigPath(args);
var watchMode = args.Any(arg => arg.Equals("--watch", StringComparison.OrdinalIgnoreCase));
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"No se encontro el archivo de configuracion: {configPath}");
    Console.Error.WriteLine("Copia appsettings.example.json a appsettings.local.json y ajustalo.");
    return 1;
}

CollectorOptions options;
try
{
    options = LoadOptions(configPath);
    options.ConfigurationDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? AppContext.BaseDirectory;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"No fue posible cargar la configuracion: {ex.Message}");
    return 1;
}

if (string.IsNullOrWhiteSpace(options.ApiBaseUrl))
{
    Console.Error.WriteLine("ApiBaseUrl es obligatorio.");
    return 1;
}

if (options.ScanCidrs.Count == 0)
{
    Console.Error.WriteLine("Debes configurar al menos un rango en ScanCidrs.");
    return 1;
}

ProbeCredential? credential = null;
if ((options.ResolveSessions || options.ResolveHardware) && options.PromptForCredential)
{
    credential = PromptForCredential(options);
}
else if (!string.IsNullOrWhiteSpace(options.Username) && !string.IsNullOrWhiteSpace(options.Password))
{
    credential = new ProbeCredential(options.Username.Trim(), options.Password, options.Domain.Trim());
}

Console.WriteLine("Iniciando colector de telemetria...");
Console.WriteLine($"API: {options.ApiBaseUrl}");
Console.WriteLine($"Rangos: {string.Join(", ", options.ScanCidrs)}");

var collector = new Collector(options, credential);

if (watchMode || options.WatchMode)
{
    Console.WriteLine("Modo agente activo. Esperando solicitudes del backend...");
    await RunAgentLoopAsync(options, collector, CancellationToken.None);
    return 0;
}

var request = await collector.BuildRequestAsync("full", null, CancellationToken.None, null);

Console.WriteLine($"Equipos detectados: {request.Devices.Count}");
Console.WriteLine($"Identidades detectadas: {request.Users.Count}");

var ingestResult = await PostToApiAsync(options, request, CancellationToken.None);
Console.WriteLine($"Snapshot registrado: {ingestResult?.SnapshotId}");
Console.WriteLine($"Riesgo general: {ingestResult?.OverallRiskLevel} ({ingestResult?.OverallRiskScore})");
Console.WriteLine(ingestResult?.Notes);
return 0;

static string ResolveConfigPath(string[] args)
{
    var argumentPath = args
        .SkipWhile(arg => !arg.Equals("--config", StringComparison.OrdinalIgnoreCase))
        .Skip(1)
        .FirstOrDefault();

    if (!string.IsNullOrWhiteSpace(argumentPath))
    {
        return Path.GetFullPath(argumentPath);
    }

    return Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");
}

static CollectorOptions LoadOptions(string path)
{
    var json = File.ReadAllText(path, Encoding.UTF8);
    var options = JsonSerializer.Deserialize<CollectorOptions>(json, JsonOptions.Default)
        ?? throw new InvalidOperationException("La configuracion esta vacia.");

    options.ApiBaseUrl = options.ApiBaseUrl.Trim().TrimEnd('/');
    options.Domain = string.IsNullOrWhiteSpace(options.Domain) ? "SSMSO" : options.Domain.Trim();
    options.SourceName = string.IsNullOrWhiteSpace(options.SourceName) ? "Collector Windows SSMSO" : options.SourceName.Trim();
    options.ScanCidrs = options.ScanCidrs
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Select(static value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    options.ScanPorts = options.ScanPorts
        .Where(static port => port > 0 && port < 65536)
        .Distinct()
        .OrderBy(static port => port)
        .ToList();

    return options;
}

static ProbeCredential PromptForCredential(CollectorOptions options)
{
    Console.Write($"Dominio [{options.Domain}]: ");
    var domain = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(domain))
    {
        domain = options.Domain;
    }

    Console.Write($"Usuario [{options.Username}]: ");
    var username = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(username))
    {
        username = options.Username;
    }

    if (string.IsNullOrWhiteSpace(username))
    {
        throw new InvalidOperationException("Debes indicar un usuario de dominio.");
    }

    Console.Write("Clave: ");
    var password = ReadPassword();
    Console.WriteLine();

    return new ProbeCredential(username.Trim(), password, domain!.Trim());
}

static string ReadPassword()
{
    var buffer = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            break;
        }

        if (key.Key == ConsoleKey.Backspace && buffer.Length > 0)
        {
            buffer.Length--;
            continue;
        }

        if (!char.IsControl(key.KeyChar))
        {
            buffer.Append(key.KeyChar);
        }
    }

    return buffer.ToString();
}

static async Task<IngestResult?> PostToApiAsync(CollectorOptions options, IngestRequest request, CancellationToken cancellationToken)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    using var message = new HttpRequestMessage(HttpMethod.Post, $"{options.ApiBaseUrl}/api/network-telemetry/ingest")
    {
        Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions.Default), Encoding.UTF8, "application/json")
    };

    if (!string.IsNullOrWhiteSpace(options.ApiKey))
    {
        message.Headers.Add("X-Network-Telemetry-Key", options.ApiKey);
    }

    var response = await http.SendAsync(message, cancellationToken);
    var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"La API devolvio {(int)response.StatusCode}: {responseContent}");
    }

    return JsonSerializer.Deserialize<IngestResult>(responseContent, JsonOptions.Default);
}

static string NormalizeCollectorScanMode(string? scanMode)
    => string.Equals(scanMode, "full", StringComparison.OrdinalIgnoreCase)
        ? "full"
        : "simple";

static async Task RunAgentLoopAsync(CollectorOptions options, Collector collector, CancellationToken cancellationToken)
{
    var sharedPath = ResolveSharedPath(options);
    Directory.CreateDirectory(sharedPath);
    var requestPath = Path.Combine(sharedPath, "scan-request.json");
    var statusPath = Path.Combine(sharedPath, "scan-status.json");
    var heartbeatPath = Path.Combine(sharedPath, "agent-heartbeat.json");
    var controlPath = Path.Combine(sharedPath, "scan-control.json");
    var logPath = Path.Combine(sharedPath, "agent-debug.log");

    await AppendAgentLogAsync(logPath, $"Boot agent {options.AgentId}. SharedPath={sharedPath}", cancellationToken);
    if (!File.Exists(requestPath) && File.Exists(controlPath))
    {
        File.Delete(controlPath);
        await AppendAgentLogAsync(logPath, "Stale control file cleared on boot.", cancellationToken);
    }

    using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var heartbeatTask = RunHeartbeatLoopAsync(heartbeatPath, options, heartbeatCts.Token);

    await SaveAgentStatusAsync(statusPath, new AgentStatus
    {
        State = "idle",
        AgentId = options.AgentId,
        Message = $"Agente {options.AgentId} en espera.",
        UpdatedAtUtc = DateTime.UtcNow
    }, cancellationToken);

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await AppendAgentLogAsync(logPath, $"Loop tick. RequestExists={File.Exists(requestPath)} ControlExists={File.Exists(controlPath)}", cancellationToken);
                if (!File.Exists(requestPath))
                {
                    if (File.Exists(controlPath))
                    {
                        File.Delete(controlPath);
                        await AppendAgentLogAsync(logPath, "Stale control file cleared while idle.", cancellationToken);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollIntervalSeconds)), cancellationToken);
                    continue;
                }

                var rawRequest = await File.ReadAllTextAsync(requestPath, cancellationToken);
                await AppendAgentLogAsync(logPath, $"Request file read. Length={rawRequest.Length}", cancellationToken);
                var scanRequest = JsonSerializer.Deserialize<AgentRequest>(rawRequest, JsonOptions.Default);
                if (scanRequest is null || string.IsNullOrWhiteSpace(scanRequest.RequestId))
                {
                    await AppendAgentLogAsync(logPath, "Request invalid or empty. Waiting next tick.", cancellationToken);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollIntervalSeconds)), cancellationToken);
                    continue;
                }

                var startedAt = DateTime.UtcNow;
                await AppendAgentLogAsync(logPath, $"Request accepted. RequestId={scanRequest.RequestId} Mode={scanRequest.ScanMode}", cancellationToken);
                await SaveAgentStatusAsync(statusPath, new AgentStatus
                {
                    RequestId = scanRequest.RequestId,
                    State = "running",
                    AgentId = options.AgentId,
                    Message = $"Agente {options.AgentId} ejecutando solicitud.",
                    RequestedAtUtc = scanRequest.RequestedAtUtc,
                    RequestedByUsername = scanRequest.RequestedByUsername,
                    StartedAtUtc = startedAt,
                    UpdatedAtUtc = startedAt
                }, cancellationToken);

                using var scanTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                scanTimeoutCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, options.MaxScanMinutes)));
                var controlMonitor = new AgentControlMonitor(controlPath, scanRequest.RequestId, statusPath, options.AgentId, options.PollIntervalSeconds);

                var progressPublisher = new ScanProgressPublisher(
                    async progress =>
                    {
                        var currentState = await controlMonitor.GetEffectiveStateAsync(scanTimeoutCts.Token);
                        await SaveAgentStatusAsync(statusPath, new AgentStatus
                        {
                            RequestId = scanRequest.RequestId,
                            State = currentState,
                            AgentId = options.AgentId,
                            Message = $"Escaneando {progress.ProcessedHosts}/{progress.TotalHosts} hosts. Actual: {progress.CurrentIpAddress}",
                            RequestedAtUtc = scanRequest.RequestedAtUtc,
                            RequestedByUsername = scanRequest.RequestedByUsername,
                            StartedAtUtc = startedAt,
                            UpdatedAtUtc = DateTime.UtcNow,
                            TotalHosts = progress.TotalHosts,
                            ProcessedHosts = progress.ProcessedHosts,
                            CurrentIpAddress = progress.CurrentIpAddress,
                            CurrentHostName = progress.CurrentHostName,
                            CurrentSubnetCidr = progress.CurrentSubnetCidr,
                            CurrentStage = progress.CurrentStage
                        }, cancellationToken);
                    },
                    TimeSpan.FromSeconds(Math.Max(1, options.ProgressUpdateSeconds)));

                var scanMode = NormalizeCollectorScanMode(scanRequest.ScanMode);
                var ingestRequest = await collector.BuildRequestAsync(scanMode, controlMonitor, scanTimeoutCts.Token, progressPublisher.ReportAsync);
                await AppendAgentLogAsync(logPath, $"Scan built. Devices={ingestRequest.Devices.Count} Users={ingestRequest.Users.Count}", cancellationToken);
                await progressPublisher.FlushAsync();
                var ingestResult = await PostToApiAsync(options, ingestRequest, scanTimeoutCts.Token);
                var completedAt = DateTime.UtcNow;
                await AppendAgentLogAsync(logPath, $"Ingest completed. SnapshotId={ingestResult?.SnapshotId}", cancellationToken);

                await SaveAgentStatusAsync(statusPath, new AgentStatus
                {
                    RequestId = scanRequest.RequestId,
                    State = "completed",
                    AgentId = options.AgentId,
                    SnapshotId = ingestResult?.SnapshotId,
                    Message = ingestResult?.Notes ?? "Escaneo completado.",
                    RequestedAtUtc = scanRequest.RequestedAtUtc,
                    RequestedByUsername = scanRequest.RequestedByUsername,
                    StartedAtUtc = startedAt,
                    CompletedAtUtc = completedAt,
                    UpdatedAtUtc = completedAt,
                    TotalHosts = progressPublisher.LastSnapshot?.TotalHosts,
                    ProcessedHosts = progressPublisher.LastSnapshot?.ProcessedHosts,
                    CurrentIpAddress = progressPublisher.LastSnapshot?.CurrentIpAddress,
                    CurrentHostName = progressPublisher.LastSnapshot?.CurrentHostName,
                    CurrentSubnetCidr = progressPublisher.LastSnapshot?.CurrentSubnetCidr,
                    CurrentStage = "completed"
                }, cancellationToken);

                controlMonitor.TryClearControlFile();
                File.Delete(requestPath);
                await AppendAgentLogAsync(logPath, $"Request finished and files cleared. RequestId={scanRequest.RequestId}", cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var status = await TryReadStatusAsync(statusPath, cancellationToken);
                var timedOutAt = DateTime.UtcNow;
                await AppendAgentLogAsync(logPath, $"Operation cancelled. StatusState={status?.State}", cancellationToken);
                await SaveAgentStatusAsync(statusPath, new AgentStatus
                {
                    RequestId = status?.RequestId ?? string.Empty,
                    State = "failed",
                    AgentId = options.AgentId,
                    Message = status?.State == "stopping"
                        ? "Escaneo detenido manualmente."
                        : $"El escaneo supero el tiempo maximo de {options.MaxScanMinutes} minutos.",
                    Error = status?.State == "stopping" ? "scan-stopped" : "scan-timeout",
                    UpdatedAtUtc = timedOutAt,
                    CompletedAtUtc = timedOutAt
                }, cancellationToken);

                if (File.Exists(requestPath))
                {
                    File.Delete(requestPath);
                }
            }
            catch (Exception ex)
            {
                var status = await TryReadStatusAsync(statusPath, cancellationToken);
                await AppendAgentLogAsync(logPath, $"Unhandled error. {ex}", cancellationToken);
                await SaveAgentStatusAsync(statusPath, new AgentStatus
                {
                    RequestId = status?.RequestId ?? string.Empty,
                    State = "failed",
                    AgentId = options.AgentId,
                    Message = "El agente no pudo completar la solicitud.",
                    Error = ex.Message,
                    UpdatedAtUtc = DateTime.UtcNow,
                    CompletedAtUtc = DateTime.UtcNow
                }, cancellationToken);

                if (File.Exists(requestPath))
                {
                    File.Delete(requestPath);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollIntervalSeconds)), cancellationToken);
        }
    }
    finally
    {
        await AppendAgentLogAsync(logPath, $"Agent stopping {options.AgentId}", CancellationToken.None);
        heartbeatCts.Cancel();
        try
        {
            await heartbeatTask;
        }
        catch (OperationCanceledException)
        {
        }
    }
}

static async Task<AgentStatus?> TryReadStatusAsync(string statusPath, CancellationToken cancellationToken)
{
    if (!File.Exists(statusPath))
    {
        return null;
    }

    await using var stream = File.OpenRead(statusPath);
    return await JsonSerializer.DeserializeAsync<AgentStatus>(stream, JsonOptions.Default, cancellationToken);
}

static async Task RunHeartbeatLoopAsync(string heartbeatPath, CollectorOptions options, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await SaveAgentHeartbeatAsync(heartbeatPath, options, cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, options.PollIntervalSeconds)), cancellationToken);
    }
}

static string ResolveSharedPath(CollectorOptions options)
{
    if (string.IsNullOrWhiteSpace(options.SharedPath))
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "runtime", "network-telemetry-agent"));
    }

    var basePath = !string.IsNullOrWhiteSpace(options.ConfigurationDirectory)
        ? options.ConfigurationDirectory
        : AppContext.BaseDirectory;

    return Path.GetFullPath(options.SharedPath, basePath);
}

static async Task SaveAgentStatusAsync(string statusPath, AgentStatus status, CancellationToken cancellationToken)
{
    var directory = Path.GetDirectoryName(statusPath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    await File.WriteAllTextAsync(statusPath, JsonSerializer.Serialize(status, JsonOptions.Default), cancellationToken);
}

static async Task SaveAgentHeartbeatAsync(string heartbeatPath, CollectorOptions options, CancellationToken cancellationToken)
{
    var directory = Path.GetDirectoryName(heartbeatPath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var heartbeat = new AgentHeartbeat
    {
        AgentId = options.AgentId,
        MachineName = Environment.MachineName,
        HeartbeatAtUtc = DateTime.UtcNow,
        Version = typeof(CollectorOptions).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        Mode = "watch"
    };

    await File.WriteAllTextAsync(heartbeatPath, JsonSerializer.Serialize(heartbeat, JsonOptions.Default), cancellationToken);
}

static async Task AppendAgentLogAsync(string logPath, string message, CancellationToken cancellationToken)
{
    var directory = Path.GetDirectoryName(logPath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {message}{Environment.NewLine}";
    await File.AppendAllTextAsync(logPath, line, Encoding.UTF8, cancellationToken);
}

internal sealed class ScanProgressPublisher
{
    private readonly Func<ScanProgressSnapshot, Task> _writer;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastWrittenAtUtc = DateTime.MinValue;
    private ScanProgressSnapshot? _latest;

    public ScanProgressPublisher(Func<ScanProgressSnapshot, Task> writer, TimeSpan interval)
    {
        _writer = writer;
        _interval = interval;
    }

    public ScanProgressSnapshot? LastSnapshot => _latest;

    public async Task ReportAsync(ScanProgressSnapshot snapshot)
    {
        _latest = snapshot;

        if (snapshot.TotalHosts > 0 &&
            snapshot.ProcessedHosts < snapshot.TotalHosts &&
            DateTime.UtcNow - _lastWrittenAtUtc < _interval)
        {
            return;
        }

        await FlushAsync();
    }

    public async Task FlushAsync()
    {
        if (_latest is null)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (_latest is null)
            {
                return;
            }

            _lastWrittenAtUtc = DateTime.UtcNow;
            await _writer(_latest);
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal sealed record ScanProgressSnapshot(
    int TotalHosts,
    int ProcessedHosts,
    string CurrentIpAddress,
    string CurrentHostName,
    string CurrentSubnetCidr,
    string CurrentStage);

internal sealed class AgentControlMonitor
{
    private readonly string _controlPath;
    private readonly string _requestId;
    private readonly string _statusPath;
    private readonly string _agentId;
    private readonly int _pollIntervalSeconds;
    private string _state = "running";

    public AgentControlMonitor(string controlPath, string requestId, string statusPath, string agentId, int pollIntervalSeconds)
    {
        _controlPath = controlPath;
        _requestId = requestId;
        _statusPath = statusPath;
        _agentId = agentId;
        _pollIntervalSeconds = Math.Max(1, pollIntervalSeconds);
    }

    public async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var action = await TryReadActionAsync(cancellationToken);
            if (string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase))
            {
                _state = "stopping";
                throw new OperationCanceledException(cancellationToken);
            }

            if (!string.Equals(action, "pause", StringComparison.OrdinalIgnoreCase))
            {
                _state = "running";
                return;
            }

            if (_state != "paused")
            {
                _state = "paused";
                var current = await TryReadStatusInternalAsync(cancellationToken) ?? new AgentStatus();
                current.RequestId = _requestId;
                current.State = "paused";
                current.AgentId = _agentId;
                current.Message = "Escaneo pausado.";
                current.UpdatedAtUtc = DateTime.UtcNow;
                await SaveStatusInternalAsync(current, cancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), cancellationToken);
        }
    }

    public void ThrowIfStopped(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<string> GetEffectiveStateAsync(CancellationToken cancellationToken)
    {
        var action = await TryReadActionAsync(cancellationToken);
        if (string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase))
        {
            _state = "stopping";
            throw new OperationCanceledException(cancellationToken);
        }

        _state = string.Equals(action, "pause", StringComparison.OrdinalIgnoreCase)
            ? "paused"
            : "running";

        return _state;
    }

    public void TryClearControlFile()
    {
        if (File.Exists(_controlPath))
        {
            File.Delete(_controlPath);
        }
    }

    private async Task<string?> TryReadActionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_controlPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(_controlPath);
        var payload = await JsonSerializer.DeserializeAsync<AgentControlRequest>(stream, JsonOptions.Default, cancellationToken);
        if (payload is null || !string.Equals(payload.RequestId, _requestId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return payload.Action;
    }

    private async Task<AgentStatus?> TryReadStatusInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statusPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(_statusPath);
        return await JsonSerializer.DeserializeAsync<AgentStatus>(stream, JsonOptions.Default, cancellationToken);
    }

    private async Task SaveStatusInternalAsync(AgentStatus status, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_statusPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(_statusPath, JsonSerializer.Serialize(status, JsonOptions.Default), cancellationToken);
    }
}

internal sealed class Collector
{
    private readonly CollectorOptions _options;
    private readonly ProbeCredential? _credential;

    public Collector(CollectorOptions options, ProbeCredential? credential)
    {
        _options = options;
        _credential = credential;
    }

    public async Task<IngestRequest> BuildRequestAsync(string? scanMode, AgentControlMonitor? controlMonitor, CancellationToken cancellationToken, Func<ScanProgressSnapshot, Task>? progressCallback)
    {
        var normalizedScanMode = string.Equals(scanMode, "full", StringComparison.OrdinalIgnoreCase)
            ? "full"
            : "simple";
        var simpleScan = normalizedScanMode == "simple";
        var candidates = BuildCandidateIps();
        var totalHosts = candidates.Count;
        var devices = new ConcurrentBag<DeviceInput>();
        var processedHosts = 0;

        if (progressCallback is not null && totalHosts > 0)
        {
            var firstCandidate = candidates[0];
            await progressCallback(new ScanProgressSnapshot(
                totalHosts,
                0,
                firstCandidate.IpAddress.ToString(),
                string.Empty,
                firstCandidate.SubnetCidr,
                "starting"));
        }

        var queue = new ConcurrentQueue<CandidateHost>(candidates);
        var workerCount = Math.Max(1, _options.MaxConcurrentHosts);
        var tasks = Enumerable.Range(0, workerCount).Select(async _ =>
        {
            while (queue.TryDequeue(out var candidate))
            {
                if (controlMonitor is not null)
                {
                    await controlMonitor.WaitIfPausedAsync(cancellationToken);
                    controlMonitor.ThrowIfStopped(cancellationToken);
                }

                if (progressCallback is not null)
                {
                    await progressCallback(new ScanProgressSnapshot(
                        totalHosts,
                        Volatile.Read(ref processedHosts),
                        candidate.IpAddress.ToString(),
                        string.Empty,
                        candidate.SubnetCidr,
                        "probing"));
                }

                var device = await ProbeHostAsync(candidate.IpAddress, simpleScan, cancellationToken);
                var processed = Interlocked.Increment(ref processedHosts);
                if (controlMonitor is not null)
                {
                    await controlMonitor.WaitIfPausedAsync(cancellationToken);
                    controlMonitor.ThrowIfStopped(cancellationToken);
                }

                if (progressCallback is not null)
                {
                    await progressCallback(new ScanProgressSnapshot(
                        totalHosts,
                        processed,
                        candidate.IpAddress.ToString(),
                        device?.HostName ?? device?.DeviceName ?? string.Empty,
                        candidate.SubnetCidr,
                        "scanning"));
                }

                if (device is not null)
                {
                    devices.Add(device);
                }
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        var materializedDevices = devices.ToList();
        var users = BuildUsers(materializedDevices);

        return new IngestRequest
        {
            SourceName = _options.SourceName,
            SourceType = "windows-collector",
            ObservedAtUtc = DateTime.UtcNow,
            WindowStartUtc = DateTime.UtcNow.AddMinutes(-30),
            WindowEndUtc = DateTime.UtcNow,
            Notes = $"Collector Windows ({(simpleScan ? "simple" : "completo")}). Hosts detectados: {materializedDevices.Count}. Identidades: {users.Count}.",
            Devices = materializedDevices.OrderBy(static item => item.IpAddress, StringComparer.OrdinalIgnoreCase).ToList(),
            Users = users
        };
    }

    private List<CandidateHost> BuildCandidateIps()
    {
        var results = new List<CandidateHost>();
        foreach (var cidr in _options.ScanCidrs)
        {
            foreach (var ip in EnumerateHosts(cidr).Take(_options.MaxHostsPerScan))
            {
                results.Add(new CandidateHost(ip, cidr));
            }
        }

        return results
            .GroupBy(static item => item.IpAddress)
            .Select(static group => group.First())
            .OrderBy(static item => ToUInt32(item.IpAddress))
            .ToList();
    }

    private async Task<DeviceInput?> ProbeHostAsync(IPAddress ip, bool simpleScan, CancellationToken cancellationToken)
    {
        var pingMs = await TryPingAsync(ip, cancellationToken);
        var openPorts = simpleScan ? [] : await ProbePortsAsync(ip, cancellationToken);
        var hostName = await ResolveHostNameAsync(ip, cancellationToken);

        var online = pingMs.HasValue || openPorts.Count > 0 || !string.IsNullOrWhiteSpace(hostName);
        if (!online)
        {
            return null;
        }

        var category = simpleScan
            ? InferSimpleCategory(hostName)
            : InferCategory(openPorts, hostName);
        var profile = simpleScan
            ? "user-session"
            : InferProfile(openPorts, category);
        var device = new DeviceInput
        {
            ExternalKey = $"collector:{ip}",
            DeviceName = !string.IsNullOrWhiteSpace(hostName) ? hostName! : $"{category}-{ip}",
            Username = string.Empty,
            Domain = InferDomain(hostName),
            IpAddress = ip.ToString(),
            MacAddress = string.Empty,
            SerialNumber = string.Empty,
            HostName = hostName ?? string.Empty,
            DeviceCategory = category,
            OperatingSystem = InferOperatingSystem(openPorts),
            OperatingSystemVersion = string.Empty,
            Manufacturer = string.Empty,
            Model = string.Empty,
            Processor = string.Empty,
            MemoryGb = null,
            DiskTotalGb = null,
            DiskFreeGb = null,
            LastBootAtUtc = null,
            IsOnline = true,
            DomainJoined = !string.IsNullOrWhiteSpace(hostName) && hostName.Contains('.', StringComparison.Ordinal),
            IsVirtualMachine = null,
            PingMs = pingMs,
            AntivirusStatus = string.Empty,
            AntivirusVersion = string.Empty,
            PatchStatus = string.Empty,
            AgentVersion = "windows-collector",
            OpenPorts = string.Join(",", openPorts),
            SubnetCidr = ResolveSubnet(ip),
            NetworkProfile = profile,
            BuildingExternalId = string.Empty,
            RoomExternalId = string.Empty,
            Status = "observed",
            Notes = string.Empty
        };

        if (_options.ResolveSessions || (!simpleScan && _options.ResolveHardware))
        {
            EnrichDeviceWithWindowsData(device, simpleScan);
        }

        return device;
    }

    private async Task<int?> TryPingAsync(IPAddress ip, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, _options.PingTimeoutMs);
            return reply.Status == IPStatus.Success ? (int)Math.Max(reply.RoundtripTime, 1) : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<int>> ProbePortsAsync(IPAddress ip, CancellationToken cancellationToken)
    {
        var openPorts = new List<int>();
        using var throttle = new SemaphoreSlim(12);
        var tasks = _options.ScanPorts.Select(async port =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(ip, port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(_options.TcpTimeoutMs, cancellationToken));
                if (completed == connectTask && client.Connected)
                {
                    lock (openPorts)
                    {
                        openPorts.Add(port);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                throttle.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
        openPorts.Sort();
        return openPorts;
    }

    private async Task<string?> ResolveHostNameAsync(IPAddress ip, CancellationToken cancellationToken)
    {
        try
        {
            var lookupTask = Dns.GetHostEntryAsync(ip);
            var completed = await Task.WhenAny(lookupTask, Task.Delay(_options.DnsTimeoutMs, cancellationToken));
            if (completed == lookupTask)
            {
                var entry = await lookupTask;
                return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName.Trim();
            }
        }
        catch
        {
        }

        return null;
    }

    private void EnrichDeviceWithWindowsData(DeviceInput device, bool simpleScan)
    {
        if (!IsWindowsCandidate(device))
        {
            return;
        }

        var host = !string.IsNullOrWhiteSpace(device.HostName) ? device.HostName : device.IpAddress;
        var sessions = _options.ResolveSessions ? TryReadInteractiveSessions(host) : [];
        if (sessions.Count > 0)
        {
            device.DetectedSessions = sessions;
            var primarySession = sessions
                .OrderByDescending(static item => item.Status == "active")
                .ThenBy(static item => item.Username, StringComparer.OrdinalIgnoreCase)
                .First();

            device.Username = primarySession.Username;
            device.Status = primarySession.Status;
            device.Notes = string.Join(" || ", sessions.Select(static item => item.Details));
        }

        if (!simpleScan && _options.ResolveHardware)
        {
            TryReadHardware(device, host);
        }
    }

    private List<InteractiveSession> TryReadInteractiveSessions(string host)
    {
        var quserSessions = TryRunQuser(host);
        if (quserSessions.Count > 0)
        {
            return quserSessions;
        }

        if (!_options.ResolveHardware)
        {
            return [];
        }

        try
        {
            var scope = CreateManagementScope(host);
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT UserName FROM Win32_ComputerSystem"));
            foreach (ManagementObject item in searcher.Get())
            {
                var raw = item["UserName"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var username = raw.Contains('\\', StringComparison.Ordinal)
                    ? raw[(raw.LastIndexOf('\\') + 1)..]
                    : raw;

                return [new InteractiveSession(username, "active", $"WMI: {raw}", host)];
            }
        }
        catch
        {
        }

        return [];
    }

    private List<InteractiveSession> TryRunQuser(string host)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "quser",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add($"/server:{host}");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [];
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var waitTimeoutMs = Math.Max(500, _options.QuserTimeoutMs);
            var finished = process.WaitForExit(waitTimeoutMs);

            if (!finished)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return [];
            }

            Task.WaitAll([outputTask, errorTask], waitTimeoutMs);
            var output = outputTask.IsCompletedSuccessfully ? outputTask.Result : string.Empty;
            var error = errorTask.IsCompletedSuccessfully ? errorTask.Result : string.Empty;
            if (string.IsNullOrWhiteSpace(output))
            {
                return [];
            }

            if (output.Contains("Error 0x", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("RPC", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("RPC", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            var rows = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Skip(1);

            var sessions = new List<InteractiveSession>();

            foreach (var row in rows)
            {
                var cleaned = System.Text.RegularExpressions.Regex.Replace(row, "\\s+", " ")
                    .Replace(">", string.Empty, StringComparison.Ordinal)
                    .Trim();

                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    continue;
                }

                var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0)
                {
                    continue;
                }

                var username = parts[0].Trim();
                if (string.IsNullOrWhiteSpace(username) || username.Equals("USERNAME", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var state = cleaned.Contains(" Active ", StringComparison.OrdinalIgnoreCase) || cleaned.EndsWith(" Active", StringComparison.OrdinalIgnoreCase)
                    ? "active"
                    : cleaned.Contains(" Disc", StringComparison.OrdinalIgnoreCase) || cleaned.Contains(" Desconectado", StringComparison.OrdinalIgnoreCase)
                        ? "disconnected"
                        : "observed";

                sessions.Add(new InteractiveSession(username, state, cleaned, host));
            }

            return sessions;
        }
        catch
        {
        }

        return [];
    }

    private static string InferSimpleCategory(string? hostName)
    {
        if (!string.IsNullOrWhiteSpace(hostName) &&
            (hostName.Contains("print", StringComparison.OrdinalIgnoreCase) ||
             hostName.Contains("zebra", StringComparison.OrdinalIgnoreCase) ||
             hostName.Contains("hp", StringComparison.OrdinalIgnoreCase)))
        {
            return "printer";
        }

        return "pc";
    }

    private void TryReadHardware(DeviceInput device, string host)
    {
        try
        {
            var scope = CreateManagementScope(host);

            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Manufacturer, Model, TotalPhysicalMemory, UserName FROM Win32_ComputerSystem")))
            {
                foreach (ManagementObject item in searcher.Get())
                {
                    device.Manufacturer = item["Manufacturer"]?.ToString()?.Trim() ?? device.Manufacturer;
                    device.Model = item["Model"]?.ToString()?.Trim() ?? device.Model;
                    if (double.TryParse(item["TotalPhysicalMemory"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var memoryBytes))
                    {
                        device.MemoryGb = Math.Round(memoryBytes / 1024d / 1024d / 1024d, 1);
                    }

                    var rawUser = item["UserName"]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(rawUser) && string.IsNullOrWhiteSpace(device.Username))
                    {
                        device.Username = rawUser.Contains('\\', StringComparison.Ordinal)
                            ? rawUser[(rawUser.LastIndexOf('\\') + 1)..]
                            : rawUser;
                    }
                }
            }

            using (var osSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Caption, Version, LastBootUpTime FROM Win32_OperatingSystem")))
            {
                foreach (ManagementObject item in osSearcher.Get())
                {
                    device.OperatingSystem = item["Caption"]?.ToString()?.Trim() ?? device.OperatingSystem;
                    device.OperatingSystemVersion = item["Version"]?.ToString()?.Trim() ?? device.OperatingSystemVersion;
                    var lastBootRaw = item["LastBootUpTime"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(lastBootRaw))
                    {
                        device.LastBootAtUtc = ManagementDateTimeConverter.ToDateTime(lastBootRaw).ToUniversalTime();
                    }
                }
            }

            using (var diskSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Size, FreeSpace FROM Win32_LogicalDisk WHERE DeviceID = 'C:'")))
            {
                foreach (ManagementObject item in diskSearcher.Get())
                {
                    if (double.TryParse(item["Size"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var totalBytes))
                    {
                        device.DiskTotalGb = Math.Round(totalBytes / 1024d / 1024d / 1024d, 1);
                    }

                    if (double.TryParse(item["FreeSpace"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var freeBytes))
                    {
                        device.DiskFreeGb = Math.Round(freeBytes / 1024d / 1024d / 1024d, 1);
                    }
                }
            }
        }
        catch
        {
        }
    }

    private ManagementScope CreateManagementScope(string host)
    {
        var connectionOptions = new ConnectionOptions();
        if (_credential is not null)
        {
            connectionOptions.Username = _credential.QualifiedUsername;
            connectionOptions.Password = _credential.Password;
            connectionOptions.EnablePrivileges = true;
            connectionOptions.Impersonation = ImpersonationLevel.Impersonate;
            connectionOptions.Authentication = AuthenticationLevel.PacketPrivacy;
        }

        var scope = new ManagementScope($@"\\{host}\root\cimv2", connectionOptions);
        scope.Connect();
        return scope;
    }

    private string ResolveSubnet(IPAddress ip)
    {
        foreach (var cidr in _options.ScanCidrs)
        {
            if (IsIpInCidr(ip, cidr))
            {
                return cidr;
            }
        }

        var bytes = ip.GetAddressBytes();
        return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
    }

    private static bool IsWindowsCandidate(DeviceInput device)
    {
        return device.DeviceCategory.Equals("pc", StringComparison.OrdinalIgnoreCase)
            || device.DeviceCategory.Equals("servidor", StringComparison.OrdinalIgnoreCase)
            || device.OpenPorts.Contains("445", StringComparison.Ordinal)
            || device.OpenPorts.Contains("3389", StringComparison.Ordinal);
    }

    private static string ResolveIdentityKey(DeviceInput device)
    {
        if (!string.IsNullOrWhiteSpace(device.Username))
        {
            return device.Username;
        }

        if (!string.IsNullOrWhiteSpace(device.HostName))
        {
            return device.HostName;
        }

        return device.IpAddress;
    }

    private static string ResolveUserStatus(DeviceInput device)
    {
        return device.Status switch
        {
            "active" => "active",
            "disconnected" => "observed",
            _ => device.IsOnline == true ? "active" : "observed"
        };
    }

    private static List<UserInput> BuildUsers(IReadOnlyList<DeviceInput> devices)
    {
        var sessions = devices
            .SelectMany(device => device.DetectedSessions.Select(session => new { Device = device, Session = session }))
            .ToList();

        if (sessions.Count > 0)
        {
            return sessions
                .GroupBy(item => item.Session.Username, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var activeSession = group
                        .OrderByDescending(item => item.Session.Status == "active")
                        .ThenBy(item => item.Session.Username, StringComparer.OrdinalIgnoreCase)
                        .First();

                    return new UserInput
                    {
                        ExternalKey = $"network-user:{activeSession.Session.Host}:{activeSession.Session.Username}",
                        Username = activeSession.Session.Username,
                        DisplayName = activeSession.Session.Username,
                        DeviceCount = group.Select(item => item.Device.IpAddress).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                        Status = activeSession.Session.Status,
                        Notes = string.Join(" || ", group.Select(item => item.Session.Details).Distinct(StringComparer.OrdinalIgnoreCase))
                    };
                })
                .OrderBy(item => item.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return devices
            .GroupBy(static device => ResolveIdentityKey(device), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var sample = group.First();
                return new UserInput
                {
                    ExternalKey = $"network:{ResolveIdentityKey(sample)}",
                    Username = ResolveIdentityKey(sample),
                    DisplayName = string.IsNullOrWhiteSpace(sample.Username) ? sample.DeviceName : sample.Username,
                    DeviceCount = group.Count(),
                    Status = ResolveUserStatus(sample),
                    Notes = $"subred {sample.SubnetCidr} | estado {sample.Status} | puertos {sample.OpenPorts}"
                };
            })
            .ToList();
    }

    private static string InferOperatingSystem(IReadOnlyCollection<int> ports)
    {
        if (ports.Contains(445) || ports.Contains(3389) || ports.Contains(135))
        {
            return "Windows";
        }

        if (ports.Contains(22))
        {
            return "Linux/Unix";
        }

        if (ports.Contains(9100) || ports.Contains(631) || ports.Contains(515))
        {
            return "Printer";
        }

        return "Unknown";
    }

    private static string InferCategory(IReadOnlyCollection<int> ports, string? hostName)
    {
        var host = hostName ?? string.Empty;
        if (ports.Contains(9100) || ports.Contains(631) || ports.Contains(515) || host.Contains("print", StringComparison.OrdinalIgnoreCase))
        {
            return "impresora";
        }

        if (ports.Contains(445) || ports.Contains(3389) || ports.Contains(135))
        {
            return "pc";
        }

        if (ports.Contains(22))
        {
            return "servidor";
        }

        return "other";
    }

    private static string InferProfile(IReadOnlyCollection<int> ports, string category)
    {
        if (category == "impresora")
        {
            return "printer";
        }

        if (category == "pc")
        {
            return "workstation";
        }

        if (category == "servidor")
        {
            return "server";
        }

        if (ports.Contains(161) || ports.Contains(53))
        {
            return "infrastructure";
        }

        return "network";
    }

    private static string InferDomain(string? hostName)
    {
        if (string.IsNullOrWhiteSpace(hostName) || !hostName.Contains('.', StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var parts = hostName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 1 ? string.Join('.', parts.Skip(1)) : string.Empty;
    }

    private static bool IsIpInCidr(IPAddress ip, string cidr)
    {
        var parts = cidr.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var baseIp) || !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return (ToUInt32(ip) & mask) == (ToUInt32(baseIp) & mask);
    }

    private static IEnumerable<IPAddress> EnumerateHosts(string cidr)
    {
        var parts = cidr.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var baseIp) || !int.TryParse(parts[1], out var prefixLength))
        {
            yield break;
        }

        var baseAddress = ToUInt32(baseIp);
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var network = baseAddress & mask;
        var broadcast = network | ~mask;

        if (broadcast <= network + 1)
        {
            yield break;
        }

        for (var value = network + 1; value < broadcast; value++)
        {
            yield return FromUInt32(value);
        }
    }

    private static uint ToUInt32(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return BitConverter.ToUInt32(bytes, 0);
    }

    private static IPAddress FromUInt32(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return new IPAddress(bytes);
    }
}

internal sealed class CollectorOptions
{
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string SourceName { get; set; } = "Collector Windows SSMSO";
    public string AgentId { get; set; } = Environment.MachineName;
    public string SharedPath { get; set; } = string.Empty;
    public string ConfigurationDirectory { get; set; } = string.Empty;
    public bool WatchMode { get; set; }
    public int PollIntervalSeconds { get; set; } = 5;
    public int ProgressUpdateSeconds { get; set; } = 2;
    public int MaxScanMinutes { get; set; } = 8;
    public string Domain { get; set; } = "SSMSO";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool PromptForCredential { get; set; } = true;
    public bool ResolveSessions { get; set; } = true;
    public bool ResolveHardware { get; set; } = true;
    public List<string> ScanCidrs { get; set; } = [];
    public List<int> ScanPorts { get; set; } = [];
    public int MaxHostsPerScan { get; set; } = 4096;
    public int MaxConcurrentHosts { get; set; } = 64;
    public int PingTimeoutMs { get; set; } = 400;
    public int TcpTimeoutMs { get; set; } = 350;
    public int DnsTimeoutMs { get; set; } = 1200;
    public int QuserTimeoutMs { get; set; } = 2500;
}

internal sealed record ProbeCredential(string Username, string Password, string Domain)
{
    public string QualifiedUsername =>
        Username.Contains('\\', StringComparison.Ordinal) || Username.Contains('@', StringComparison.Ordinal)
            ? Username
            : $"{Domain}\\{Username}";
}

internal sealed record CandidateHost(IPAddress IpAddress, string SubnetCidr);

internal sealed record InteractiveSession(string Username, string Status, string Details, string Host);

internal sealed class IngestRequest
{
    public string SourceName { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public DateTime? ObservedAtUtc { get; set; }
    public DateTime? WindowStartUtc { get; set; }
    public DateTime? WindowEndUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
    public IReadOnlyList<DeviceInput> Devices { get; set; } = [];
    public IReadOnlyList<UserInput> Users { get; set; } = [];
}

internal sealed class DeviceInput
{
    public string ExternalKey { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string DeviceCategory { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string OperatingSystemVersion { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Processor { get; set; } = string.Empty;
    public double? MemoryGb { get; set; }
    public double? DiskTotalGb { get; set; }
    public double? DiskFreeGb { get; set; }
    public DateTime? LastBootAtUtc { get; set; }
    public bool? IsOnline { get; set; }
    public bool? DomainJoined { get; set; }
    public bool? IsVirtualMachine { get; set; }
    public int? PingMs { get; set; }
    public string AntivirusStatus { get; set; } = string.Empty;
    public string AntivirusVersion { get; set; } = string.Empty;
    public string PatchStatus { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string OpenPorts { get; set; } = string.Empty;
    public string SubnetCidr { get; set; } = string.Empty;
    public string NetworkProfile { get; set; } = string.Empty;
    public string BuildingExternalId { get; set; } = string.Empty;
    public string RoomExternalId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    [JsonIgnore]
    public List<InteractiveSession> DetectedSessions { get; set; } = [];
}

internal sealed class UserInput
{
    public string ExternalKey { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int? DeviceCount { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

internal sealed class IngestResult
{
    public int SnapshotId { get; set; }
    public string OverallRiskLevel { get; set; } = string.Empty;
    public int OverallRiskScore { get; set; }
    public string Notes { get; set; } = string.Empty;
}

internal sealed class AgentRequest
{
    public string RequestId { get; set; } = string.Empty;
    public DateTime? RequestedAtUtc { get; set; }
    public string RequestedByUsername { get; set; } = string.Empty;
    public bool ResolveInteractiveSessions { get; set; } = true;
    public string ScanMode { get; set; } = "simple";
}

internal sealed class AgentControlRequest
{
    public string RequestId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string RequestedByUsername { get; set; } = string.Empty;
    public DateTime? RequestedAtUtc { get; set; }
}

internal sealed class AgentStatus
{
    public string RequestId { get; set; } = string.Empty;
    public string State { get; set; } = "idle";
    public string AgentId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int? SnapshotId { get; set; }
    public DateTime? RequestedAtUtc { get; set; }
    public string RequestedByUsername { get; set; } = string.Empty;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public int? TotalHosts { get; set; }
    public int? ProcessedHosts { get; set; }
    public string CurrentIpAddress { get; set; } = string.Empty;
    public string CurrentHostName { get; set; } = string.Empty;
    public string CurrentSubnetCidr { get; set; } = string.Empty;
    public string CurrentStage { get; set; } = string.Empty;
}

internal sealed class AgentHeartbeat
{
    public string AgentId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public DateTime HeartbeatAtUtc { get; set; }
    public string Version { get; set; } = string.Empty;
    public string Mode { get; set; } = "watch";
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
}
