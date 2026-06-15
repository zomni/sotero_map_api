using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Models;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Services;

public class NetworkTelemetryService
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;

    public NetworkTelemetryService(AppDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    public bool IsEnabled()
        => _configuration.GetValue<bool?>("NetworkTelemetrySettings:Enabled") ?? true;

    public int FreshnessMinutes()
        => _configuration.GetValue<int?>("NetworkTelemetrySettings:FreshnessMinutes") ?? 30;

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
            RecentSnapshots = snapshots.Select(MapSnapshot).ToList()
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
}
