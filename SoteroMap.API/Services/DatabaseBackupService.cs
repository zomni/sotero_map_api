using System.Security.Cryptography;
using Cronos;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Infrastructure;
using SoteroMap.API.Models;

namespace SoteroMap.API.Services;

public class DatabaseBackupService
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly AuditLogService _auditLogService;

    public DatabaseBackupService(
        AppDbContext context,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        AuditLogService auditLogService)
    {
        _context = context;
        _configuration = configuration;
        _environment = environment;
        _auditLogService = auditLogService;
    }

    public bool IsEnabled()
    {
        return GetBool("BackupSettings:Enabled", "BACKUP_ENABLED", true);
    }

    public async Task<BackupHistory> CreateBackupAsync(
        string? createdByUsername,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var backupDirectory = GetBackupDirectory();
        Directory.CreateDirectory(backupDirectory);

        var backupSuffix = Guid.NewGuid().ToString("N")[..8];
        var backupFileName = $"soteromap-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{backupSuffix}.db";
        var backupPath = Path.Combine(backupDirectory, backupFileName);

        var actor = string.IsNullOrWhiteSpace(createdByUsername) ? "sistema" : createdByUsername.Trim();

        try
        {
            await CreateBackupFileAsync(backupPath, cancellationToken);
            ValidateSqliteFile(backupPath);

            var history = new BackupHistory
            {
                CreatedAtUtc = DateTime.UtcNow,
                Status = "success",
                FilePath = backupPath,
                SizeBytes = new FileInfo(backupPath).Length,
                Hash = ComputeSha256(backupPath),
                ErrorMessage = string.Empty,
                CreatedByUsername = actor,
                Reason = reason
            };

            _context.BackupHistories.Add(history);
            await _context.SaveChangesAsync(cancellationToken);

            await _auditLogService.LogSecurityEventAsync(
                actionType: "backup-created",
                resource: "database",
                summary: $"Respaldo creado: {Path.GetFileName(backupPath)}",
                details: $"Motivo: {reason}",
                result: "success",
                severity: "info",
                changedByUsername: actor,
                cancellationToken: cancellationToken);

            await CleanupExpiredBackupsAsync(cancellationToken);
            return history;
        }
        catch (Exception ex)
        {
            var history = new BackupHistory
            {
                CreatedAtUtc = DateTime.UtcNow,
                Status = "failure",
                FilePath = backupPath,
                SizeBytes = System.IO.File.Exists(backupPath) ? new FileInfo(backupPath).Length : 0,
                Hash = System.IO.File.Exists(backupPath) ? ComputeSha256(backupPath) : string.Empty,
                ErrorMessage = ex.Message,
                CreatedByUsername = actor,
                Reason = reason
            };

            _context.BackupHistories.Add(history);
            await _context.SaveChangesAsync(cancellationToken);

            await _auditLogService.LogSecurityEventAsync(
                actionType: "backup-failed",
                resource: "database",
                summary: $"Fallo al crear respaldo: {Path.GetFileName(backupPath)}",
                details: ex.Message,
                result: "failure",
                severity: "critical",
                changedByUsername: actor,
                cancellationToken: cancellationToken);

            throw;
        }
    }

    public async Task<IReadOnlyList<BackupHistory>> GetLatestBackupsAsync(int take, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 100);
        return await _context.BackupHistories
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CleanupExpiredBackupsAsync(CancellationToken cancellationToken = default)
    {
        var retentionDays = GetInt("BackupSettings:RetentionDays", "BACKUP_RETENTION_DAYS", 30);
        if (retentionDays <= 0)
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var expired = await _context.BackupHistories
            .Where(item => item.Status == "success" && item.CreatedAtUtc < cutoff)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return 0;
        }

        foreach (var backup in expired)
        {
            TryDeleteFile(backup.FilePath);
            backup.Status = "expired";
            backup.ErrorMessage = $"Retention cleanup ({retentionDays} days)";
        }

        await _context.SaveChangesAsync(cancellationToken);
        await _auditLogService.LogSecurityEventAsync(
            actionType: "backup-retention-cleanup",
            resource: "database-backup",
            summary: $"Se limpiaron {expired.Count} respaldos expirados",
            details: $"Retencion configurada: {retentionDays} dias",
            result: "success",
            severity: "info",
            changedByUsername: "sistema",
            cancellationToken: cancellationToken);
        return expired.Count;
    }

    public DateTimeOffset? GetNextScheduledRunUtc(DateTimeOffset nowUtc)
    {
        var cron = GetString("BackupSettings:Cron", "BACKUP_CRON");
        if (!string.IsNullOrWhiteSpace(cron))
        {
            if (TryParseCron(cron, out var expression))
            {
                DateTime? next = expression?.GetNextOccurrence(nowUtc.UtcDateTime, TimeZoneInfo.Utc);
                return next.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(next.Value, DateTimeKind.Utc))
                    : null;
            }
        }

        var intervalHours = GetInt("BackupSettings:IntervalHours", "BACKUP_INTERVAL_HOURS", 24);
        if (intervalHours <= 0)
        {
            intervalHours = 24;
        }

        return nowUtc.AddHours(intervalHours);
    }

    public async Task CreateBackupFromCurrentDatabaseAsync(string? createdByUsername, string reason, CancellationToken cancellationToken = default)
    {
        await CreateBackupAsync(createdByUsername, reason, cancellationToken);
    }

    private async Task CreateBackupFileAsync(string backupPath, CancellationToken cancellationToken)
    {
        var sourcePath = SqliteDatabasePathResolver.ResolveDatabasePath(_configuration, _environment.ContentRootPath);
        await using var source = new SqliteConnection($"Data Source={sourcePath}");
        await using var destination = new SqliteConnection($"Data Source={backupPath}");
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private static bool TryParseCron(string cron, out CronExpression? expression)
    {
        if (CronExpression.TryParse(cron, CronFormat.IncludeSeconds, out expression))
        {
            return true;
        }

        return CronExpression.TryParse(cron, CronFormat.Standard, out expression);
    }

    private string GetBackupDirectory()
    {
        var databasePath = SqliteDatabasePathResolver.ResolveDatabasePath(_configuration, _environment.ContentRootPath);
        var databaseDirectory = Path.GetDirectoryName(databasePath) ?? AppContext.BaseDirectory;
        return Path.Combine(databaseDirectory, "backups");
    }

    private static void ValidateSqliteFile(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master LIMIT 1;";
        command.ExecuteScalar();
    }

    private static string ComputeSha256(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch
        {
        }
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
