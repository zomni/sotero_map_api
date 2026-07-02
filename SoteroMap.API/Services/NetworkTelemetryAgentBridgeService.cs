using System.Text.Json;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Services;

public class NetworkTelemetryAgentBridgeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public NetworkTelemetryAgentBridgeService(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public bool UseAgentMode()
        => string.Equals(
            _configuration["NetworkTelemetrySettings:ExecutionMode"] ?? "agent",
            "agent",
            StringComparison.OrdinalIgnoreCase);

    public string GetSharedPath()
    {
        var configured = _configuration["NetworkTelemetrySettings:AgentSharedPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured, _environment.ContentRootPath);
        }

        return Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "..", "runtime", "network-telemetry-agent"));
    }

    public string GetRequestPath() => Path.Combine(GetSharedPath(), "scan-request.json");

    public string GetStatusPath() => Path.Combine(GetSharedPath(), "scan-status.json");

    public string GetHeartbeatPath() => Path.Combine(GetSharedPath(), "agent-heartbeat.json");

    public string GetControlPath() => Path.Combine(GetSharedPath(), "scan-control.json");

    public async Task<NetworkTelemetryAgentStatusViewModel> QueueScanAsync(string requestedByUsername, NetworkTelemetryLiveScanRequest? request, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(GetSharedPath());
        TryDeleteControl();

        var requestPayload = new NetworkTelemetryAgentRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            RequestedAtUtc = DateTime.UtcNow,
            RequestedByUsername = string.IsNullOrWhiteSpace(requestedByUsername) ? "system" : requestedByUsername.Trim(),
            ResolveInteractiveSessions = request?.ResolveInteractiveSessions ?? true,
            ScanMode = NormalizeScanMode(request?.ScanMode),
            TriggerType = NormalizeTriggerType(request?.TriggerType)
        };

        await File.WriteAllTextAsync(GetRequestPath(), JsonSerializer.Serialize(requestPayload, JsonOptions), cancellationToken);

        var statusPayload = new NetworkTelemetryAgentStatus
        {
            RequestId = requestPayload.RequestId,
            State = "pending",
            Message = $"Solicitud de escaneo creada por {requestPayload.RequestedByUsername}. Esperando al agente Windows.",
            RequestedAtUtc = requestPayload.RequestedAtUtc,
            RequestedByUsername = requestPayload.RequestedByUsername,
            TriggerType = requestPayload.TriggerType,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await File.WriteAllTextAsync(GetStatusPath(), JsonSerializer.Serialize(statusPayload, JsonOptions), cancellationToken);
        return MapStatus(statusPayload);
    }

    public async Task<NetworkTelemetryAgentStatusViewModel> SendControlAsync(string requestedByUsername, string action, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(GetSharedPath());

        var normalizedAction = NormalizeControlAction(action);
        var current = await GetRawStatusAsync(cancellationToken) ?? new NetworkTelemetryAgentStatus
        {
            State = "idle",
            Message = "Sin solicitudes recientes para el agente Windows."
        };

        var payload = new NetworkTelemetryAgentControl
        {
            RequestId = current.RequestId,
            Action = normalizedAction,
            RequestedByUsername = string.IsNullOrWhiteSpace(requestedByUsername) ? "system" : requestedByUsername.Trim(),
            RequestedAtUtc = DateTime.UtcNow
        };

        await File.WriteAllTextAsync(GetControlPath(), JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);

        if (string.Equals(normalizedAction, "pause", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.State, "running", StringComparison.OrdinalIgnoreCase))
        {
            current.State = "paused";
            current.Message = $"Escaneo pausado por {payload.RequestedByUsername}.";
            current.UpdatedAtUtc = DateTime.UtcNow;
            await SaveStatusAsync(current, cancellationToken);
        }
        else if (string.Equals(normalizedAction, "resume", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(current.State, "paused", StringComparison.OrdinalIgnoreCase))
        {
            current.State = "running";
            current.Message = $"Escaneo reanudado por {payload.RequestedByUsername}.";
            current.UpdatedAtUtc = DateTime.UtcNow;
            await SaveStatusAsync(current, cancellationToken);
        }
        else if (string.Equals(normalizedAction, "stop", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(current.State, "pending", StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteRequest();
                current.State = "failed";
                current.Error = "scan-stopped-before-start";
                current.Message = $"Solicitud detenida por {payload.RequestedByUsername} antes de iniciar.";
                current.CompletedAtUtc = DateTime.UtcNow;
                current.UpdatedAtUtc = DateTime.UtcNow;
                await SaveStatusAsync(current, cancellationToken);
            }
            else
            {
                current.State = "stopping";
                current.Message = $"Deteniendo escaneo por solicitud de {payload.RequestedByUsername}.";
                current.UpdatedAtUtc = DateTime.UtcNow;
                await SaveStatusAsync(current, cancellationToken);
            }
        }

        return MapStatus(current);
    }

    public async Task<NetworkTelemetryAgentRequest?> TryReadPendingRequestAsync(CancellationToken cancellationToken = default)
    {
        var requestPath = GetRequestPath();
        if (!File.Exists(requestPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(requestPath);
        return await JsonSerializer.DeserializeAsync<NetworkTelemetryAgentRequest>(stream, JsonOptions, cancellationToken);
    }

    public async Task MarkRunningAsync(string requestId, string agentId, CancellationToken cancellationToken = default)
    {
        var current = await GetRawStatusAsync(cancellationToken) ?? new NetworkTelemetryAgentStatus();
        current.RequestId = requestId;
        current.State = "running";
        current.AgentId = agentId;
        current.Message = $"Agente {agentId} ejecutando escaneo.";
        current.StartedAtUtc ??= DateTime.UtcNow;
        current.UpdatedAtUtc = DateTime.UtcNow;
        await SaveStatusAsync(current, cancellationToken);
    }

    public async Task MarkCompletedAsync(string requestId, string agentId, int? snapshotId, string? message, CancellationToken cancellationToken = default)
    {
        var current = await GetRawStatusAsync(cancellationToken) ?? new NetworkTelemetryAgentStatus();
        current.RequestId = requestId;
        current.State = "completed";
        current.AgentId = agentId;
        current.SnapshotId = snapshotId;
        current.Message = string.IsNullOrWhiteSpace(message) ? "Escaneo completado." : message.Trim();
        current.CompletedAtUtc = DateTime.UtcNow;
        current.UpdatedAtUtc = DateTime.UtcNow;
        await SaveStatusAsync(current, cancellationToken);
        TryDeleteRequest();
    }

    public async Task MarkFailedAsync(string requestId, string agentId, string? error, CancellationToken cancellationToken = default)
    {
        var current = await GetRawStatusAsync(cancellationToken) ?? new NetworkTelemetryAgentStatus();
        current.RequestId = requestId;
        current.State = "failed";
        current.AgentId = agentId;
        current.Error = string.IsNullOrWhiteSpace(error) ? "Error no especificado." : error.Trim();
        current.Message = "El agente Windows no pudo completar el escaneo.";
        current.CompletedAtUtc = DateTime.UtcNow;
        current.UpdatedAtUtc = DateTime.UtcNow;
        await SaveStatusAsync(current, cancellationToken);
    }

    public async Task<NetworkTelemetryAgentStatusViewModel> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var current = await GetRawStatusAsync(cancellationToken);
        var heartbeat = await GetHeartbeatAsync(cancellationToken);
        var nowUtc = DateTime.UtcNow;
        var heartbeatTimeout = TimeSpan.FromSeconds(GetHeartbeatTimeoutSeconds());
        var mapped = current is null
            ? new NetworkTelemetryAgentStatusViewModel
            {
                State = "idle",
                Message = "Sin solicitudes recientes para el agente Windows."
            }
            : MapStatus(current);

        if (string.IsNullOrWhiteSpace(mapped.TriggerType))
        {
            var request = await TryReadPendingRequestAsync(cancellationToken);
            if (request is not null)
            {
                mapped.TriggerType = request.TriggerType;
            }
        }

        mapped.LastHeartbeatAtUtc = heartbeat?.HeartbeatAtUtc;
        var heartbeatIsFresh = heartbeat is not null && heartbeat.HeartbeatAtUtc >= nowUtc.Subtract(heartbeatTimeout);
        var stateLooksActive = mapped.State is "pending" or "running" or "paused" or "stopping";
        var recentProgress = mapped.UpdatedAtUtc.HasValue &&
                             mapped.UpdatedAtUtc.Value >= nowUtc.Subtract(TimeSpan.FromSeconds(Math.Max(GetHeartbeatTimeoutSeconds() * 2, 90)));

        mapped.IsConnected = heartbeatIsFresh || (stateLooksActive && recentProgress);
        mapped.AgentId = !string.IsNullOrWhiteSpace(mapped.AgentId)
            ? mapped.AgentId
            : (heartbeat?.AgentId ?? string.Empty);

        if (!heartbeatIsFresh && stateLooksActive && recentProgress)
        {
            mapped.Message = "Escaneo en curso con avance reciente. El heartbeat del agente esta atrasado, pero el proceso sigue reportando progreso.";
        }
        else if (!mapped.IsConnected)
        {
            mapped.Message = "Agente Windows desconectado o sin latido reciente.";
        }

        return mapped;
    }

    private int GetHeartbeatTimeoutSeconds()
    {
        var raw = _configuration["NetworkTelemetrySettings:AgentHeartbeatTimeoutSeconds"];
        return int.TryParse(raw, out var value) && value > 5
            ? value
            : 30;
    }

    private async Task<NetworkTelemetryAgentStatus?> GetRawStatusAsync(CancellationToken cancellationToken)
    {
        var statusPath = GetStatusPath();
        if (!File.Exists(statusPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(statusPath);
        return await JsonSerializer.DeserializeAsync<NetworkTelemetryAgentStatus>(stream, JsonOptions, cancellationToken);
    }

    private async Task<NetworkTelemetryAgentHeartbeat?> GetHeartbeatAsync(CancellationToken cancellationToken)
    {
        var heartbeatPath = GetHeartbeatPath();
        if (!File.Exists(heartbeatPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(heartbeatPath);
        return await JsonSerializer.DeserializeAsync<NetworkTelemetryAgentHeartbeat>(stream, JsonOptions, cancellationToken);
    }

    private async Task SaveStatusAsync(NetworkTelemetryAgentStatus status, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(GetSharedPath());
        await File.WriteAllTextAsync(GetStatusPath(), JsonSerializer.Serialize(status, JsonOptions), cancellationToken);
    }

    private void TryDeleteRequest()
    {
        var requestPath = GetRequestPath();
        if (File.Exists(requestPath))
        {
            File.Delete(requestPath);
        }
    }

    private void TryDeleteControl()
    {
        var controlPath = GetControlPath();
        if (File.Exists(controlPath))
        {
            File.Delete(controlPath);
        }
    }

    private static string NormalizeScanMode(string? scanMode)
        => string.Equals(scanMode, "full", StringComparison.OrdinalIgnoreCase)
            ? "full"
            : "simple";

    private static string NormalizeTriggerType(string? triggerType)
        => triggerType?.Trim().ToLowerInvariant() switch
        {
            "scheduled" => "scheduled",
            "automatic" => "automatic",
            _ => "manual"
        };

    private static string NormalizeControlAction(string? action)
        => action?.Trim().ToLowerInvariant() switch
        {
            "pause" => "pause",
            "resume" => "resume",
            "stop" => "stop",
            _ => "pause"
        };

    private static NetworkTelemetryAgentStatusViewModel MapStatus(NetworkTelemetryAgentStatus status)
        => new()
        {
            RequestId = status.RequestId,
            State = status.State,
            Message = status.Message,
            AgentId = status.AgentId,
            SnapshotId = status.SnapshotId,
            Error = status.Error,
            RequestedAtUtc = status.RequestedAtUtc,
            StartedAtUtc = status.StartedAtUtc,
            CompletedAtUtc = status.CompletedAtUtc,
            UpdatedAtUtc = status.UpdatedAtUtc,
            RequestedByUsername = status.RequestedByUsername,
            TriggerType = status.TriggerType,
            TotalHosts = status.TotalHosts,
            ProcessedHosts = status.ProcessedHosts,
            CurrentIpAddress = status.CurrentIpAddress,
            CurrentHostName = status.CurrentHostName,
            CurrentSubnetCidr = status.CurrentSubnetCidr,
            CurrentStage = status.CurrentStage
        };
}

public class NetworkTelemetryAgentRequest
{
    public string RequestId { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; }
    public string RequestedByUsername { get; set; } = string.Empty;
    public bool ResolveInteractiveSessions { get; set; } = true;
    public string ScanMode { get; set; } = "simple";
    public string TriggerType { get; set; } = "manual";
}

public class NetworkTelemetryAgentStatus
{
    public string RequestId { get; set; } = string.Empty;
    public string State { get; set; } = "idle";
    public string Message { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public int? SnapshotId { get; set; }
    public string Error { get; set; } = string.Empty;
    public DateTime? RequestedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string RequestedByUsername { get; set; } = string.Empty;
    public string TriggerType { get; set; } = string.Empty;
    public int? TotalHosts { get; set; }
    public int? ProcessedHosts { get; set; }
    public string CurrentIpAddress { get; set; } = string.Empty;
    public string CurrentHostName { get; set; } = string.Empty;
    public string CurrentSubnetCidr { get; set; } = string.Empty;
    public string CurrentStage { get; set; } = string.Empty;
}

public class NetworkTelemetryAgentStatusViewModel
{
    public string RequestId { get; set; } = string.Empty;
    public string State { get; set; } = "idle";
    public string Message { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public int? SnapshotId { get; set; }
    public string Error { get; set; } = string.Empty;
    public DateTime? RequestedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string RequestedByUsername { get; set; } = string.Empty;
    public string TriggerType { get; set; } = string.Empty;
    public DateTime? LastHeartbeatAtUtc { get; set; }
    public bool IsConnected { get; set; }
    public int? TotalHosts { get; set; }
    public int? ProcessedHosts { get; set; }
    public string CurrentIpAddress { get; set; } = string.Empty;
    public string CurrentHostName { get; set; } = string.Empty;
    public string CurrentSubnetCidr { get; set; } = string.Empty;
    public string CurrentStage { get; set; } = string.Empty;
}

public class NetworkTelemetryAgentControl
{
    public string RequestId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string RequestedByUsername { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; }
}

public class NetworkTelemetryAgentHeartbeat
{
    public string AgentId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public DateTime HeartbeatAtUtc { get; set; }
    public string Version { get; set; } = string.Empty;
    public string Mode { get; set; } = "watch";
}
