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
        _scheduleTimeZone = TelemetryScanScheduleService.ResolveTimeZone(configuration["NetworkTelemetrySettings:AutoScanTimeZone"]);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = GetBool("NetworkTelemetrySettings:AutoScanEnabled", "NETWORK_TELEMETRY_AUTO_SCAN_ENABLED", true);
        if (!enabled)
        {
            _logger.LogInformation("Live network telemetry scheduler disabled by configuration.");
            return;
        }

        await RecoverOrphanedRunsAsync(stoppingToken);

        var lastWatchdogUtc = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTime scheduledAtUtc;
            string matchedCron = string.Empty;
            string matchedLabel = string.Empty;
            try
            {
                var schedules = await LoadActiveSchedulesAsync(stoppingToken);
                var result = GetDelayUntilNextRun(schedules);
                var delay = result.Delay;
                scheduledAtUtc = result.NextScheduledUtc;
                matchedCron = result.MatchedCron;
                matchedLabel = result.MatchedLabel;
                if (schedules.Count == 0)
                {
                    _logger.LogWarning(
                        "No active telemetry scan schedules found. Falling back to interval mode every {Minutes} minutes.",
                        GetInt("NetworkTelemetrySettings:AutoScanIntervalMinutes", "NETWORK_TELEMETRY_AUTO_SCAN_INTERVAL_MINUTES", 30));
                }
                else if (delay > TimeSpan.Zero)
                {
                    _logger.LogInformation("Next live telemetry scan scheduled in {Delay}.", delay);
                }

                if (delay > TimeSpan.Zero)
                {
                    var pollInterval = TimeSpan.FromSeconds(30);
                    var remaining = delay;
                    while (remaining > TimeSpan.Zero && !stoppingToken.IsCancellationRequested)
                    {
                        var sleepDuration = remaining > pollInterval ? pollInterval : remaining;
                        await Task.Delay(sleepDuration, stoppingToken);
                        remaining -= sleepDuration;

                        if (remaining > TimeSpan.Zero)
                        {
                            var freshSchedules = await LoadActiveSchedulesAsync(stoppingToken);
                            var freshResult = GetDelayUntilNextRun(freshSchedules);
                            if (freshResult.Delay > TimeSpan.Zero && freshResult.NextScheduledUtc < scheduledAtUtc)
                            {
                                _logger.LogInformation(
                                    "Re-evaluated schedules: earlier run detected at {FreshScheduledUtc} (was {ScheduledAtUtc}). Waking early.",
                                    freshResult.NextScheduledUtc, scheduledAtUtc);
                                scheduledAtUtc = freshResult.NextScheduledUtc;
                                matchedCron = freshResult.MatchedCron;
                                matchedLabel = freshResult.MatchedLabel;
                                break;
                            }
                        }
                    }
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

            if (DateTime.UtcNow - lastWatchdogUtc > TimeSpan.FromMinutes(5))
            {
                await MarkStuckRunsAsFailedAsync(stoppingToken);
                lastWatchdogUtc = DateTime.UtcNow;
            }

            ScheduledScanRun? currentRun = null;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var bridge = scope.ServiceProvider.GetRequiredService<NetworkTelemetryAgentBridgeService>();

                var nowUtc = DateTime.UtcNow;
                var scheduleLabel = matchedLabel;
                var normalizedCron = matchedCron;

                var existingRunForSlot = await db.ScheduledScanRuns
                    .Where(r => r.ScheduledAtUtc == scheduledAtUtc && r.Status != "failed")
                    .OrderByDescending(r => r.CreatedAtUtc)
                    .FirstOrDefaultAsync(stoppingToken);
                if (existingRunForSlot is not null)
                {
                    _logger.LogWarning(
                        "Skipping duplicate scheduled scan for {ScheduledAtUtc}: run #{Id} already exists with status {Status}.",
                        scheduledAtUtc, existingRunForSlot.Id, existingRunForSlot.Status);
                    continue;
                }

                var scheduledLocal = TimeZoneInfo.ConvertTime(scheduledAtUtc, _scheduleTimeZone);

                var run = new ScheduledScanRun
                {
                    ScheduledAtUtc = scheduledAtUtc,
                    StartedAtUtc = nowUtc,
                    Status = "running",
                    ScheduledTimeLocal = scheduledLocal.ToString("HH:mm"),
                    ScheduledDayLocal = scheduledLocal.ToString("dddd", new System.Globalization.CultureInfo("es-CL")),
                    NormalizedCron = normalizedCron,
                    ScheduleLabel = scheduleLabel,
                    CreatedAtUtc = nowUtc
                };
                db.ScheduledScanRuns.Add(run);
                await db.SaveChangesAsync(stoppingToken);
                currentRun = run;

                var scanner = scope.ServiceProvider.GetRequiredService<NetworkTelemetryLiveScanService>();

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

                    var previousQueuedRun = await db.ScheduledScanRuns
                        .Where(r => r.Status == "queued" && r.CompletedAtUtc == null)
                        .OrderByDescending(r => r.CreatedAtUtc)
                        .FirstOrDefaultAsync(stoppingToken);

                    var shouldFallbackToInline = !agentStatus.IsConnected
                        || previousQueuedRun is not null;

                    if (shouldFallbackToInline)
                    {
                        var reason = !agentStatus.IsConnected
                            ? $"Agent disconnected (state={agentStatus.State})"
                            : $"Previous scan run #{previousQueuedRun!.Id} is still queued without completion";

                        _logger.LogWarning(
                            "Falling back to inline scan. Reason: {Reason}",
                            reason);

                        var result = await scanner.ScanAndStoreAsync("system", new NetworkTelemetryLiveScanRequest
                        {
                            ResolveInteractiveSessions = true,
                            ScanMode = "full",
                            TriggerType = "scheduled"
                        }, stoppingToken);
                        _logger.LogInformation("Live network telemetry auto scan completed inline (agent bypassed).");

                        run.Status = "completed";
                        run.CompletedAtUtc = DateTime.UtcNow;
                        run.SnapshotId = result.SnapshotId;
                        run.DeviceCount = result.DeviceCount;
                        run.UserCount = result.UserCount;
                        await db.SaveChangesAsync(stoppingToken);
                        continue;
                    }

                    await bridge.QueueScanAsync("system", new NetworkTelemetryLiveScanRequest
                    {
                        ResolveInteractiveSessions = true,
                        ScanMode = "full",
                        TriggerType = "scheduled"
                    }, stoppingToken);
                    _logger.LogInformation("Live network telemetry auto scan queued for Windows agent.");

                    run.Status = "queued";
                    await db.SaveChangesAsync(stoppingToken);
                }
                else
                {
                    var result = await scanner.ScanAndStoreAsync("system", new NetworkTelemetryLiveScanRequest
                    {
                        ResolveInteractiveSessions = true,
                        ScanMode = "full",
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
                if (currentRun is not null)
                {
                    await MarkRunAsFailedAsync(currentRun.Id, "Cancelado por apagado del servicio", stoppingToken);
                }
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Live network telemetry scan failed.");
                if (currentRun is not null)
                {
                    await MarkRunAsFailedAsync(currentRun.Id, ex.Message, stoppingToken);
                }
                else
                {
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
    }

    private async Task<IReadOnlyList<ActiveSchedule>> LoadActiveSchedulesAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dbSchedules = await db.TelemetryScanSchedules
            .AsNoTracking()
            .Where(s => s.IsEnabled && s.DeletedAtUtc == null)
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.CreatedAtUtc)
            .Select(s => new ActiveSchedule(s.Cron, s.TimeZone, s.Label))
            .ToListAsync(stoppingToken);

        if (dbSchedules.Count > 0)
        {
            return dbSchedules;
        }

        var hasAnyRecords = await db.TelemetryScanSchedules.AnyAsync(stoppingToken);
        if (!hasAnyRecords)
        {
            var seeded = await SeedFromConfigAsync(db, stoppingToken);
            if (seeded)
            {
                dbSchedules = await db.TelemetryScanSchedules
                    .AsNoTracking()
                    .Where(s => s.IsEnabled && s.DeletedAtUtc == null)
                    .OrderBy(s => s.SortOrder)
                    .ThenBy(s => s.CreatedAtUtc)
                    .Select(s => new ActiveSchedule(s.Cron, s.TimeZone, s.Label))
                    .ToListAsync(stoppingToken);
            }
        }

        return dbSchedules;
    }

    private async Task<bool> SeedFromConfigAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var configured = _configuration["NetworkTelemetrySettings:AutoScanCrons"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = _configuration["NetworkTelemetrySettings:AutoScanCron"];
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        var tzId = _configuration["NetworkTelemetrySettings:AutoScanTimeZone"] ?? "America/Santiago";
        var cronEntries = configured.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var seeded = false;

        foreach (var cron in cronEntries)
        {
            if (!TelemetryScanScheduleService.IsValidCompoundCron(cron))
            {
                continue;
            }

            var slots = TelemetryScanScheduleService.ParseSlotsFromCron(cron);
            if (slots.Count == 0)
            {
                continue;
            }

            var label = TelemetryScanScheduleService.GenerateLabelFromSlots(slots);

            var schedule = new TelemetryScanSchedule
            {
                Cron = cron,
                Label = label,
                TimeZone = TelemetryScanScheduleService.ResolveScheduleTimeZone(tzId),
                IsEnabled = true,
                SortOrder = db.TelemetryScanSchedules.Count(),
                CreatedAtUtc = DateTime.UtcNow
            };

            db.TelemetryScanSchedules.Add(schedule);
            seeded = true;
        }

        if (seeded)
        {
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Seeded {Count} scan schedule(s) from appsettings.", cronEntries.Length);
        }

        return seeded;
    }

    private DelayResult GetDelayUntilNextRun(IReadOnlyList<ActiveSchedule> schedules)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var candidates = new List<(DateTimeOffset Occurrence, string Cron, string Label)>();

        _logger.LogInformation("GetDelayUntilNextRun: nowUtc={NowUtc}, schedules count={Count}", nowUtc.UtcDateTime, schedules.Count);

        foreach (var schedule in schedules)
        {
            var timeZone = TelemetryScanScheduleService.ResolveTimeZone(schedule.TimeZone);
            var cronParts = schedule.Cron.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in cronParts)
            {
                if (!TelemetryScanScheduleService.TryParseCron(part, out var expression) || expression is null)
                {
                    _logger.LogWarning("GetDelayUntilNextRun: cron PARSE FAILED for part={Part} (schedule={Label})", part, schedule.Label);
                    continue;
                }

                var nextUtc = expression.GetNextOccurrence(nowUtc.UtcDateTime, timeZone);
                if (nextUtc is null)
                {
                    _logger.LogWarning("GetDelayUntilNextRun: GetNextOccurrence returned NULL for part={Part} tz={Tz}", part, timeZone.Id);
                    continue;
                }

                _logger.LogInformation("GetDelayUntilNextRun: candidate part={Part} tz={Tz} nextUtc={NextUtc}", part, timeZone.Id, nextUtc.Value);
                candidates.Add((new DateTimeOffset(nextUtc.Value, TimeSpan.Zero), part, schedule.Label));
            }
        }

        if (candidates.Count > 0)
        {
            var best = candidates.OrderBy(c => c.Occurrence).First();
            var delay = best.Occurrence - nowUtc;
            _logger.LogInformation("GetDelayUntilNextRun: chosen nextUtc={NextUtc} delay={Delay} cron={Cron} label={Label}", best.Occurrence.UtcDateTime, delay, best.Cron, best.Label);
            return new DelayResult(
                delay > TimeSpan.Zero ? delay : TimeSpan.Zero,
                best.Occurrence.UtcDateTime,
                best.Cron,
                best.Label);
        }

        var intervalMinutes = GetInt("NetworkTelemetrySettings:AutoScanIntervalMinutes", "NETWORK_TELEMETRY_AUTO_SCAN_INTERVAL_MINUTES", 30);
        if (intervalMinutes <= 0)
        {
            intervalMinutes = 30;
        }

        return new DelayResult(
            TimeSpan.FromMinutes(intervalMinutes),
            nowUtc.UtcDateTime.AddMinutes(intervalMinutes),
            string.Empty,
            string.Empty);
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

    private async Task RecoverOrphanedRunsAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var orphaned = await db.ScheduledScanRuns
                .Where(r => (r.Status == "running" || r.Status == "queued") && r.CompletedAtUtc == null)
                .ToListAsync(stoppingToken);

            foreach (var run in orphaned)
            {
                _logger.LogWarning(
                    "Recovering orphaned scan run #{Id} (status={Status}, scheduled={ScheduledAtUtc}).",
                    run.Id, run.Status, run.ScheduledAtUtc);
                run.Status = "failed";
                run.CompletedAtUtc = DateTime.UtcNow;
                run.ErrorMessage = "Cancelado por reinicio del servicio";
            }

            if (orphaned.Count > 0)
            {
                await db.SaveChangesAsync(stoppingToken);
                _logger.LogInformation("Recovered {Count} orphaned scan run(s) on startup.", orphaned.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover orphaned scan runs on startup.");
        }
    }

    private async Task MarkStuckRunsAsFailedAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var threshold = DateTime.UtcNow.AddMinutes(-30);

            var stuck = await db.ScheduledScanRuns
                .Where(r => (r.Status == "running" || r.Status == "queued")
                         && r.StartedAtUtc != null
                         && r.StartedAtUtc < threshold)
                .ToListAsync(stoppingToken);

            foreach (var run in stuck)
            {
                _logger.LogWarning(
                    "Watchdog: marking stuck scan run #{Id} as failed (status={Status}, started={StartedAtUtc}).",
                    run.Id, run.Status, run.StartedAtUtc);
                run.Status = "failed";
                run.CompletedAtUtc = DateTime.UtcNow;
                run.ErrorMessage = $"Expirado por watchdog (más de 30 minutos en estado '{run.Status}')";
            }

            if (stuck.Count > 0)
            {
                await db.SaveChangesAsync(stoppingToken);
                _logger.LogInformation("Watchdog marked {Count} stuck scan run(s) as failed.", stuck.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watchdog failed to update stuck scan runs.");
        }
    }

    private async Task MarkRunAsFailedAsync(int runId, string errorMessage, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = await db.ScheduledScanRuns.FindAsync(new object[] { runId }, stoppingToken);
            if (run is not null && run.Status != "failed" && run.Status != "completed")
            {
                run.Status = "failed";
                run.CompletedAtUtc = DateTime.UtcNow;
                run.ErrorMessage = errorMessage;
                await db.SaveChangesAsync(stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark run #{RunId} as failed.", runId);
        }
    }

    private readonly record struct ActiveSchedule(string Cron, string TimeZone, string Label);

    private readonly record struct DelayResult(TimeSpan Delay, DateTime NextScheduledUtc, string MatchedCron, string MatchedLabel);
}
