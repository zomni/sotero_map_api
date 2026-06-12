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
    private readonly LdapAuthenticationService _ldapAuthenticationService;
    private readonly IConfiguration _configuration;

    public BackendAuthService(
        AppDbContext context,
        IPasswordHasher<AuthUser> passwordHasher,
        LdapAuthenticationService ldapAuthenticationService,
        IConfiguration configuration)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _ldapAuthenticationService = ldapAuthenticationService;
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
            AppRoles.Viewer,
            cancellationToken);

        await NormalizeLegacyRolesAsync(cancellationToken);
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

        var useLdap = GetBool("AuthSettings:UseLdapAuthentication", "AUTH_USE_LDAP", true);
        var allowLocalBreakGlass = GetBool("AuthSettings:AllowLocalBreakGlass", "ALLOW_LOCAL_BREAK_GLASS", true);

        if (useLdap)
        {
            var ldapResult = await _ldapAuthenticationService.AuthenticateAsync(normalizedUsername, password, cancellationToken);
            if (ldapResult.Succeeded)
            {
                var directoryUser = await _context.AuthUsers
                    .SingleOrDefaultAsync(u => u.NormalizedUsername == ldapResult.NormalizedUsername, cancellationToken);

                if (directoryUser is null || !directoryUser.IsActive)
                {
                    directoryUser = await CreateOrReactivateLdapUserIfEnabledAsync(ldapResult.NormalizedUsername, cancellationToken);
                    if (directoryUser is null)
                    {
                        return LoginResult.CreateFailed("Usuario autenticado en AD pero no tiene perfil local habilitado.");
                    }
                }

                if (directoryUser.LockedUntilUtc.HasValue && directoryUser.LockedUntilUtc > DateTime.UtcNow)
                {
                    return LoginResult.CreateLocked(directoryUser.LockedUntilUtc.Value);
                }

                directoryUser.FailedLoginAttempts = 0;
                directoryUser.LockedUntilUtc = null;
                directoryUser.LastLoginAtUtc = DateTime.UtcNow;
                directoryUser.UpdatedAtUtc = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);

                return LoginResult.CreateSucceeded(directoryUser);
            }

            if (!ldapResult.IsAvailable)
            {
                return LoginResult.CreateFailed(ldapResult.Message);
            }

            if (!allowLocalBreakGlass)
            {
                return LoginResult.CreateFailed(ldapResult.Message);
            }
        }

        if (!allowLocalBreakGlass || !IsBreakGlassUser(normalizedUsername))
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

    public static string NormalizeRole(string? role)
    {
        return (role ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            AppRoles.Admin => AppRoles.Admin,
            AppRoles.Editor => AppRoles.Editor,
            AppRoles.Viewer => AppRoles.Viewer,
            AppRoles.Auditor => AppRoles.Auditor,
            "user" => AppRoles.Viewer,
            _ => AppRoles.Viewer
        };
    }

    private async Task NormalizeLegacyRolesAsync(CancellationToken cancellationToken)
    {
        var legacyUsers = await _context.AuthUsers
            .Where(user => user.Role == "user")
            .ToListAsync(cancellationToken);

        if (legacyUsers.Count == 0)
        {
            return;
        }

        foreach (var user in legacyUsers)
        {
            user.Role = AppRoles.Viewer;
            user.UpdatedAtUtc = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AuthUser?> GetUserByIdAsync(int userId, CancellationToken cancellationToken = default)
    {
        return await _context.AuthUsers.SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
    }

    public async Task UpdateUserAsync(AuthUser user, CancellationToken cancellationToken = default)
    {
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<AuthUser?> CreateOrReactivateLdapUserIfEnabledAsync(
        string normalizedUsername,
        CancellationToken cancellationToken)
    {
        var autoProvision = GetBool("AuthSettings:AutoProvisionLdapUsers", "AUTO_PROVISION_LDAP_USERS", true);
        if (!autoProvision)
        {
            return null;
        }

        var defaultRole = NormalizeRole(_configuration["AuthSettings:DefaultLdapRole"]);
        var existing = await _context.AuthUsers
            .SingleOrDefaultAsync(user => user.NormalizedUsername == normalizedUsername, cancellationToken);

        if (existing is not null)
        {
            existing.IsActive = true;
            if (string.IsNullOrWhiteSpace(existing.Role))
            {
                existing.Role = defaultRole;
            }

            existing.UpdatedAtUtc = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var user = new AuthUser
        {
            Username = normalizedUsername.ToLowerInvariant(),
            NormalizedUsername = normalizedUsername,
            Role = defaultRole,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            LastLoginAtUtc = DateTime.UtcNow
        };

        user.PasswordHash = _passwordHasher.HashPassword(user, Guid.NewGuid().ToString("N"));
        _context.AuthUsers.Add(user);
        await _context.SaveChangesAsync(cancellationToken);

        return user;
    }

    private bool IsBreakGlassUser(string normalizedUsername)
    {
        var configuredUsers = (_configuration["AuthSettings:BreakGlassUsernames"] ?? _configuration["SeedUsers:Admin:Username"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (configuredUsers.Length == 0)
        {
            return true;
        }

        return configuredUsers.Any(user => string.Equals(Normalize(user), normalizedUsername, StringComparison.OrdinalIgnoreCase));
    }

    private bool GetBool(string configKey, string envKey, bool fallback)
    {
        var envValue = Environment.GetEnvironmentVariable(envKey);
        if (bool.TryParse(envValue, out var parsedEnv))
        {
            return parsedEnv;
        }

        return bool.TryParse(_configuration[configKey], out var parsedConfig) ? parsedConfig : fallback;
    }
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
