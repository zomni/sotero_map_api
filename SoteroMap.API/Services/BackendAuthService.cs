using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Models;

namespace SoteroMap.API.Services;

public class BackendAuthService
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly AppDbContext _context;
    private readonly IPasswordHasher<AuthUser> _passwordHasher;
    private readonly IConfiguration _configuration;

    public BackendAuthService(
        AppDbContext context,
        IPasswordHasher<AuthUser> passwordHasher,
        IConfiguration configuration)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _configuration = configuration;
    }

    public async Task EnsureSeedUsersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureUserAsync(
            _configuration["SeedUsers:Admin:Username"],
            _configuration["SeedUsers:Admin:Password"],
            AppRoles.Admin,
            cancellationToken);

        await EnsureUserAsync(
            _configuration["SeedUsers:Viewer:Username"],
            _configuration["SeedUsers:Viewer:Password"],
            AppRoles.User,
            cancellationToken);
    }

    public async Task<LoginResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = Normalize(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername) || string.IsNullOrWhiteSpace(password))
        {
            return LoginResult.CreateFailed("Credenciales invalidas.");
        }

        var user = await _context.AuthUsers
            .SingleOrDefaultAsync(u => u.NormalizedUsername == normalizedUsername, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return LoginResult.CreateFailed("Credenciales invalidas.");
        }

        if (user.LockedUntilUtc.HasValue && user.LockedUntilUtc > DateTime.UtcNow)
        {
            return LoginResult.CreateLocked(user.LockedUntilUtc.Value);
        }

        var passwordResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);

        if (passwordResult == PasswordVerificationResult.Failed)
        {
            user.FailedLoginAttempts += 1;
            user.UpdatedAtUtc = DateTime.UtcNow;

            if (user.FailedLoginAttempts >= MaxFailedAttempts)
            {
                user.LockedUntilUtc = DateTime.UtcNow.Add(LockoutDuration);
                user.FailedLoginAttempts = 0;
            }

            await _context.SaveChangesAsync(cancellationToken);
            return user.LockedUntilUtc.HasValue && user.LockedUntilUtc > DateTime.UtcNow
                ? LoginResult.CreateLocked(user.LockedUntilUtc.Value)
                : LoginResult.CreateFailed("Credenciales invalidas.");
        }

        if (passwordResult == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _passwordHasher.HashPassword(user, password);
        }

        user.FailedLoginAttempts = 0;
        user.LockedUntilUtc = null;
        user.LastLoginAtUtc = DateTime.UtcNow;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return LoginResult.CreateSucceeded(user);
    }

    private async Task EnsureUserAsync(
        string? username,
        string? password,
        string role,
        CancellationToken cancellationToken)
    {
        var normalizedUsername = Normalize(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var existing = await _context.AuthUsers
            .SingleOrDefaultAsync(u => u.NormalizedUsername == normalizedUsername, cancellationToken);

        if (existing is null)
        {
            var user = new AuthUser
            {
                Username = username!.Trim(),
                NormalizedUsername = normalizedUsername,
                Role = role,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            user.PasswordHash = _passwordHasher.HashPassword(user, password);
            _context.AuthUsers.Add(user);
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        var changed = false;

        if (!string.Equals(existing.Role, role, StringComparison.OrdinalIgnoreCase))
        {
            existing.Role = role;
            changed = true;
        }

        if (!existing.IsActive)
        {
            existing.IsActive = true;
            changed = true;
        }

        if (!string.Equals(existing.Username, username?.Trim(), StringComparison.Ordinal))
        {
            existing.Username = username!.Trim();
            changed = true;
        }

        existing.PasswordHash = _passwordHasher.HashPassword(existing, password);
        existing.FailedLoginAttempts = 0;
        existing.LockedUntilUtc = null;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        changed = true;

        if (changed)
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}

public sealed class LoginResult
{
    private LoginResult(bool succeeded, AuthUser? user, string errorMessage, DateTime? lockedUntilUtc)
    {
        Succeeded = succeeded;
        User = user;
        ErrorMessage = errorMessage;
        LockedUntilUtc = lockedUntilUtc;
    }

    public bool Succeeded { get; }
    public AuthUser? User { get; }
    public string ErrorMessage { get; }
    public DateTime? LockedUntilUtc { get; }

    public static LoginResult CreateSucceeded(AuthUser user) => new(true, user, string.Empty, null);
    public static LoginResult CreateFailed(string errorMessage) => new(false, null, errorMessage, null);
    public static LoginResult CreateLocked(DateTime lockedUntilUtc) => new(false, null, "Cuenta bloqueada temporalmente.", lockedUntilUtc);
}
