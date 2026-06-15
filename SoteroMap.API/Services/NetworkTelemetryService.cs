using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Models;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Services;

public class NetworkTelemetryService
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly AuditLogService _auditLogService;

    public NetworkTelemetryService(
        AppDbContext context,
        IConfiguration configuration,
        AuditLogService auditLogService)
    {
        _context = context;
        _configuration = configuration;
        _auditLogService = auditLogService;
    }

    public bool IsEnabled()
        => _configuration.GetValue<bool?>("NetworkTelemetrySettings:Enabled") ?? true;

    public int FreshnessMinutes()
        => _configuration.GetValue<int?>("NetworkTelemetrySettings:FreshnessMinutes") ?? 30;

    public int RetentionDays()
        => _configuration.GetValue<int?>("NetworkTelemetrySettings:RetentionDays") ?? 90;

    public int MaxSnapshots()
        => _configuration.GetValue<int?>("NetworkTelemetrySettings:MaxSnapshots") ?? 100;

    public string? IngestApiKey()
        => _configuration["NetworkTelemetrySettings:IngestApiKey"];

    public async Task<NetworkTelemetryDashboardViewModel> GetDashboardAsync(int take = 10, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 50);

        var snapshots = await _context.NetworkTelemetrySnapshots
            .AsNoTracking()
            .OrderByDescending(snapshot => snapshot.ObservedAtUtc)
            .ThenByDescending(snapshot => snapshot.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        var latest = snapshots.FirstOrDefault();
        var enabled = IsEnabled();
        var nowUtc = DateTime.UtcNow;
        var freshnessWindow = TimeSpan.FromMinutes(FreshnessMinutes());
        var isFresh = latest is not null && (nowUtc - latest.ObservedAtUtc) <= freshnessWindow;

        var healthLabel = !enabled
            ? "Deshabilitado"
            : latest is null
                ? "Sin datos"
                : isFresh
                    ? "Activo"
                    : "Desactualizado";

        var healthTone = !enabled
            ? "secondary"
            : latest is null
                ? "warning"
                : isFresh
                    ? "success"
                    : "warning";

        var topRiskObservations = latest is null
            ? []
            : await GetTopRiskObservationsAsync(latest.Id, 10, cancellationToken);

        return new NetworkTelemetryDashboardViewModel
        {
            Enabled = enabled,
            HasData = latest is not null,
            IsFresh = isFresh,
            HealthLabel = healthLabel,
            HealthTone = healthTone,
            LatestSourceName = latest?.SourceName ?? string.Empty,
            LatestSourceType = latest?.SourceType ?? string.Empty,
            LatestRiskLevel = latest?.RiskLevel ?? string.Empty,
            LatestStatus = latest?.Status ?? string.Empty,
            Notes = latest?.Notes ?? string.Empty,
            LatestRiskScore = latest?.RiskScore ?? 0,
            TotalSnapshots = snapshots.Count,
            LatestDeviceCount = latest?.DeviceCount ?? 0,
            LatestConnectedUserCount = latest?.ConnectedUserCount ?? 0,
            LatestHighRiskDeviceCount = latest?.HighRiskDeviceCount ?? 0,
            LatestMediumRiskDeviceCount = latest?.MediumRiskDeviceCount ?? 0,
            LatestLowRiskDeviceCount = latest?.LowRiskDeviceCount ?? 0,
            LatestObservedAtUtc = latest?.ObservedAtUtc,
            LatestWindowStartUtc = latest?.WindowStartUtc,
            LatestWindowEndUtc = latest?.WindowEndUtc,
            GeneratedAtUtc = nowUtc,
            RecentSnapshots = snapshots.Select(MapSnapshot).ToList(),
            TopRiskObservations = topRiskObservations
        };
    }

    public async Task<IReadOnlyList<NetworkTelemetrySnapshotViewModel>> GetRecentSnapshotsAsync(int take = 10, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 50);

        var snapshots = await _context.NetworkTelemetrySnapshots
            .AsNoTracking()
            .OrderByDescending(snapshot => snapshot.ObservedAtUtc)
            .ThenByDescending(snapshot => snapshot.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        return snapshots.Select(MapSnapshot).ToList();
    }

    public async Task<NetworkTelemetryIngestResultViewModel> IngestAsync(
        NetworkTelemetryIngestRequest request,
        string createdByUsername,
        CancellationToken cancellationToken = default)
    {
        var observedAtUtc = request.ObservedAtUtc ?? DateTime.UtcNow;
        var sourceName = string.IsNullOrWhiteSpace(request.SourceName) ? "desconocido" : request.SourceName.Trim();
        var sourceType = string.IsNullOrWhiteSpace(request.SourceType) ? "collector" : request.SourceType.Trim();

        var deviceInputs = request.Devices ?? [];
        var userInputs = request.Users ?? [];
        var normalizedDevices = deviceInputs
            .Select(input => NormalizeDeviceInput(input))
            .ToList();
        var normalizedUsers = userInputs
            .Select(input => NormalizeUserInput(input))
            .ToList();

        var importedItems = await _context.ImportedInventoryItems
            .AsNoTracking()
            .Select(item => new InventoryMatchRecord(
                item.Id,
                Normalize(item.SerialNumber),
                Normalize(item.IpAddress),
                Normalize(item.MacAddress),
                Normalize(item.ResponsibleUser),
                Normalize(item.AssignedBuildingExternalId),
                Normalize(item.AssignedRoomExternalId)))
            .ToListAsync(cancellationToken);

        var syncedEquipments = await _context.SyncedEquipments
            .AsNoTracking()
            .Select(item => new SyncedEquipmentMatchRecord(
                item.Id,
                Normalize(item.SerialNumber),
                Normalize(item.IpAddress),
                Normalize(item.MacAddress),
                Normalize(item.Name),
                Normalize(item.BuildingExternalId),
                Normalize(item.RoomExternalId),
                item.SyncedBuildingId,
                item.SyncedRoomId))
            .ToListAsync(cancellationToken);

        var authUsers = await _context.AuthUsers
            .AsNoTracking()
            .Select(user => new AuthUserMatchRecord(user.Id, Normalize(user.Username), Normalize(user.NormalizedUsername), BackendAuthService.NormalizeRole(user.Role)))
            .ToListAsync(cancellationToken);

        var duplicateIpSet = normalizedDevices
            .Where(device => !string.IsNullOrWhiteSpace(device.IpAddress))
            .GroupBy(device => Normalize(device.IpAddress))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var duplicateMacSet = normalizedDevices
            .Where(device => IsValidMac(device.MacAddress))
            .GroupBy(device => Normalize(device.MacAddress))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var deviceObservations = new List<ObservationResult>();
        foreach (var device in normalizedDevices)
        {
            deviceObservations.Add(ScoreDevice(
                device,
                observedAtUtc,
                importedItems,
                syncedEquipments,
                authUsers,
                duplicateIpSet,
                duplicateMacSet));
        }

        var userObservations = new List<ObservationResult>();
        foreach (var user in normalizedUsers)
        {
            var linkedDevices = normalizedDevices.Count(device =>
                !string.IsNullOrWhiteSpace(user.Username) &&
                string.Equals(Normalize(device.Username), Normalize(user.Username), StringComparison.OrdinalIgnoreCase));

            userObservations.Add(ScoreUser(user, observedAtUtc, authUsers, linkedDevices));
        }

        var deviceRiskScore = deviceObservations.Count == 0 ? 0 : (int)Math.Round(deviceObservations.Average(observation => observation.RiskScore));
        var userRiskScore = userObservations.Count == 0 ? 0 : (int)Math.Round(userObservations.Average(observation => observation.RiskScore));
        var overallRiskScore = deviceObservations.Count == 0 && userObservations.Count == 0
            ? 0
            : (int)Math.Round((deviceRiskScore + userRiskScore) / 2.0);
        var overallRiskLevel = ToRiskLevel(overallRiskScore);

        var snapshot = new NetworkTelemetrySnapshot
        {
            SourceName = sourceName,
            SourceType = sourceType,
            Status = "received",
            RiskLevel = overallRiskLevel,
            RiskScore = overallRiskScore,
            DeviceCount = deviceObservations.Count,
            ConnectedUserCount = userObservations.Count,
            HighRiskDeviceCount = deviceObservations.Count(observation => observation.RiskLevel == "high" || observation.RiskLevel == "critical"),
            MediumRiskDeviceCount = deviceObservations.Count(observation => observation.RiskLevel == "medium"),
            LowRiskDeviceCount = deviceObservations.Count(observation => observation.RiskLevel == "low"),
            ObservedAtUtc = observedAtUtc,
            WindowStartUtc = request.WindowStartUtc,
            WindowEndUtc = request.WindowEndUtc,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? string.Empty : request.Notes.Trim(),
            PayloadJson = JsonSerializer.Serialize(request),
            CreatedByUsername = createdByUsername,
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.NetworkTelemetrySnapshots.Add(snapshot);
        await _context.SaveChangesAsync(cancellationToken);

        var deviceEntities = deviceObservations.Select(observation => new NetworkTelemetryObservation
        {
            NetworkTelemetrySnapshotId = snapshot.Id,
            ObservationType = "device",
            ExternalKey = observation.ExternalKey,
            DeviceName = observation.DeviceName,
            Username = observation.Username,
            Domain = observation.Domain,
            IpAddress = observation.IpAddress,
            MacAddress = observation.MacAddress,
            SerialNumber = observation.SerialNumber,
            HostName = observation.HostName,
            BuildingExternalId = observation.BuildingExternalId,
            RoomExternalId = observation.RoomExternalId,
            ImportedInventoryItemId = observation.ImportedInventoryItemId,
            SyncedEquipmentId = observation.SyncedEquipmentId,
            AuthUserId = observation.AuthUserId,
            Status = observation.Status,
            RiskLevel = observation.RiskLevel,
            RiskScore = observation.RiskScore,
            RiskReasonsJson = JsonSerializer.Serialize(observation.RiskReasons),
            RawJson = observation.RawJson,
            ObservedAtUtc = observedAtUtc,
            CreatedAtUtc = DateTime.UtcNow
        }).ToList();

        var userEntities = userObservations.Select(observation => new NetworkTelemetryObservation
        {
            NetworkTelemetrySnapshotId = snapshot.Id,
            ObservationType = "user",
            ExternalKey = observation.ExternalKey,
            DeviceName = observation.DeviceName,
            Username = observation.Username,
            Domain = observation.Domain,
            IpAddress = observation.IpAddress,
            MacAddress = observation.MacAddress,
            SerialNumber = observation.SerialNumber,
            HostName = observation.HostName,
            BuildingExternalId = observation.BuildingExternalId,
            RoomExternalId = observation.RoomExternalId,
            ImportedInventoryItemId = observation.ImportedInventoryItemId,
            SyncedEquipmentId = observation.SyncedEquipmentId,
            AuthUserId = observation.AuthUserId,
            Status = observation.Status,
            RiskLevel = observation.RiskLevel,
            RiskScore = observation.RiskScore,
            RiskReasonsJson = JsonSerializer.Serialize(observation.RiskReasons),
            RawJson = observation.RawJson,
            ObservedAtUtc = observedAtUtc,
            CreatedAtUtc = DateTime.UtcNow
        }).ToList();

        _context.NetworkTelemetryObservations.AddRange(deviceEntities);
        _context.NetworkTelemetryObservations.AddRange(userEntities);
        await _context.SaveChangesAsync(cancellationToken);

        await CleanupOldSnapshotsAsync(cancellationToken);

        await _auditLogService.LogSecurityEventAsync(
            actionType: "network-telemetry-ingest",
            resource: "network-telemetry",
            summary: $"Se registro una observacion de red desde {sourceName}",
            details: $"Dispositivos: {deviceObservations.Count}; Usuarios: {userObservations.Count}; Riesgo: {overallRiskLevel} ({overallRiskScore})",
            result: "success",
            severity: overallRiskLevel == "critical" ? "critical" : "info",
            changedByUsername: createdByUsername,
            cancellationToken: cancellationToken);

        return new NetworkTelemetryIngestResultViewModel
        {
            SnapshotId = snapshot.Id,
            SourceName = snapshot.SourceName,
            SourceType = snapshot.SourceType,
            ObservedAtUtc = snapshot.ObservedAtUtc,
            DeviceCount = snapshot.DeviceCount,
            UserCount = snapshot.ConnectedUserCount,
            HighRiskDeviceCount = snapshot.HighRiskDeviceCount,
            MediumRiskDeviceCount = snapshot.MediumRiskDeviceCount,
            LowRiskDeviceCount = snapshot.LowRiskDeviceCount,
            HighRiskUserCount = userEntities.Count(observation => observation.RiskLevel == "high" || observation.RiskLevel == "critical"),
            MediumRiskUserCount = userEntities.Count(observation => observation.RiskLevel == "medium"),
            LowRiskUserCount = userEntities.Count(observation => observation.RiskLevel == "low"),
            OverallRiskScore = snapshot.RiskScore,
            OverallRiskLevel = snapshot.RiskLevel,
            Notes = snapshot.Notes
        };
    }

    public async Task<IReadOnlyList<NetworkTelemetryObservationViewModel>> GetTopRiskObservationsAsync(int snapshotId, int take = 10, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 50);

        var observations = await _context.NetworkTelemetryObservations
            .AsNoTracking()
            .Where(observation => observation.NetworkTelemetrySnapshotId == snapshotId)
            .OrderByDescending(observation => observation.RiskScore)
            .ThenByDescending(observation => observation.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        return observations.Select(MapObservation).ToList();
    }

    public async Task<IReadOnlyList<NetworkTelemetryObservationViewModel>> GetObservationsAsync(
        int snapshotId,
        int take = 25,
        string? observationType = null,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 100);
        var query = _context.NetworkTelemetryObservations
            .AsNoTracking()
            .Where(observation => observation.NetworkTelemetrySnapshotId == snapshotId);

        if (!string.IsNullOrWhiteSpace(observationType))
        {
            query = query.Where(observation => observation.ObservationType == observationType);
        }

        var observations = await query
            .OrderByDescending(observation => observation.RiskScore)
            .ThenByDescending(observation => observation.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        return observations.Select(MapObservation).ToList();
    }

    private static ObservationResult ScoreDevice(
        DeviceCandidate device,
        DateTime observedAtUtc,
        IReadOnlyList<InventoryMatchRecord> importedItems,
        IReadOnlyList<SyncedEquipmentMatchRecord> syncedEquipments,
        IReadOnlyList<AuthUserMatchRecord> authUsers,
        ISet<string> duplicateIpSet,
        ISet<string> duplicateMacSet)
    {
        var reasons = new List<string>();
        var score = 0;

        var importedMatch = FindImportedMatch(device, importedItems);
        var syncedMatch = FindSyncedEquipmentMatch(device, syncedEquipments);
        var authMatch = FindAuthUserMatch(device.Username, authUsers);

        if (importedMatch is null && syncedMatch is null)
        {
            score += 35;
            reasons.Add("No coincide con inventario");
        }
        else
        {
            if ((importedMatch is not null && string.IsNullOrWhiteSpace(importedMatch.AssignedBuildingExternalId)) ||
                (syncedMatch is not null && string.IsNullOrWhiteSpace(syncedMatch.BuildingExternalId)))
            {
                score += 15;
                reasons.Add("Sin asignacion a edificio");
            }
        }

        if (!string.IsNullOrWhiteSpace(device.IpAddress) && duplicateIpSet.Contains(Normalize(device.IpAddress)))
        {
            score += 25;
            reasons.Add("IP duplicada");
        }

        if (IsValidMac(device.MacAddress) && duplicateMacSet.Contains(Normalize(device.MacAddress)))
        {
            score += 25;
            reasons.Add("MAC duplicada");
        }

        if (!string.IsNullOrWhiteSpace(device.Username) && authMatch is null)
        {
            score += 15;
            reasons.Add("Usuario no conocido");
        }

        var missingIdentifiers = 0;
        if (string.IsNullOrWhiteSpace(device.SerialNumber)) missingIdentifiers++;
        if (string.IsNullOrWhiteSpace(device.IpAddress)) missingIdentifiers++;
        if (string.IsNullOrWhiteSpace(device.MacAddress)) missingIdentifiers++;
        if (missingIdentifiers >= 2)
        {
            score += 10;
            reasons.Add("Identificadores incompletos");
        }

        score = Math.Min(score, 100);
        var riskLevel = ToRiskLevel(score);

        return new ObservationResult(
            ExternalKey: string.IsNullOrWhiteSpace(device.ExternalKey) ? BuildFallbackExternalKey(device) : device.ExternalKey,
            DeviceName: device.DeviceName,
            Username: device.Username,
            Domain: device.Domain,
            IpAddress: device.IpAddress,
            MacAddress: device.MacAddress,
            SerialNumber: device.SerialNumber,
            HostName: device.HostName,
            BuildingExternalId: importedMatch?.AssignedBuildingExternalId ?? syncedMatch?.BuildingExternalId ?? device.BuildingExternalId,
            RoomExternalId: importedMatch?.AssignedRoomExternalId ?? syncedMatch?.RoomExternalId ?? device.RoomExternalId,
            ImportedInventoryItemId: importedMatch?.Id,
            SyncedEquipmentId: syncedMatch?.Id,
            AuthUserId: authMatch?.Id,
            Status: device.Status,
            RiskLevel: riskLevel,
            RiskScore: score,
            RiskReasons: reasons,
            RawJson: JsonSerializer.Serialize(device),
            ObservedAtUtc: observedAtUtc);
    }

    private static ObservationResult ScoreUser(
        UserCandidate user,
        DateTime observedAtUtc,
        IReadOnlyList<AuthUserMatchRecord> authUsers,
        int linkedDeviceCount)
    {
        var reasons = new List<string>();
        var score = 0;
        var authMatch = FindAuthUserMatch(user.Username, authUsers);

        if (authMatch is null && !string.IsNullOrWhiteSpace(user.Username))
        {
            score += 20;
            reasons.Add("Usuario no conocido");
        }

        if (linkedDeviceCount >= 5)
        {
            score += 15;
            reasons.Add("Demasiados equipos asociados");
        }
        else if (linkedDeviceCount >= 3)
        {
            score += 10;
            reasons.Add("Varios equipos asociados");
        }

        if (string.Equals(user.Status, "critical", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
            reasons.Add("Estado critico");
        }

        score = Math.Min(score, 100);
        var riskLevel = ToRiskLevel(score);

        return new ObservationResult(
            ExternalKey: string.IsNullOrWhiteSpace(user.ExternalKey) ? BuildFallbackExternalKey(user.Username, "user") : user.ExternalKey,
            DeviceName: user.DisplayName,
            Username: user.Username,
            Domain: string.Empty,
            IpAddress: string.Empty,
            MacAddress: string.Empty,
            SerialNumber: string.Empty,
            HostName: string.Empty,
            BuildingExternalId: string.Empty,
            RoomExternalId: string.Empty,
            ImportedInventoryItemId: null,
            SyncedEquipmentId: null,
            AuthUserId: authMatch?.Id,
            Status: user.Status,
            RiskLevel: riskLevel,
            RiskScore: score,
            RiskReasons: reasons,
            RawJson: JsonSerializer.Serialize(user),
            ObservedAtUtc: observedAtUtc);
    }

    private async Task CleanupOldSnapshotsAsync(CancellationToken cancellationToken)
    {
        var retentionCutoff = DateTime.UtcNow.AddDays(-Math.Max(1, RetentionDays()));
        var oldSnapshots = await _context.NetworkTelemetrySnapshots
            .Where(snapshot => snapshot.ObservedAtUtc < retentionCutoff)
            .OrderBy(snapshot => snapshot.ObservedAtUtc)
            .Take(500)
            .ToListAsync(cancellationToken);

        if (oldSnapshots.Count > 0)
        {
            _context.NetworkTelemetrySnapshots.RemoveRange(oldSnapshots);
            await _context.SaveChangesAsync(cancellationToken);
        }

        var excessCount = await _context.NetworkTelemetrySnapshots.CountAsync(cancellationToken) - MaxSnapshots();
        if (excessCount > 0)
        {
            var toDelete = await _context.NetworkTelemetrySnapshots
                .OrderBy(snapshot => snapshot.ObservedAtUtc)
                .ThenBy(snapshot => snapshot.Id)
                .Take(excessCount)
                .ToListAsync(cancellationToken);

            _context.NetworkTelemetrySnapshots.RemoveRange(toDelete);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    private static InventoryMatchRecord? FindImportedMatch(DeviceCandidate device, IReadOnlyList<InventoryMatchRecord> importedItems)
    {
        if (!string.IsNullOrWhiteSpace(device.SerialNumber))
        {
            var bySerial = importedItems.FirstOrDefault(item => string.Equals(item.SerialNumber, Normalize(device.SerialNumber), StringComparison.OrdinalIgnoreCase));
            if (bySerial is not null) return bySerial;
        }

        if (!string.IsNullOrWhiteSpace(device.MacAddress) && IsValidMac(device.MacAddress))
        {
            var byMac = importedItems.FirstOrDefault(item => string.Equals(item.MacAddress, Normalize(device.MacAddress), StringComparison.OrdinalIgnoreCase));
            if (byMac is not null) return byMac;
        }

        if (!string.IsNullOrWhiteSpace(device.IpAddress))
        {
            var byIp = importedItems.FirstOrDefault(item => string.Equals(item.IpAddress, Normalize(device.IpAddress), StringComparison.OrdinalIgnoreCase));
            if (byIp is not null) return byIp;
        }

        return null;
    }

    private static SyncedEquipmentMatchRecord? FindSyncedEquipmentMatch(DeviceCandidate device, IReadOnlyList<SyncedEquipmentMatchRecord> syncedEquipments)
    {
        if (!string.IsNullOrWhiteSpace(device.SerialNumber))
        {
            var bySerial = syncedEquipments.FirstOrDefault(item => string.Equals(item.SerialNumber, Normalize(device.SerialNumber), StringComparison.OrdinalIgnoreCase));
            if (bySerial is not null) return bySerial;
        }

        if (!string.IsNullOrWhiteSpace(device.MacAddress) && IsValidMac(device.MacAddress))
        {
            var byMac = syncedEquipments.FirstOrDefault(item => string.Equals(item.MacAddress, Normalize(device.MacAddress), StringComparison.OrdinalIgnoreCase));
            if (byMac is not null) return byMac;
        }

        if (!string.IsNullOrWhiteSpace(device.IpAddress))
        {
            var byIp = syncedEquipments.FirstOrDefault(item => string.Equals(item.IpAddress, Normalize(device.IpAddress), StringComparison.OrdinalIgnoreCase));
            if (byIp is not null) return byIp;
        }

        return null;
    }

    private static AuthUserMatchRecord? FindAuthUserMatch(string username, IReadOnlyList<AuthUserMatchRecord> authUsers)
    {
        var candidates = BuildUsernameCandidates(username);
        if (candidates.Count == 0)
            return null;

        return authUsers.FirstOrDefault(user => candidates.Contains(user.NormalizedUsername, StringComparer.OrdinalIgnoreCase));
    }

    private static List<string> BuildUsernameCandidates(string username)
    {
        var normalized = Normalize(username);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        var candidates = new List<string> { normalized };
        var localPart = normalized.Split('@', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(localPart))
        {
            candidates.Add(localPart);
        }

        var backslashIndex = normalized.IndexOf('\\');
        if (backslashIndex >= 0 && backslashIndex < normalized.Length - 1)
        {
            candidates.Add(normalized[(backslashIndex + 1)..]);
        }

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ToRiskLevel(int score)
        => score >= 70 ? "critical" : score >= 40 ? "high" : score >= 20 ? "medium" : "low";

    private static string BuildFallbackExternalKey(DeviceCandidate device)
    {
        var baseValue = string.Join("|", new[]
        {
            Normalize(device.SerialNumber),
            Normalize(device.IpAddress),
            Normalize(device.MacAddress),
            Normalize(device.DeviceName)
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return string.IsNullOrWhiteSpace(baseValue)
            ? $"unknown-device-{HashSuffix("device")}"
            : baseValue;
    }

    private static string BuildFallbackExternalKey(string value, string suffix)
    {
        var baseValue = string.Join("|", new[] { Normalize(value), suffix }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return string.IsNullOrWhiteSpace(baseValue)
            ? $"unknown-{suffix}-{HashSuffix(value)}"
            : baseValue;
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();

    private static string Clean(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim();

    private static string HashSuffix(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value));
        return Convert.ToHexString(bytes)[..10];
    }

    private static bool IsValidMac(string? value)
    {
        var normalized = Normalize(value);
        return !string.IsNullOrWhiteSpace(normalized) &&
               normalized is not "N/D" &&
               normalized is not "ND" &&
               normalized is not "-" &&
               normalized.Length >= 8;
    }

    private static DeviceCandidate NormalizeDeviceInput(NetworkTelemetryDeviceInput input)
    {
        return new DeviceCandidate(
            ExternalKey: Clean(input.ExternalKey),
            DeviceName: Clean(input.DeviceName),
            Username: Clean(input.Username),
            Domain: Clean(input.Domain),
            IpAddress: Clean(input.IpAddress),
            MacAddress: Clean(input.MacAddress),
            SerialNumber: Clean(input.SerialNumber),
            HostName: Clean(input.HostName),
            BuildingExternalId: Clean(input.BuildingExternalId),
            RoomExternalId: Clean(input.RoomExternalId),
            Status: Clean(input.Status),
            Notes: Clean(input.Notes));
    }

    private static UserCandidate NormalizeUserInput(NetworkTelemetryUserInput input)
    {
        return new UserCandidate(
            ExternalKey: Clean(input.ExternalKey),
            Username: Clean(input.Username),
            DisplayName: Clean(input.DisplayName),
            DeviceCount: input.DeviceCount ?? 0,
            Status: Clean(input.Status),
            Notes: Clean(input.Notes));
    }

    private static NetworkTelemetrySnapshotViewModel MapSnapshot(NetworkTelemetrySnapshot snapshot)
    {
        return new NetworkTelemetrySnapshotViewModel
        {
            Id = snapshot.Id,
            SourceName = snapshot.SourceName,
            SourceType = snapshot.SourceType,
            Status = snapshot.Status,
            RiskLevel = snapshot.RiskLevel,
            RiskScore = snapshot.RiskScore,
            DeviceCount = snapshot.DeviceCount,
            ConnectedUserCount = snapshot.ConnectedUserCount,
            HighRiskDeviceCount = snapshot.HighRiskDeviceCount,
            MediumRiskDeviceCount = snapshot.MediumRiskDeviceCount,
            LowRiskDeviceCount = snapshot.LowRiskDeviceCount,
            ObservedAtUtc = snapshot.ObservedAtUtc,
            WindowStartUtc = snapshot.WindowStartUtc,
            WindowEndUtc = snapshot.WindowEndUtc,
            Notes = snapshot.Notes,
            CreatedByUsername = snapshot.CreatedByUsername
        };
    }

    private static NetworkTelemetryObservationViewModel MapObservation(NetworkTelemetryObservation observation)
    {
        return new NetworkTelemetryObservationViewModel
        {
            Id = observation.Id,
            ObservationType = observation.ObservationType,
            ExternalKey = observation.ExternalKey,
            DeviceName = observation.DeviceName,
            Username = observation.Username,
            Domain = observation.Domain,
            IpAddress = observation.IpAddress,
            MacAddress = observation.MacAddress,
            SerialNumber = observation.SerialNumber,
            HostName = observation.HostName,
            BuildingExternalId = observation.BuildingExternalId,
            RoomExternalId = observation.RoomExternalId,
            Status = observation.Status,
            RiskLevel = observation.RiskLevel,
            RiskScore = observation.RiskScore,
            RiskReasons = string.IsNullOrWhiteSpace(observation.RiskReasonsJson)
                ? []
                : JsonSerializer.Deserialize<List<string>>(observation.RiskReasonsJson) ?? [],
            ObservedAtUtc = observation.ObservedAtUtc
        };
    }

    private sealed record InventoryMatchRecord(
        int Id,
        string SerialNumber,
        string IpAddress,
        string MacAddress,
        string ResponsibleUser,
        string AssignedBuildingExternalId,
        string AssignedRoomExternalId);

    private sealed record SyncedEquipmentMatchRecord(
        int Id,
        string SerialNumber,
        string IpAddress,
        string MacAddress,
        string Name,
        string BuildingExternalId,
        string RoomExternalId,
        int SyncedBuildingId,
        int? SyncedRoomId);

    private sealed record AuthUserMatchRecord(
        int Id,
        string Username,
        string NormalizedUsername,
        string Role);

    private sealed record DeviceCandidate(
        string ExternalKey,
        string DeviceName,
        string Username,
        string Domain,
        string IpAddress,
        string MacAddress,
        string SerialNumber,
        string HostName,
        string BuildingExternalId,
        string RoomExternalId,
        string Status,
        string Notes);

    private sealed record UserCandidate(
        string ExternalKey,
        string Username,
        string DisplayName,
        int DeviceCount,
        string Status,
        string Notes);

    private sealed record ObservationResult(
        string ExternalKey,
        string DeviceName,
        string Username,
        string Domain,
        string IpAddress,
        string MacAddress,
        string SerialNumber,
        string HostName,
        string BuildingExternalId,
        string RoomExternalId,
        int? ImportedInventoryItemId,
        int? SyncedEquipmentId,
        int? AuthUserId,
        string Status,
        string RiskLevel,
        int RiskScore,
        IReadOnlyList<string> RiskReasons,
        string RawJson,
        DateTime ObservedAtUtc);
}
