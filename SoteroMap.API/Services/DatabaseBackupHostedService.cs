using Cronos;

namespace SoteroMap.API.Services;

public class DatabaseBackupHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseBackupHostedService> _logger;
    private readonly IConfiguration _configuration;

    public DatabaseBackupHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseBackupHostedService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = GetBool("BackupSettings:Enabled", "BACKUP_ENABLED", true);
        if (!enabled)
        {
            _logger.LogInformation("Database backup scheduler disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = GetDelayUntilNextRun();
                if (delay > TimeSpan.Zero)
                {
                    _logger.LogInformation("Next database backup scheduled in {Delay}.", delay);
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
                var backupService = scope.ServiceProvider.GetRequiredService<DatabaseBackupService>();
                await backupService.CreateBackupAsync("sistema", "scheduled-backup", stoppingToken);
                _logger.LogInformation("Scheduled database backup created successfully.");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled database backup failed.");
            }
        }
    }

    private TimeSpan GetDelayUntilNextRun()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var cron = GetString("BackupSettings:Cron", "BACKUP_CRON");
        if (!string.IsNullOrWhiteSpace(cron))
        {
            if (TryParseCron(cron, out var expression))
            {
                DateTime? next = expression?.GetNextOccurrence(nowUtc.UtcDateTime, TimeZoneInfo.Utc);
                if (next.HasValue)
                {
                    var delay = next.Value - nowUtc.UtcDateTime;
                    return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
                }
            }
        }

        var intervalHours = GetInt("BackupSettings:IntervalHours", "BACKUP_INTERVAL_HOURS", 24);
        if (intervalHours <= 0)
        {
            intervalHours = 24;
        }

        return TimeSpan.FromHours(intervalHours);
    }

    private static bool TryParseCron(string cron, out CronExpression? expression)
    {
        if (CronExpression.TryParse(cron, CronFormat.IncludeSeconds, out expression))
        {
            return true;
        }

        return CronExpression.TryParse(cron, CronFormat.Standard, out expression);
    }

    private string? GetString(string configKey, string envKey)
    {
        return Environment.GetEnvironmentVariable(envKey) ?? _configuration[configKey];
    }

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
