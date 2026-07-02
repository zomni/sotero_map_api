using Cronos;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Models;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Services;

public class NetworkTelemetryLiveScanHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NetworkTelemetryLiveScanHostedService> _logger;
    private readonly IConfiguration _configuration;
    private readonly TimeZoneInfo _scheduleTimeZone;

    public NetworkTelemetryLiveScanHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<NetworkTelemetryLiveScanHostedService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
        _scheduleTimeZone = ResolveTimeZone(configuration["NetworkTelemetrySettings:AutoScanTimeZone"]);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = GetBool("NetworkTelemetrySettings:AutoScanEnabled", "NETWORK_TELEMETRY_AUTO_SCAN_ENABLED", true);
        if (!enabled)
        {
            _logger.LogInformation("Live network telemetry scheduler disabled by configuration.");
            return;
        }

        var configuredSchedules = GetCronExpressions();
        if (configuredSchedules.Count == 0)
        {
            _logger.LogWarning(
                "No valid live telemetry cron expressions were found. Falling back to interval mode every {Minutes} minutes.",
                GetInt("NetworkTelemetrySettings:AutoScanIntervalMinutes", "NETWORK_TELEMETRY_AUTO_SCAN_INTERVAL_MINUTES", 30));
        }
        else
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var nextOccurrenceUtc = configuredSchedules
                .Select(expression => expression.GetNextOccurrence(nowUtc.UtcDateTime, _scheduleTimeZone))
                .Where(next => next.HasValue)
                .Select(next => new DateTimeOffset(
                    TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(next!.Value, DateTimeKind.Unspecified),
                        _scheduleTimeZone)))
                .OrderBy(next => next)
                .FirstOrDefault();

            if (nextOccurrenceUtc != default)
            {
                var nextLocal = TimeZoneInfo.ConvertTime(nextOccurrenceUtc, _scheduleTimeZone);
                _logger.LogInformation(
                    "Live telemetry scheduler active. Next scheduled run at {NextLocal} ({TimeZone}).",
                    nextLocal.ToString("yyyy-MM-dd HH:mm:ss"),
                    _scheduleTimeZone.Id);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTime scheduledAtUtc;
            try
            {
                var delay = GetDelayUntilNextRun(out var nextScheduledUtc);
                scheduledAtUtc = nextScheduledUtc;
                if (delay > TimeSpan.Zero)
                {
                    _logger.LogInformation("Next live telemetry scan scheduled in {Delay}.", delay);
                    await Task.Delay(delay, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var bridge = scope.ServiceProvider.GetRequiredService<NetworkTelemetryAgentBridgeService>();

                var nowUtc = DateTime.UtcNow;
                var scheduledLocal = TimeZoneInfo.ConvertTime(scheduledAtUtc, _scheduleTimeZone);
                var normalizedCron = string.Join(";", GetCronExpressions().Select(c => c.ToString()));

                var run = new ScheduledScanRun
                {
                    ScheduledAtUtc = scheduledAtUtc,
                    StartedAtUtc = nowUtc,
                    Status = "running",
                    ScheduledTimeLocal = scheduledLocal.ToString("HH:mm"),
                    ScheduledDayLocal = scheduledLocal.ToString("dddd", new System.Globalization.CultureInfo("es-CL")),
                    NormalizedCron = normalizedCron,
                    CreatedAtUtc = nowUtc
                };
                db.ScheduledScanRuns.Add(run);
                await db.SaveChangesAsync(stoppingToken);

                if (bridge.UseAgentMode())
                {
                    var agentStatus = await bridge.GetStatusAsync(stoppingToken);
                    if (string.Equals(agentStatus.State, "pending", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(agentStatus.State, "running", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(agentStatus.State, "paused", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(agentStatus.State, "stopping", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Skipping automatic telemetry queue because agent is currently {State}.", agentStatus.State);
                        run.Status = "skipped";
                        run.CompletedAtUtc = DateTime.UtcNow;
                        run.ErrorMessage = $"Agente ocupado (estado: {agentStatus.State})";
                        await db.SaveChangesAsync(stoppingToken);
                        continue;
                    }

                    await bridge.QueueScanAsync("system", new NetworkTelemetryLiveScanRequest
                    {
                        ResolveInteractiveSessions = true,
                        ScanMode = "simple",
                        TriggerType = "scheduled"
                    }, stoppingToken);
                    _logger.LogInformation("Live network telemetry auto scan queued for Windows agent.");

                    run.Status = "queued";
                    await db.SaveChangesAsync(stoppingToken);
                }
                else
                {
                    var scanner = scope.ServiceProvider.GetRequiredService<NetworkTelemetryLiveScanService>();
                    var result = await scanner.ScanAndStoreAsync("system", new NetworkTelemetryLiveScanRequest
                    {
                        ResolveInteractiveSessions = true,
                        ScanMode = "simple",
                        TriggerType = "scheduled"
                    }, stoppingToken);
                    _logger.LogInformation("Live network telemetry scan completed successfully.");

                    run.Status = "completed";
                    run.CompletedAtUtc = DateTime.UtcNow;
                    run.SnapshotId = result.SnapshotId;
                    run.DeviceCount = result.DeviceCount;
                    run.UserCount = result.UserCount;
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Live network telemetry scan failed.");
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var failedRun = await db.ScheduledScanRuns
                        .OrderByDescending(r => r.CreatedAtUtc)
                        .FirstOrDefaultAsync(r => r.Status == "running", stoppingToken);
                    if (failedRun != null)
                    {
                        failedRun.Status = "failed";
                        failedRun.CompletedAtUtc = DateTime.UtcNow;
                        failedRun.ErrorMessage = ex.Message;
                        await db.SaveChangesAsync(stoppingToken);
                    }
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, "Failed to update scheduled scan run status.");
                }
            }
        }
    }

    private TimeSpan GetDelayUntilNextRun(out DateTime nextScheduledUtc)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var schedules = GetCronExpressions();
        var nextOccurrenceUtc = schedules
            .Select(expression => expression.GetNextOccurrence(nowUtc.UtcDateTime, _scheduleTimeZone))
            .Where(next => next.HasValue)
            .Select(next => new DateTimeOffset(
                TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(next!.Value, DateTimeKind.Unspecified),
                    _scheduleTimeZone)))
            .OrderBy(next => next)
            .FirstOrDefault();

        if (nextOccurrenceUtc != default)
        {
            nextScheduledUtc = nextOccurrenceUtc.UtcDateTime;
            var delay = nextOccurrenceUtc - nowUtc;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        var intervalMinutes = GetInt("NetworkTelemetrySettings:AutoScanIntervalMinutes", "NETWORK_TELEMETRY_AUTO_SCAN_INTERVAL_MINUTES", 30);
        if (intervalMinutes <= 0)
        {
            intervalMinutes = 30;
        }

        nextScheduledUtc = nowUtc.UtcDateTime.AddMinutes(intervalMinutes);
        return TimeSpan.FromMinutes(intervalMinutes);
    }

    private IReadOnlyList<CronExpression> GetCronExpressions()
    {
        var configured = GetString("NetworkTelemetrySettings:AutoScanCrons", "NETWORK_TELEMETRY_AUTO_SCAN_CRONS");
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = GetString("NetworkTelemetrySettings:AutoScanCron", "NETWORK_TELEMETRY_AUTO_SCAN_CRON");
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            return Array.Empty<CronExpression>();
        }

        return configured
            .Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => TryParseCron(value, out var expression) ? expression : null)
            .Where(expression => expression is not null)
            .Cast<CronExpression>()
            .ToList();
    }

    private static bool TryParseCron(string cron, out CronExpression? expression)
    {
        if (CronExpression.TryParse(cron, CronFormat.IncludeSeconds, out expression))
        {
            return true;
        }

        return CronExpression.TryParse(cron, CronFormat.Standard, out expression);
    }

    private static TimeZoneInfo ResolveTimeZone(string? configuredTimeZone)
    {
        if (!string.IsNullOrWhiteSpace(configuredTimeZone))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(configuredTimeZone);
            }
            catch
            {
            }
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }

    private string? GetString(string configKey, string envKey)
        => Environment.GetEnvironmentVariable(envKey) ?? _configuration[configKey];

    private int GetInt(string configKey, string envKey, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envKey);
        if (int.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return int.TryParse(_configuration[configKey], out parsed) ? parsed : fallback;
    }

    private bool GetBool(string configKey, string envKey, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envKey);
        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return bool.TryParse(_configuration[configKey], out parsed) ? parsed : fallback;
    }
}
