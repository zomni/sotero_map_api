using System.DirectoryServices.Protocols;
using System.Net;

namespace SoteroMap.API.Services;

public sealed class LdapAuthenticationService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<LdapAuthenticationService> _logger;

    public LdapAuthenticationService(
        IConfiguration configuration,
        ILogger<LdapAuthenticationService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<LdapAuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername) || string.IsNullOrWhiteSpace(password))
        {
            return LdapAuthenticationResult.CreateFailed("Credenciales invalidas.");
        }

        var host = GetString("LDAP_HOST", "LdapSettings:Host");
        var fallbackHost = GetString("LDAP_FALLBACK_HOST", "LdapSettings:FallbackHost");
        var port = GetInt("LDAP_PORT", "LdapSettings:Port", 636);
        var domain = GetString("LDAP_DOMAIN", "LdapSettings:Domain");
        var baseDn = GetString("LDAP_BASE_DN", "LdapSettings:BaseDn");
        var upnSuffixes = GetString("LDAP_UPN_SUFFIXES", "LdapSettings:UpnSuffixes");
        var useSsl = GetBool("LDAP_USE_SSL", "LdapSettings:UseSsl", true);
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(domain))
        {
            return LdapAuthenticationResult.CreateUnavailable("LDAP no configurado.");
        }

        try
        {
            var hosts = new[] { host, fallbackHost }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var bindAttempts = BuildBindAttempts(normalizedUsername, password, domain, baseDn, upnSuffixes).ToArray();
            LdapException? lastLdapException = null;
            Exception? lastConnectionException = null;

            foreach (var candidateHost in hosts)
            {
                foreach (var bindAttempt in bindAttempts)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(10));

                    try
                    {
                        return await Task.Run(() =>
                        {
                            var identifier = new LdapDirectoryIdentifier(candidateHost, port);
                            using var connection = new LdapConnection(identifier)
                            {
                                AuthType = bindAttempt.AuthType,
                                Credential = bindAttempt.Credential,
                                Timeout = TimeSpan.FromSeconds(10)
                            };

                            connection.SessionOptions.ProtocolVersion = 3;
                            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

                            connection.SessionOptions.SecureSocketLayer = useSsl;
                            cts.Token.ThrowIfCancellationRequested();
                            connection.Bind();

                            return LdapAuthenticationResult.CreateSucceeded(normalizedUsername.ToUpperInvariant());
                        }, cts.Token);
                    }
                    catch (LdapException ex) when (IsInvalidCredentials(ex))
                    {
                        _logger.LogWarning(
                            ex,
                            "LDAPS rejected credentials. Host={Host}; AuthType={AuthType}; UserFormat={UserFormat}; ErrorCode={ErrorCode}; ServerError={ServerError}",
                            candidateHost,
                            bindAttempt.AuthType,
                            bindAttempt.Label,
                            ex.ErrorCode,
                            ex.ServerErrorMessage);
                        lastLdapException = ex;
                    }
                    catch (LdapException ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "LDAPS bind failed. Host={Host}; AuthType={AuthType}; UserFormat={UserFormat}; ErrorCode={ErrorCode}; ServerError={ServerError}",
                            candidateHost,
                            bindAttempt.AuthType,
                            bindAttempt.Label,
                            ex.ErrorCode,
                            ex.ServerErrorMessage);
                        lastConnectionException = ex;
                    }
                    catch (OperationCanceledException ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "LDAPS bind timeout. Host={Host}; AuthType={AuthType}; UserFormat={UserFormat}",
                            candidateHost,
                            bindAttempt.AuthType,
                            bindAttempt.Label);
                        lastConnectionException = ex;
                    }
                }
            }

            return lastLdapException is not null
                ? LdapAuthenticationResult.CreateFailed("Credenciales invalidas.")
                : LdapAuthenticationResult.CreateUnavailable(BuildUnavailableMessage(lastConnectionException));
        }
        catch (OperationCanceledException)
        {
            return LdapAuthenticationResult.CreateUnavailable("LDAP no respondio a tiempo.");
        }
        catch (LdapException ex) when (IsInvalidCredentials(ex))
        {
            return LdapAuthenticationResult.CreateFailed("Credenciales invalidas.");
        }
        catch (LdapException ex)
        {
            return LdapAuthenticationResult.CreateUnavailable($"LDAP no disponible. Codigo: {ex.ErrorCode}.");
        }
        catch (Exception)
        {
            return LdapAuthenticationResult.CreateUnavailable("LDAP no disponible.");
        }
    }

    private string? GetString(string envName, string configKey)
    {
        return Environment.GetEnvironmentVariable(envName)
            ?? _configuration[configKey];
    }

    private int GetInt(string envName, string configKey, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        if (int.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return int.TryParse(_configuration[configKey], out parsed) ? parsed : fallback;
    }

    private bool GetBool(string envName, string configKey, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return bool.TryParse(_configuration[configKey], out parsed) ? parsed : fallback;
    }

    private static IEnumerable<LdapBindAttempt> BuildBindAttempts(
        string username,
        string password,
        string domain,
        string? baseDn,
        string? configuredUpnSuffixes)
    {
        yield return new LdapBindAttempt(
            "domain-credential",
            AuthType.Negotiate,
            new NetworkCredential(username, password, domain));

        foreach (var upnSuffix in BuildUpnSuffixes(baseDn, configuredUpnSuffixes))
        {
            yield return new LdapBindAttempt(
                $"upn-{upnSuffix}",
                AuthType.Basic,
                new NetworkCredential($"{username}@{upnSuffix}", password));
        }

        yield return new LdapBindAttempt(
            "netbios-user",
            AuthType.Basic,
            new NetworkCredential($@"{domain}\{username}", password));

        yield return new LdapBindAttempt(
            "username-domain",
            AuthType.Basic,
            new NetworkCredential(username, password, domain));
    }

    private static IEnumerable<string> BuildUpnSuffixes(string? baseDn, string? configuredUpnSuffixes)
    {
        var suffixes = (configuredUpnSuffixes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var baseDnSuffix = BaseDnToDnsName(baseDn);
        if (!string.IsNullOrWhiteSpace(baseDnSuffix))
        {
            suffixes.Add(baseDnSuffix);
        }

        return suffixes
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string BaseDnToDnsName(string? baseDn)
    {
        if (string.IsNullOrWhiteSpace(baseDn))
        {
            return string.Empty;
        }

        return string.Join(
            ".",
            baseDn
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(part => part.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                .Select(part => part[3..]));
    }

    private static bool IsInvalidCredentials(LdapException exception)
    {
        return exception.ErrorCode == 49
            || exception.ServerErrorMessage.Contains("data 52e", StringComparison.OrdinalIgnoreCase)
            || exception.ServerErrorMessage.Contains("invalidCredentials", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildUnavailableMessage(Exception? exception)
    {
        if (exception is not LdapException ldapException)
        {
            return "LDAP no disponible.";
        }

        return ldapException.ErrorCode == 81
            ? "LDAP no disponible. Revisa conectividad, nombre del DC o confianza del certificado LDAPS. Codigo: 81."
            : $"LDAP no disponible. Codigo: {ldapException.ErrorCode}.";
    }

    private static string NormalizeUsername(string? value)
    {
        var username = (value ?? string.Empty).Trim();
        if (username.Contains('\\'))
        {
            username = username[(username.LastIndexOf('\\') + 1)..];
        }

        if (username.Contains('@'))
        {
            username = username[..username.IndexOf('@')];
        }

        return username.Trim();
    }
}

public sealed record LdapAuthenticationResult(bool Succeeded, bool IsAvailable, string NormalizedUsername, string Message)
{
    public static LdapAuthenticationResult CreateSucceeded(string normalizedUsername)
        => new(true, true, normalizedUsername, string.Empty);

    public static LdapAuthenticationResult CreateFailed(string message)
        => new(false, true, string.Empty, message);

    public static LdapAuthenticationResult CreateUnavailable(string message)
        => new(false, false, string.Empty, message);
}

internal sealed record LdapBindAttempt(string Label, AuthType AuthType, NetworkCredential Credential);
