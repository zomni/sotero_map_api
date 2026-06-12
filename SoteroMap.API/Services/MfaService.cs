using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OtpNet;
using QRCoder;
using SoteroMap.API.Data;
using SoteroMap.API.Models;

namespace SoteroMap.API.Services;

public sealed class MfaService
{
    private const string CachePrefix = "sotero:mfa:setup:";
    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly IDataProtector _protector;
    private readonly IConfiguration _configuration;

    public MfaService(
        AppDbContext context,
        IMemoryCache cache,
        IDataProtectionProvider dataProtectionProvider,
        IConfiguration configuration)
    {
        _context = context;
        _cache = cache;
        _protector = dataProtectionProvider.CreateProtector("SoteroMap.API.MfaSecret.v1");
        _configuration = configuration;
    }

    public bool IsRequiredForRole(string role)
    {
        var normalizedRole = BackendAuthService.NormalizeRole(role);
        return normalizedRole == AppRoles.Admin
            ? GetBool("MfaSettings:RequireForAdmins", "MFA_REQUIRE_FOR_ADMINS", true)
            : GetBool("MfaSettings:RequireForNonAdmins", "MFA_REQUIRE_FOR_NON_ADMINS", false);
    }

    public async Task<MfaSetupSession?> BeginEnrollmentAsync(AuthUser user, CancellationToken cancellationToken = default)
    {
        if (user is null)
        {
            return null;
        }

        var setupKey = Guid.NewGuid().ToString("N");
        var secret = GenerateSecret();
        var issuer = GetString("MfaSettings:Issuer", "MFA_ISSUER") ?? "SoteroMap";
        var accountName = user.Username;
        var otpAuthUri = BuildOtpAuthUri(issuer, accountName, secret);
        var qrDataUri = BuildQrDataUri(otpAuthUri);
        var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(GetInt("MfaSettings:PendingMinutes", "MFA_PENDING_MINUTES", 10));
        var state = new MfaPendingState(user.Id, user.NormalizedUsername, user.Username, secret, issuer, expiresAtUtc);

        _cache.Set(
            CacheKey(setupKey),
            state,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = expiresAtUtc
            });

        await Task.CompletedTask;

