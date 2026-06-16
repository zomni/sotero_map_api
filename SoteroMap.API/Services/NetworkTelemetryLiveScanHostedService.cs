using Cronos;

namespace SoteroMap.API.Services;

public class NetworkTelemetryLiveScanHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NetworkTelemetryLiveScanHostedService> _logger;
    private readonly IConfiguration _configuration;

    public NetworkTelemetryLiveScanHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<NetworkTelemetryLiveScanHostedService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = GetBool("NetworkTelemetrySettings:AutoScanEnabled", "NETWORK_TELEMETRY_AUTO_SCAN_ENABLED", true);
        if (!enabled)
        {
            _logger.LogInformation("Live network telemetry scheduler disabled by configuration.");
            return;
        }

        var firstRun = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!firstRun)
            {
                try
                {
                    var delay = GetDelayUntilNextRun();
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
            }

            firstRun = false;

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var scanner = scope.ServiceProvider.GetRequiredService<NetworkTelemetryLiveScanService>();
                await scanner.ScanAndStoreAsync("system", null, stoppingToken);
                _logger.LogInformation("Live network telemetry scan completed successfully.");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Live network telemetry scan failed.");
            }
        }
    }

    private TimeSpan GetDelayUntilNextRun()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var cron = GetString("NetworkTelemetrySettings:AutoScanCron", "NETWORK_TELEMETRY_AUTO_SCAN_CRON");
        if (!string.IsNullOrWhiteSpace(cron))
        {
            if (TryParseCron(cron, out var expression))
            {
                var next = expression?.GetNextOccurrence(nowUtc.UtcDateTime, TimeZoneInfo.Utc);
                if (next.HasValue)
                {
                    var delay = next.Value - nowUtc.UtcDateTime;
                    return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
                }
            }
        }

        var intervalMinutes = GetInt("NetworkTelemetrySettings:AutoScanIntervalMinutes", "NETWORK_TELEMETRY_AUTO_SCAN_INTERVAL_MINUTES", 30);
        if (intervalMinutes <= 0)
        {
            intervalMinutes = 30;
        }

        return TimeSpan.FromMinutes(intervalMinutes);
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
