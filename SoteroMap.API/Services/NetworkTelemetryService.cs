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
        var buildingRiskSummaries = latest is null
            ? []
            : await GetBuildingRiskSummariesAsync(latest.Id, cancellationToken);
        var subnetRiskSummaries = latest is null
            ? []
            : await GetSubnetRiskSummariesAsync(latest.Id, cancellationToken);
        var sessionOverview = await GetSessionOverviewAsync(cancellationToken);

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
            LatestSnapshotId = latest?.Id ?? 0,
            LatestObservedAtUtc = latest?.ObservedAtUtc,
            LatestWindowStartUtc = latest?.WindowStartUtc,
            LatestWindowEndUtc = latest?.WindowEndUtc,
            GeneratedAtUtc = nowUtc,
            RecentSnapshots = snapshots.Select(MapSnapshot).ToList(),
            TopRiskObservations = topRiskObservations,
            BuildingRiskSummaries = buildingRiskSummaries,
            SubnetRiskSummaries = subnetRiskSummaries,
            SessionOverview = sessionOverview
        };
    }

    public async Task<NetworkTelemetrySessionOverviewViewModel> GetSessionOverviewAsync(CancellationToken cancellationToken = default)
    {
        var latestSnapshot = await _context.NetworkTelemetrySnapshots
            .AsNoTracking()
            .OrderByDescending(snapshot => snapshot.ObservedAtUtc)
            .ThenByDescending(snapshot => snapshot.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestSnapshot is null)
        {
            return new NetworkTelemetrySessionOverviewViewModel();
        }

        var deviceObservations = await _context.NetworkTelemetryObservations
            .AsNoTracking()
            .Where(observation => observation.NetworkTelemetrySnapshotId == latestSnapshot.Id && observation.ObservationType == "device")
            .OrderByDescending(observation => observation.ObservedAtUtc)
            .ThenByDescending(observation => observation.Id)
            .ToListAsync(cancellationToken);

        if (deviceObservations.Count == 0)
        {
            return new NetworkTelemetrySessionOverviewViewModel();
        }

        var userObservations = await _context.NetworkTelemetryObservations
            .AsNoTracking()
            .Where(observation => observation.NetworkTelemetrySnapshotId == latestSnapshot.Id && observation.ObservationType == "user")
            .OrderByDescending(observation => observation.ObservedAtUtc)
            .ThenByDescending(observation => observation.Id)
            .ToListAsync(cancellationToken);

        var sessionUsers = new List<NetworkTelemetrySessionUserViewModel>();
        var activeCount = 0;
        var lockedCount = 0;
        var expiredCount = 0;
        var pendingIdentityCount = 0;
        var inactiveCount = 0;

        if (userObservations.Count > 0)
        {
            foreach (var group in userObservations.GroupBy(observation => ResolveDisplayIdentity(observation), StringComparer.OrdinalIgnoreCase))
            {
                var latestUser = group
                    .OrderByDescending(item => item.ObservedAtUtc)
                    .ThenByDescending(item => item.Id)
                    .First();

                var identity = ResolveDisplayIdentity(latestUser);
                var linkedDevices = deviceObservations
                    .Where(device => string.Equals(Normalize(device.Username), Normalize(identity), StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var latestDevice = linkedDevices
                    .OrderByDescending(item => item.ObservedAtUtc)
                    .ThenByDescending(item => item.Id)
                    .FirstOrDefault();
                var sessionState = ResolveUserObservationState(latestUser, latestDevice);
                var sessionStateLabel = sessionState switch
                {
                    "active" => "Conectado",
                    "locked" => "Riesgo alto",
                    "expired" => "Sin respuesta",
                    "mfa-pending" => "Identidad incierta",
                    "inactive" => "Inactivo",
                    _ => "Sin datos"
                };

                switch (sessionState)
                {
                    case "active":
                        activeCount++;
                        break;
                    case "locked":
                        lockedCount++;
                        break;
                    case "expired":
                        expiredCount++;
                        break;
                    case "mfa-pending":
                        pendingIdentityCount++;
                        break;
                    default:
                        inactiveCount++;
                        break;
                }

                sessionUsers.Add(new NetworkTelemetrySessionUserViewModel
                {
                    Username = identity,
                    Role = !string.IsNullOrWhiteSpace(latestDevice?.DeviceCategory) ? latestDevice.DeviceCategory : "user",
                    SessionState = sessionState,
                    SessionStateLabel = sessionStateLabel,
                    EndpointKey = latestUser.ExternalKey,
                    SubnetCidr = latestDevice?.SubnetCidr ?? string.Empty,
                    NetworkProfile = latestDevice?.NetworkProfile ?? string.Empty,
                    OpenPorts = latestDevice?.OpenPorts ?? string.Empty,
                    PingMs = latestDevice?.PingMs,
                    IsOnline = latestDevice?.IsOnline ?? latestUser.IsOnline,
                    RiskScore = Math.Max(latestUser.RiskScore, latestDevice?.RiskScore ?? 0),
                    RiskLevel = (latestDevice?.RiskScore ?? 0) > latestUser.RiskScore ? latestDevice!.RiskLevel : latestUser.RiskLevel,
                    LastLoginAtUtc = latestUser.ObservedAtUtc,
                    LastLogoutAtUtc = group
                        .Where(item => item.IsOnline == false)
                        .OrderByDescending(item => item.ObservedAtUtc)
                        .ThenByDescending(item => item.Id)
                        .Select(item => (DateTime?)item.ObservedAtUtc)
                        .FirstOrDefault(),
                    LastMfaVerifiedAtUtc = latestUser.ObservedAtUtc,
                    IsActive = sessionState == "active",
                    LinkedDeviceCount = linkedDevices.Count,
                    LastSeenAtUtc = latestDevice?.ObservedAtUtc ?? latestUser.ObservedAtUtc
                });
            }
        }
        else
        {
            foreach (var group in deviceObservations.GroupBy(BuildEndpointIdentity, StringComparer.OrdinalIgnoreCase))
            {
                var latest = group
                    .OrderByDescending(item => item.ObservedAtUtc)
                    .ThenByDescending(item => item.Id)
                    .First();

                var identity = ResolveDisplayIdentity(latest);
                var sessionState = DetermineNetworkSessionState(latest, identity);
                var sessionStateLabel = sessionState switch
                {
                    "active" => "Conectado",
                    "locked" => "Riesgo alto",
                    "expired" => "Sin respuesta",
                    "mfa-pending" => "Identidad incierta",
                    "inactive" => "Inactivo",
                    _ => "Sin datos"
                };

                switch (sessionState)
                {
                    case "active":
                        activeCount++;
                        break;
                    case "locked":
                        lockedCount++;
                        break;
                    case "expired":
                        expiredCount++;
                        break;
                    case "mfa-pending":
                        pendingIdentityCount++;
                        break;
                    default:
                        inactiveCount++;
                        break;
                }

                sessionUsers.Add(new NetworkTelemetrySessionUserViewModel
                {
                    Username = identity,
                    Role = string.IsNullOrWhiteSpace(latest.DeviceCategory) ? "network" : latest.DeviceCategory,
                    SessionState = sessionState,
                    SessionStateLabel = sessionStateLabel,
                    EndpointKey = latest.ExternalKey,
                    SubnetCidr = latest.SubnetCidr,
                    NetworkProfile = latest.NetworkProfile,
                    OpenPorts = latest.OpenPorts,
                    PingMs = latest.PingMs,
                    IsOnline = latest.IsOnline,
                    RiskScore = latest.RiskScore,
                    RiskLevel = latest.RiskLevel,
                    LastLoginAtUtc = latest.ObservedAtUtc,
                    LastLogoutAtUtc = group
                        .Where(item => item.IsOnline == false)
                        .OrderByDescending(item => item.ObservedAtUtc)
                        .ThenByDescending(item => item.Id)
                        .Select(item => (DateTime?)item.ObservedAtUtc)
                        .FirstOrDefault(),
                    LastMfaVerifiedAtUtc = latest.ObservedAtUtc,
                    IsActive = latest.IsOnline ?? false,
                    LinkedDeviceCount = group.Count(),
                    LastSeenAtUtc = latest.ObservedAtUtc
                });
            }
        }

        var orderedUsers = sessionUsers
            .OrderByDescending(user => user.SessionState == "active")
            .ThenByDescending(user => user.LastSeenAtUtc ?? DateTime.MinValue)
            .ThenBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

        return new NetworkTelemetrySessionOverviewViewModel
        {
            ActiveUserCount = activeCount,
            LockedUserCount = lockedCount,
            ExpiredUserCount = expiredCount,
            PendingMfaUserCount = pendingIdentityCount,
            InactiveUserCount = inactiveCount,
            TotalEvaluatedUsers = sessionUsers.Count,
            Users = orderedUsers
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
        var liveScanMode = string.Equals(sourceType, "live-scan", StringComparison.OrdinalIgnoreCase);

        var deviceInputs = request.Devices ?? [];
        var userInputs = request.Users ?? [];
        var normalizedDevices = deviceInputs
            .Select(input => NormalizeDeviceInput(input))
            .ToList();
        var normalizedUsers = userInputs
            .Select(input => NormalizeUserInput(input))
            .ToList();
        var importedItems = liveScanMode
            ? []
            : await _context.ImportedInventoryItems
                .AsNoTracking()
                .Select(item => new InventoryMatchRecord(
                    item.Id,
                    Normalize(item.SerialNumber),
                    Normalize(item.IpAddress),
                    Normalize(item.MacAddress),
                    Normalize(item.ResponsibleUser),
                    Normalize(item.Email),
                    Normalize(item.UnitOrDepartment),
                    Normalize(item.OrganizationalUnit),
                    Normalize(item.AssignedBuildingExternalId),
                    Normalize(item.AssignedRoomExternalId)))
                .ToListAsync(cancellationToken);

        var syncedEquipments = liveScanMode
            ? []
            : await _context.SyncedEquipments
                .AsNoTracking()
                .Select(item => new SyncedEquipmentMatchRecord(
                    item.Id,
                    Normalize(item.SerialNumber),
                    Normalize(item.IpAddress),
                    Normalize(item.MacAddress),
                    Normalize(item.AssignedTo),
                    Normalize(item.ResponsiblePerson),
                    Normalize(item.Name),
                    Normalize(item.BuildingExternalId),
                    Normalize(item.RoomExternalId),
                    item.SyncedBuildingId,
                    item.SyncedRoomId))
                .ToListAsync(cancellationToken);

        var authUsers = liveScanMode
            ? []
            : await _context.AuthUsers
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
                duplicateMacSet,
                string.Equals(sourceType, "live-scan", StringComparison.OrdinalIgnoreCase)));
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
            DeviceCategory = observation.DeviceCategory,
            OperatingSystem = observation.OperatingSystem,
            OperatingSystemVersion = observation.OperatingSystemVersion,
            Manufacturer = observation.Manufacturer,
            Model = observation.Model,
            Processor = observation.Processor,
            MemoryGb = observation.MemoryGb,
            DiskTotalGb = observation.DiskTotalGb,
            DiskFreeGb = observation.DiskFreeGb,
            LastBootAtUtc = observation.LastBootAtUtc,
            IsOnline = observation.IsOnline,
            DomainJoined = observation.DomainJoined,
            IsVirtualMachine = observation.IsVirtualMachine,
            PingMs = observation.PingMs,
            AntivirusStatus = observation.AntivirusStatus,
            AntivirusVersion = observation.AntivirusVersion,
            PatchStatus = observation.PatchStatus,
            AgentVersion = observation.AgentVersion,
            OpenPorts = observation.OpenPorts,
            SubnetCidr = observation.SubnetCidr,
            NetworkProfile = observation.NetworkProfile,
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

    public async Task<NetworkTelemetryObservationPageViewModel> GetObservationPageAsync(
        int snapshotId,
        NetworkTelemetryObservationQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = request.PageSize switch
        {
            25 or 50 or 100 or 200 => request.PageSize,
            _ => 50
        };

        var observationType = string.IsNullOrWhiteSpace(request.ObservationType) ? "device" : request.ObservationType.Trim().ToLowerInvariant();
        var query = _context.NetworkTelemetryObservations
            .AsNoTracking()
            .Where(observation => observation.NetworkTelemetrySnapshotId == snapshotId);

        if (!string.IsNullOrWhiteSpace(observationType))
        {
            query = query.Where(observation => observation.ObservationType == observationType);
        }

        if (!string.IsNullOrWhiteSpace(request.RiskLevel))
        {
            var normalizedRiskLevel = request.RiskLevel.Trim().ToLowerInvariant();
            query = query.Where(observation => observation.RiskLevel == normalizedRiskLevel);
        }

        if (!string.IsNullOrWhiteSpace(request.BuildingExternalId))
        {
            var normalizedBuildingId = request.BuildingExternalId.Trim();
            query = query.Where(observation => observation.BuildingExternalId == normalizedBuildingId);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(observation =>
                observation.DeviceName.Contains(search) ||
                observation.Username.Contains(search) ||
                observation.IpAddress.Contains(search) ||
                observation.MacAddress.Contains(search) ||
                observation.SerialNumber.Contains(search) ||
                observation.HostName.Contains(search) ||
                observation.BuildingExternalId.Contains(search) ||
                observation.RoomExternalId.Contains(search) ||
                observation.OperatingSystem.Contains(search) ||
                observation.Model.Contains(search) ||
                observation.Manufacturer.Contains(search));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));

        var items = await query
            .OrderByDescending(observation => observation.RiskScore)
            .ThenByDescending(observation => observation.ObservedAtUtc)
            .ThenByDescending(observation => observation.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var buildingRiskSummaries = observationType == "device"
            ? await BuildBuildingRiskSummariesAsync(query, cancellationToken)
            : [];

        return new NetworkTelemetryObservationPageViewModel
        {
            SnapshotId = snapshotId,
            Search = request.Search ?? string.Empty,
            RiskLevel = request.RiskLevel ?? string.Empty,
            BuildingExternalId = request.BuildingExternalId ?? string.Empty,
            ObservationType = observationType,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            Items = items.Select(MapObservation).ToList(),
            BuildingRiskSummaries = buildingRiskSummaries
        };
    }

    public async Task<IReadOnlyList<NetworkTelemetryBuildingRiskSummaryViewModel>> GetBuildingRiskSummariesAsync(
        int snapshotId,
        CancellationToken cancellationToken = default)
    {
        var query = _context.NetworkTelemetryObservations
            .AsNoTracking()
            .Where(observation => observation.NetworkTelemetrySnapshotId == snapshotId && observation.ObservationType == "device");

        return await BuildBuildingRiskSummariesAsync(query, cancellationToken);
    }

    private static string BuildSessionState(
        AuthUser user,
        DateTime? lastLoginAtUtc,
        DateTime? lastLogoutAtUtc,
        DateTime idleCutoff,
        DateTime nowUtc)
    {
        if (!user.IsActive)
        {
            return "inactive";
        }

        if (user.LockedUntilUtc.HasValue && user.LockedUntilUtc.Value > nowUtc)
        {
            return "locked";
        }

        if (string.Equals(user.Role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase) &&
            (!user.MfaEnabled || string.IsNullOrWhiteSpace(user.MfaSecretProtected)))
        {
            return "mfa-pending";
        }

        var activityReference = user.MfaLastVerifiedAtUtc ?? lastLoginAtUtc;
        if (activityReference.HasValue)
        {
            if (lastLogoutAtUtc.HasValue && lastLogoutAtUtc.Value > activityReference.Value)
            {
                return "expired";
            }

            if (activityReference.Value >= idleCutoff)
            {
                return "active";
            }
        }

        return lastLoginAtUtc.HasValue ? "expired" : "inactive";
    }

    private static int CountLinkedDevices(
        string username,
        IReadOnlyList<LinkedImportedRow> importedDeviceRows,
        IReadOnlyList<LinkedSyncedRow> syncedDeviceRows)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return 0;
        }

        var candidates = BuildUsernameCandidates(username);
        var importedCount = importedDeviceRows.Count(item =>
            candidates.Contains(Normalize(item.ResponsibleUser)) ||
            candidates.Contains(Normalize(item.Email)) ||
            candidates.Contains(Normalize(item.UnitOrDepartment)) ||
            candidates.Contains(Normalize(item.OrganizationalUnit)));

        var syncedCount = syncedDeviceRows.Count(item =>
            candidates.Contains(Normalize(item.AssignedTo)) ||
            candidates.Contains(Normalize(item.ResponsiblePerson)) ||
            candidates.Contains(Normalize(item.Name)) ||
            candidates.Contains(Normalize(item.InventoryCode)));

        return importedCount + syncedCount;
    }

    private static string BuildEndpointIdentity(NetworkTelemetryObservation observation)
    {
        var candidates = new[]
        {
            observation.Username,
            observation.HostName,
            observation.DeviceName,
            observation.IpAddress,
            observation.ExternalKey
        };

        return candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? $"endpoint-{observation.Id}";
    }

    private static string ResolveDisplayIdentity(NetworkTelemetryObservation observation)
    {
        var candidates = new[]
        {
            observation.Username,
            observation.DeviceName,
            observation.HostName,
            observation.IpAddress
        };

        var identity = candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(identity))
        {
            return $"endpoint-{observation.Id}";
        }

        return identity!;
    }

    private static string DetermineNetworkSessionState(NetworkTelemetryObservation observation, string identity)
    {
        var hasHumanIdentity = !string.IsNullOrWhiteSpace(identity) &&
                               !identity.Equals(observation.IpAddress, StringComparison.OrdinalIgnoreCase) &&
                               !identity.Equals(observation.HostName, StringComparison.OrdinalIgnoreCase);
        var isRisky = observation.RiskLevel is "high" or "critical";

        if (observation.IsOnline == true && isRisky)
        {
            return "locked";
        }

        if (observation.IsOnline == true)
        {
            return hasHumanIdentity ? "active" : "mfa-pending";
        }

        if (observation.IsOnline == false || observation.PingMs is null && string.IsNullOrWhiteSpace(observation.OpenPorts))
        {
            return "expired";
        }

        return hasHumanIdentity ? "inactive" : "mfa-pending";
    }

    private static string ResolveUserObservationState(NetworkTelemetryObservation userObservation, NetworkTelemetryObservation? linkedDevice)
    {
        var normalizedStatus = Normalize(userObservation.Status);
        if (normalizedStatus.Contains("ACTIVE", StringComparison.OrdinalIgnoreCase) ||
            normalizedStatus.Contains("ACTIVO", StringComparison.OrdinalIgnoreCase))
        {
            return linkedDevice?.RiskLevel is "high" or "critical" ? "locked" : "active";
        }

        if (normalizedStatus.Contains("DISC", StringComparison.OrdinalIgnoreCase) ||
            normalizedStatus.Contains("DISCONNECTED", StringComparison.OrdinalIgnoreCase) ||
            normalizedStatus.Contains("DESCONECT", StringComparison.OrdinalIgnoreCase))
        {
            return "inactive";
        }

        if (linkedDevice is not null)
        {
            return DetermineNetworkSessionState(linkedDevice, ResolveDisplayIdentity(linkedDevice));
        }

        return string.IsNullOrWhiteSpace(userObservation.Username) ? "mfa-pending" : "active";
    }

    private static string ResolveDeviceUsername(
        DeviceCandidate device,
        InventoryMatchRecord? importedMatch,
        SyncedEquipmentMatchRecord? syncedMatch)
    {
        var candidates = new[]
        {
            importedMatch?.ResponsibleUser,
            importedMatch?.Email,
            importedMatch?.UnitOrDepartment,
            importedMatch?.OrganizationalUnit,
            syncedMatch?.AssignedTo,
            syncedMatch?.ResponsiblePerson,
            device.Username,
            device.HostName,
            device.DeviceName,
            device.IpAddress
        };

        return candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static bool LooksLikeHumanIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = Normalize(value);
        if (normalized.Contains('@', StringComparison.Ordinal) || normalized.Contains('\\', StringComparison.Ordinal))
        {
            return true;
        }

        var devicePrefixes = new[]
        {
            "PC", "WS", "DESKTOP", "LAPTOP", "NOTEBOOK", "PRINTER", "IMPRES", "SERVER", "SRV", "MFP", "ZEBRA", "HP", "DELL", "LENOVO", "CANON", "EPSON", "BROTHER"
        };

        if (devicePrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return normalized.Any(char.IsLetter) && normalized.Any(char.IsLower) && normalized.Length >= 3;
    }

    private static string InferNetworkProfile(string deviceCategory, string openPorts, string? hostName, string? deviceName)
    {
        var ports = ParseOpenPorts(openPorts);
        var composite = $"{deviceCategory} {hostName} {deviceName}".ToUpperInvariant();

        if (ports.Contains(9100) || ports.Contains(631) || ports.Contains(515) ||
            composite.Contains("PRINTER", StringComparison.OrdinalIgnoreCase) ||
            composite.Contains("IMPRES", StringComparison.OrdinalIgnoreCase))
        {
            return "printer";
        }

        if (ports.Contains(3389) || ports.Contains(445) || ports.Contains(135) || ports.Contains(5985) || ports.Contains(5986))
        {
            return "workstation";
        }

        if (ports.Contains(22) || ports.Contains(389) || ports.Contains(88))
        {
            return "server";
        }

        if (ports.Contains(161) || ports.Contains(53))
        {
            return "infrastructure";
        }

        return string.IsNullOrWhiteSpace(deviceCategory) ? "network" : deviceCategory;
    }

    private static ObservationResult ScoreDevice(
        DeviceCandidate device,
        DateTime observedAtUtc,
        IReadOnlyList<InventoryMatchRecord> importedItems,
        IReadOnlyList<SyncedEquipmentMatchRecord> syncedEquipments,
        IReadOnlyList<AuthUserMatchRecord> authUsers,
        ISet<string> duplicateIpSet,
        ISet<string> duplicateMacSet,
        bool liveScanMode)
    {
        var reasons = new List<string>();
        var score = 0;

        var importedMatch = FindImportedMatch(device, importedItems);
        var syncedMatch = FindSyncedEquipmentMatch(device, syncedEquipments);
        var authMatch = FindAuthUserMatch(device.Username, authUsers);

        if (!liveScanMode && importedMatch is null && syncedMatch is null)
        {
            score += 35;
            reasons.Add("No coincide con inventario");
        }
        else if (!liveScanMode)
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

        if (!string.IsNullOrWhiteSpace(device.Username) && authMatch is null && !liveScanMode)
        {
            score += 15;
            reasons.Add("Usuario no conocido");
        }

        if (device.IsOnline == false)
        {
            score += 20;
            reasons.Add("Equipo sin respuesta en red");
        }

        if (!liveScanMode)
        {
            if (!string.IsNullOrWhiteSpace(device.AntivirusStatus))
            {
                var antivirusStatus = Normalize(device.AntivirusStatus);
                if (antivirusStatus.Contains("DISABLED", StringComparison.OrdinalIgnoreCase) ||
                    antivirusStatus.Contains("INACTIVE", StringComparison.OrdinalIgnoreCase) ||
                    antivirusStatus.Contains("OFF", StringComparison.OrdinalIgnoreCase) ||
                    antivirusStatus.Contains("NO_INSTALADO", StringComparison.OrdinalIgnoreCase) ||
                    antivirusStatus.Contains("NOT_INSTALLED", StringComparison.OrdinalIgnoreCase))
                {
                    score += 20;
                    reasons.Add("Antivirus deshabilitado o ausente");
                }
            }

            if (!string.IsNullOrWhiteSpace(device.PatchStatus))
            {
                var patchStatus = Normalize(device.PatchStatus);
                if (patchStatus.Contains("OUTDATED", StringComparison.OrdinalIgnoreCase) ||
                    patchStatus.Contains("PENDING", StringComparison.OrdinalIgnoreCase) ||
                    patchStatus.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
                {
                    score += 18;
                    reasons.Add("Parches pendientes o desactualizados");
                }
            }

            if (device.DomainJoined == false)
            {
                score += 12;
                reasons.Add("Equipo fuera de dominio");
            }

            if (device.DiskTotalGb.HasValue && device.DiskFreeGb.HasValue && device.DiskTotalGb.Value > 0)
            {
                var freeRatio = device.DiskFreeGb.Value / device.DiskTotalGb.Value;
                if (freeRatio <= 0.1d)
                {
                    score += 10;
                    reasons.Add("Espacio libre en disco bajo");
                }
            }

            if (device.LastBootAtUtc.HasValue && (observedAtUtc - device.LastBootAtUtc.Value).TotalDays >= 45)
            {
                score += 8;
                reasons.Add("Uptime prolongado");
            }

            if (device.PingMs.HasValue && device.PingMs.Value >= 250)
            {
                score += 5;
                reasons.Add("Latencia elevada");
            }
        }
        else
        {
            var openPorts = ParseOpenPorts(device.OpenPorts);
            if (openPorts.Contains(3389))
            {
                score += 10;
                reasons.Add("RDP expuesto");
            }

            if (openPorts.Contains(445) || openPorts.Contains(139) || openPorts.Contains(135))
            {
                score += 8;
                reasons.Add("Servicios SMB/WMI expuestos");
            }

            if (openPorts.Contains(22))
            {
                score += 6;
                reasons.Add("SSH expuesto");
            }

            if (openPorts.Contains(9100) || openPorts.Contains(631) || openPorts.Contains(515))
            {
                score += 4;
                reasons.Add("Servicio de impresion visible");
            }

            if (string.IsNullOrWhiteSpace(device.HostName) && string.IsNullOrWhiteSpace(device.DeviceName))
            {
                score += 4;
                reasons.Add("Sin nombre resolvible");
            }

            if (device.PingMs.HasValue && device.PingMs.Value >= 250)
            {
                score += 4;
                reasons.Add("Latencia elevada");
            }
        }

        var missingIdentifiers = 0;
        if (string.IsNullOrWhiteSpace(device.SerialNumber)) missingIdentifiers++;
        if (string.IsNullOrWhiteSpace(device.IpAddress)) missingIdentifiers++;
        if (string.IsNullOrWhiteSpace(device.MacAddress)) missingIdentifiers++;
        if (!liveScanMode && missingIdentifiers >= 2)
        {
            score += 10;
            reasons.Add("Identificadores incompletos");
        }

        score = Math.Min(score, 100);
        var riskLevel = ToRiskLevel(score);
        var resolvedUsername = ResolveDeviceUsername(device, importedMatch, syncedMatch);
        var resolvedNetworkProfile = string.IsNullOrWhiteSpace(device.NetworkProfile)
            ? InferNetworkProfile(device.DeviceCategory, device.OpenPorts, device.HostName, device.DeviceName)
            : device.NetworkProfile;

        return new ObservationResult(
            ExternalKey: string.IsNullOrWhiteSpace(device.ExternalKey) ? BuildFallbackExternalKey(device) : device.ExternalKey,
            DeviceName: device.DeviceName,
            Username: resolvedUsername,
            Domain: device.Domain,
            IpAddress: device.IpAddress,
            MacAddress: device.MacAddress,
            SerialNumber: device.SerialNumber,
            HostName: device.HostName,
            DeviceCategory: device.DeviceCategory,
            OperatingSystem: device.OperatingSystem,
            OperatingSystemVersion: device.OperatingSystemVersion,
            Manufacturer: device.Manufacturer,
            Model: device.Model,
            Processor: device.Processor,
            MemoryGb: device.MemoryGb,
            DiskTotalGb: device.DiskTotalGb,
            DiskFreeGb: device.DiskFreeGb,
            LastBootAtUtc: device.LastBootAtUtc,
            IsOnline: device.IsOnline,
            DomainJoined: device.DomainJoined,
            IsVirtualMachine: device.IsVirtualMachine,
            PingMs: device.PingMs,
            AntivirusStatus: device.AntivirusStatus,
            AntivirusVersion: device.AntivirusVersion,
            PatchStatus: device.PatchStatus,
            AgentVersion: device.AgentVersion,
            OpenPorts: device.OpenPorts,
            SubnetCidr: device.SubnetCidr,
            NetworkProfile: resolvedNetworkProfile,
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

    private static ISet<int> ParseOpenPorts(string? value)
    {
        var ports = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return ports;
        }

        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, out var port) && port > 0 && port < 65536)
            {
                ports.Add(port);
            }
        }

        return ports;
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

        if (authMatch is null && LooksLikeHumanIdentity(user.Username))
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
            DeviceCategory: string.Empty,
            OperatingSystem: string.Empty,
            OperatingSystemVersion: string.Empty,
            Manufacturer: string.Empty,
            Model: string.Empty,
            Processor: string.Empty,
            MemoryGb: null,
            DiskTotalGb: null,
            DiskFreeGb: null,
            LastBootAtUtc: null,
            IsOnline: null,
            DomainJoined: null,
            IsVirtualMachine: null,
            PingMs: null,
            AntivirusStatus: string.Empty,
            AntivirusVersion: string.Empty,
            PatchStatus: string.Empty,
            AgentVersion: string.Empty,
            OpenPorts: string.Empty,
            SubnetCidr: string.Empty,
            NetworkProfile: string.Empty,
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
            DeviceCategory: Clean(input.DeviceCategory),
            OperatingSystem: Clean(input.OperatingSystem),
            OperatingSystemVersion: Clean(input.OperatingSystemVersion),
            Manufacturer: Clean(input.Manufacturer),
            Model: Clean(input.Model),
            Processor: Clean(input.Processor),
            MemoryGb: input.MemoryGb,
            DiskTotalGb: input.DiskTotalGb,
            DiskFreeGb: input.DiskFreeGb,
            LastBootAtUtc: input.LastBootAtUtc,
            IsOnline: input.IsOnline,
            DomainJoined: input.DomainJoined,
            IsVirtualMachine: input.IsVirtualMachine,
            PingMs: input.PingMs,
            AntivirusStatus: Clean(input.AntivirusStatus),
            AntivirusVersion: Clean(input.AntivirusVersion),
            PatchStatus: Clean(input.PatchStatus),
            AgentVersion: Clean(input.AgentVersion),
            OpenPorts: Clean(input.OpenPorts),
            SubnetCidr: Clean(input.SubnetCidr),
            NetworkProfile: Clean(input.NetworkProfile),
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
            DeviceCategory = observation.DeviceCategory,
            OperatingSystem = observation.OperatingSystem,
            OperatingSystemVersion = observation.OperatingSystemVersion,
            Manufacturer = observation.Manufacturer,
            Model = observation.Model,
            Processor = observation.Processor,
            MemoryGb = observation.MemoryGb,
            DiskTotalGb = observation.DiskTotalGb,
            DiskFreeGb = observation.DiskFreeGb,
            LastBootAtUtc = observation.LastBootAtUtc,
            IsOnline = observation.IsOnline,
            DomainJoined = observation.DomainJoined,
            IsVirtualMachine = observation.IsVirtualMachine,
            PingMs = observation.PingMs,
            AntivirusStatus = observation.AntivirusStatus,
            AntivirusVersion = observation.AntivirusVersion,
            PatchStatus = observation.PatchStatus,
            AgentVersion = observation.AgentVersion,
            OpenPorts = observation.OpenPorts,
            SubnetCidr = observation.SubnetCidr,
            NetworkProfile = observation.NetworkProfile,
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

    private static async Task<IReadOnlyList<NetworkTelemetryBuildingRiskSummaryViewModel>> BuildBuildingRiskSummariesAsync(
        IQueryable<NetworkTelemetryObservation> query,
        CancellationToken cancellationToken)
    {
        return await query
            .Where(observation => observation.ObservationType == "device" && observation.BuildingExternalId != string.Empty)
            .GroupBy(observation => observation.BuildingExternalId)
            .Select(group => new NetworkTelemetryBuildingRiskSummaryViewModel
            {
                BuildingExternalId = group.Key,
                DeviceCount = group.Count(),
                CriticalCount = group.Count(item => item.RiskLevel == "critical"),
                HighCount = group.Count(item => item.RiskLevel == "high"),
                MediumCount = group.Count(item => item.RiskLevel == "medium"),
                LowCount = group.Count(item => item.RiskLevel == "low"),
                MaxRiskScore = group.Max(item => item.RiskScore),
                MaxRiskLevel = group
                    .OrderByDescending(item => item.RiskScore)
                    .ThenByDescending(item => item.Id)
                    .Select(item => item.RiskLevel)
                    .FirstOrDefault() ?? "low"
            })
            .OrderByDescending(item => item.MaxRiskScore)
            .ThenByDescending(item => item.DeviceCount)
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<NetworkTelemetrySubnetRiskSummaryViewModel>> BuildSubnetRiskSummariesAsync(
        IQueryable<NetworkTelemetryObservation> query,
        CancellationToken cancellationToken)
    {
        return await query
            .Where(observation => observation.ObservationType == "device" && observation.SubnetCidr != string.Empty)
            .GroupBy(observation => observation.SubnetCidr)
            .Select(group => new NetworkTelemetrySubnetRiskSummaryViewModel
            {
                SubnetCidr = group.Key,
                DeviceCount = group.Count(),
                CriticalCount = group.Count(item => item.RiskLevel == "critical"),
                HighCount = group.Count(item => item.RiskLevel == "high"),
                MediumCount = group.Count(item => item.RiskLevel == "medium"),
                LowCount = group.Count(item => item.RiskLevel == "low"),
                MaxRiskScore = group.Max(item => item.RiskScore),
                MaxRiskLevel = group
                    .OrderByDescending(item => item.RiskScore)
                    .ThenByDescending(item => item.Id)
                    .Select(item => item.RiskLevel)
                    .FirstOrDefault() ?? "low"
            })
            .OrderByDescending(item => item.MaxRiskScore)
            .ThenByDescending(item => item.DeviceCount)
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NetworkTelemetrySubnetRiskSummaryViewModel>> GetSubnetRiskSummariesAsync(
        int snapshotId,
        CancellationToken cancellationToken = default)
    {
        var query = _context.NetworkTelemetryObservations
            .AsNoTracking()
            .Where(observation => observation.NetworkTelemetrySnapshotId == snapshotId && observation.ObservationType == "device");

        return await BuildSubnetRiskSummariesAsync(query, cancellationToken);
    }

    private sealed record InventoryMatchRecord(
        int Id,
        string SerialNumber,
        string IpAddress,
        string MacAddress,
        string ResponsibleUser,
        string Email,
        string UnitOrDepartment,
        string OrganizationalUnit,
        string AssignedBuildingExternalId,
        string AssignedRoomExternalId);

    private sealed record SyncedEquipmentMatchRecord(
        int Id,
        string SerialNumber,
        string IpAddress,
        string MacAddress,
        string AssignedTo,
        string ResponsiblePerson,
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

    private sealed record LinkedImportedRow(
        string? ResponsibleUser,
        string? Email,
        string? UnitOrDepartment,
        string? OrganizationalUnit);

    private sealed record LinkedSyncedRow(
        string? AssignedTo,
        string? ResponsiblePerson,
        string? Name,
        string? InventoryCode);

    private sealed record DeviceCandidate(
        string ExternalKey,
        string DeviceName,
        string Username,
        string Domain,
        string IpAddress,
        string MacAddress,
        string SerialNumber,
        string HostName,
        string DeviceCategory,
        string OperatingSystem,
        string OperatingSystemVersion,
        string Manufacturer,
        string Model,
        string Processor,
        double? MemoryGb,
        double? DiskTotalGb,
        double? DiskFreeGb,
        DateTime? LastBootAtUtc,
        bool? IsOnline,
        bool? DomainJoined,
        bool? IsVirtualMachine,
        int? PingMs,
        string AntivirusStatus,
        string AntivirusVersion,
        string PatchStatus,
        string AgentVersion,
        string OpenPorts,
        string SubnetCidr,
        string NetworkProfile,
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
        string DeviceCategory,
        string OperatingSystem,
        string OperatingSystemVersion,
        string Manufacturer,
        string Model,
        string Processor,
        double? MemoryGb,
        double? DiskTotalGb,
        double? DiskFreeGb,
        DateTime? LastBootAtUtc,
        bool? IsOnline,
        bool? DomainJoined,
        bool? IsVirtualMachine,
        int? PingMs,
        string AntivirusStatus,
        string AntivirusVersion,
        string PatchStatus,
        string AgentVersion,
        string OpenPorts,
        string SubnetCidr,
        string NetworkProfile,
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