        return new MfaSetupSession(
            setupKey,
            accountName,
            issuer,
            secret,
            FormatSecretForDisplay(secret),
            otpAuthUri,
            qrDataUri,
            expiresAtUtc);
    }

    public MfaSetupSession? GetEnrollmentSession(string setupKey)
    {
        var state = GetPendingState(setupKey);
        if (state is null)
        {
            return null;
        }

        var otpAuthUri = BuildOtpAuthUri(state.Issuer, state.Username, state.Secret);
        return new MfaSetupSession(
            setupKey,
            state.Username,
            state.Issuer,
            state.Secret,
            FormatSecretForDisplay(state.Secret),
            otpAuthUri,
            BuildQrDataUri(otpAuthUri),
            state.ExpiresAtUtc);
    }

    public string GetCurrentDevelopmentCode(string setupKey)
    {
        var state = GetPendingState(setupKey);
        if (state is null)
        {
            return string.Empty;
        }

        var digits = GetInt("MfaSettings:Digits", "MFA_DIGITS", 6);
        var stepSeconds = GetInt("MfaSettings:PeriodSeconds", "MFA_PERIOD_SECONDS", 30);
        var key = Base32Encoding.ToBytes(state.Secret);
        var totp = new Totp(key, step: stepSeconds, totpSize: digits, mode: OtpHashMode.Sha1);
        return totp.ComputeTotp(DateTime.UtcNow);
    }

    public async Task<bool> CompleteEnrollmentAsync(
        int userId,
        string setupKey,
        string code,
        CancellationToken cancellationToken = default)
    {
        var state = GetPendingState(setupKey);
        if (state is null || state.UserId != userId)
        {
            return false;
        }

        if (!VerifyCode(state.Secret, code))
        {
            return false;
        }

        var user = await _context.AuthUsers.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null)
        {
            return false;
        }

        user.MfaSecretProtected = ProtectSecret(state.Secret);
        user.MfaEnabled = true;
        user.MfaEnrolledAtUtc = DateTime.UtcNow;
        user.MfaLastVerifiedAtUtc = DateTime.UtcNow;
        user.UpdatedAtUtc = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        _cache.Remove(CacheKey(setupKey));
        return true;
    }

    public bool VerifyExistingCode(AuthUser user, string code)
    {
        if (user is null || !user.MfaEnabled || string.IsNullOrWhiteSpace(user.MfaSecretProtected))
        {
            return false;
        }

        var secret = UnprotectSecret(user.MfaSecretProtected);
        if (string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        return VerifyCode(secret, code);
    }

    public void MarkMfaVerified(AuthUser user)
    {
        if (user is null)
        {
            return;
        }

        user.MfaLastVerifiedAtUtc = DateTime.UtcNow;
    }

    public async Task<bool> ResetEnrollmentAsync(int userId, CancellationToken cancellationToken = default)
    {
        var user = await _context.AuthUsers.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null)
        {
            return false;
        }

        user.MfaEnabled = false;
        user.MfaSecretProtected = string.Empty;
        user.MfaEnrolledAtUtc = null;
        user.MfaLastVerifiedAtUtc = null;
        user.UpdatedAtUtc = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string CacheKey(string setupKey) => $"{CachePrefix}{setupKey}";

    private MfaPendingState? GetPendingState(string setupKey)
    {
        return _cache.TryGetValue(CacheKey(setupKey), out MfaPendingState? state) ? state : null;
    }

    private string ProtectSecret(string secret)
    {
        return _protector.Protect(secret);
    }

    private string UnprotectSecret(string protectedSecret)
    {
        try
        {
            return _protector.Unprotect(protectedSecret);
        }
        catch
        {
            return string.Empty;
        }
    }

    private string BuildOtpAuthUri(string issuer, string accountName, string secret)
    {
        var digits = GetInt("MfaSettings:Digits", "MFA_DIGITS", 6);
        var period = GetInt("MfaSettings:PeriodSeconds", "MFA_PERIOD_SECONDS", 30);
        return $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountName)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={digits}&period={period}";
    }

    private string BuildQrDataUri(string otpAuthUri)
    {
        var generator = new QRCodeGenerator();
        var data = generator.CreateQrCode(otpAuthUri, QRCodeGenerator.ECCLevel.Q);
        var qr = new PngByteQRCode(data);
        var bytes = qr.GetGraphic(10);
        var base64 = Convert.ToBase64String(bytes);
        return $"data:image/png;base64,{base64}";
    }

    private bool VerifyCode(string secret, string code)
    {
        var digits = GetInt("MfaSettings:Digits", "MFA_DIGITS", 6);
        var window = GetInt("MfaSettings:WindowSteps", "MFA_WINDOW_STEPS", 6);
        var normalizedCode = NormalizeDigits(code, digits);
        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return false;
        }

        var stepSeconds = GetInt("MfaSettings:PeriodSeconds", "MFA_PERIOD_SECONDS", 30);
        var key = Base32Encoding.ToBytes(secret);
        var totp = new Totp(key, step: stepSeconds, totpSize: digits, mode: OtpHashMode.Sha1);
        return totp.VerifyTotp(
            normalizedCode,
            out _,
            new VerificationWindow(previous: window, future: window));
    }

    private static string GenerateSecret(int byteCount = 20)
    {
        var bytes = KeyGeneration.GenerateRandomKey(byteCount);
        return Base32Encoding.ToString(bytes);
    }

    private static string NormalizeDigits(string? value, int digits)
    {
        var sanitized = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return sanitized.Length == digits ? sanitized : string.Empty;
    }

    private static string FormatSecretForDisplay(string secret)
    {
        return string.Join(" ", secret.Chunk(4).Select(chunk => new string(chunk)));
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

public sealed record MfaSetupSession(
    string SetupKey,
    string AccountName,
    string Issuer,
    string Secret,
    string SecretDisplay,
    string OtpAuthUri,
    string QrSvg,
    DateTimeOffset ExpiresAtUtc);

internal sealed record MfaPendingState(
    int UserId,
    string NormalizedUsername,
    string Username,
    string Secret,
    string Issuer,
    DateTimeOffset ExpiresAtUtc);
