using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoteroMap.API.Models;
using SoteroMap.API.Services;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/backups")]
[Authorize(Roles = AppRoles.Admin)]
public class BackupsController : ControllerBase
{
    private readonly DatabaseBackupService _backupService;

    public BackupsController(DatabaseBackupService backupService)
    {
        _backupService = backupService;
    }

    [HttpGet("latest")]
    public async Task<IActionResult> Latest([FromQuery] int take = 10, CancellationToken cancellationToken = default)
    {
        var backups = await _backupService.GetLatestBackupsAsync(take, cancellationToken);
        return Ok(backups.Select(MapBackup));
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken cancellationToken = default)
    {
        var backup = await _backupService.CreateBackupAsync(User.Identity?.Name ?? "admin", "manual-api-trigger", cancellationToken);
        return Ok(MapBackup(backup));
    }

    [HttpPost("cleanup")]
    public async Task<IActionResult> Cleanup(CancellationToken cancellationToken = default)
    {
        var removed = await _backupService.CleanupExpiredBackupsAsync(cancellationToken);
        return Ok(new { removed });
    }

    [HttpGet("{backupId:int}/verify")]
    public async Task<IActionResult> Verify(int backupId, CancellationToken cancellationToken = default)
    {
        var backup = await _backupService.GetBackupByIdAsync(backupId, cancellationToken);
        if (backup is null)
        {
            return NotFound(new { message = "Backup no encontrado." });
        }

        var verification = await _backupService.VerifyBackupAsync(backup.FilePath, backup.Hash, cancellationToken);
        return Ok(new
        {
            backup.Id,
            backup.CreatedAtUtc,
            backup.Status,
            backup.FilePath,
            backup.SizeBytes,
            backup.Hash,
            backup.ErrorMessage,
            backup.CreatedByUsername,
            backup.Reason,
            verification
        });
    }

    [HttpGet("latest/verify")]
    public async Task<IActionResult> VerifyLatest(CancellationToken cancellationToken = default)
    {
        var backup = (await _backupService.GetLatestBackupsAsync(1, cancellationToken)).FirstOrDefault();
        if (backup is null)
        {
            return NotFound(new { message = "No hay backups disponibles." });
        }

        var verification = await _backupService.VerifyBackupAsync(backup.FilePath, backup.Hash, cancellationToken);
        return Ok(new
        {
            backup.Id,
            backup.CreatedAtUtc,
            backup.Status,
            backup.FilePath,
            backup.SizeBytes,
            backup.Hash,
            backup.ErrorMessage,
            backup.CreatedByUsername,
            backup.Reason,
            verification
        });
    }

    private static object MapBackup(BackupHistory backup)
    {
        return new
        {
            backup.Id,
            backup.CreatedAtUtc,
            backup.Status,
            backup.FilePath,
            backup.SizeBytes,
            backup.Hash,
            backup.ErrorMessage,
            backup.CreatedByUsername,
            backup.Reason
        };
    }
}
