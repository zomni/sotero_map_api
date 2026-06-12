using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoteroMap.API.Infrastructure;
using SoteroMap.API.Models;
using SoteroMap.API.Services;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Controllers;

[AllowAnonymous]
public class AuthController : Controller
{
    private const string PendingMfaScheme = "MfaPending";
    private const string RememberMeClaimType = "sotero:remember_me";
    private const string MfaModeClaimType = "sotero:mfa_mode";
    private const string MfaSetupKeyClaimType = "sotero:mfa_setup_key";
    private const string MfaUserIdClaimType = "sotero:mfa_user_id";
    private const string MfaReturnUrlClaimType = "sotero:mfa_return_url";
    private const string MfaModeSetup = "setup";
    private const string MfaModeChallenge = "challenge";
    private readonly BackendAuthService _authService;
    private readonly MfaService _mfaService;
    private readonly AuditLogService _auditLogService;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public AuthController(
        BackendAuthService authService,
        MfaService mfaService,
        AuditLogService auditLogService,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        _authService = authService;
        _mfaService = mfaService;
        _auditLogService = auditLogService;
        _configuration = configuration;
        _environment = environment;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToLocal(returnUrl);
        }

        return View(new LoginViewModel
        {
            ReturnUrl = returnUrl ?? string.Empty
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _authService.AuthenticateAsync(model.Username, model.Password, cancellationToken);

        if (!result.Succeeded || result.User is null)
        {
            await _auditLogService.LogSecurityEventAsync(
                actionType: "login-failed",
                resource: "auth/login",
                summary: $"Intento de login fallido para {model.Username}",
                details: result.ErrorMessage ?? "Credenciales invalidas",
                result: "failure",
                severity: "warning",
                changedByUsername: model.Username,
                cancellationToken: cancellationToken);

            if (result.LockedUntilUtc.HasValue)
            {
                ModelState.AddModelError(string.Empty,
                    $"Cuenta bloqueada hasta {result.LockedUntilUtc.Value.ToLocalTime():dd/MM/yyyy HH:mm}.");
            }
            else
            {
                ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "No se pudo completar el inicio de sesion.");
            }

            return View(model);
        }

        if (_mfaService.IsRequiredForRole(result.User.Role))
        {
            await _auditLogService.LogSecurityEventAsync(
                actionType: "login-challenge",
                resource: "auth/mfa",
                summary: $"Login validado para {result.User.Username}, pendiente MFA",
                details: $"Rol {BackendAuthService.NormalizeRole(result.User.Role)}",
                result: "challenge",
                severity: "info",
                changedByUsername: result.User.Username,
                cancellationToken: cancellationToken);

            return await BeginMfaFlowAsync(result.User, model, cancellationToken);
        }

        await SignInFinalUserAsync(result.User, model.RememberMe, cancellationToken);
        await _auditLogService.LogSecurityEventAsync(
            actionType: "login-success",
            resource: "auth/login",
            summary: $"Login exitoso de {result.User.Username}",
            details: $"Rol {BackendAuthService.NormalizeRole(result.User.Role)}",
            result: "success",
            severity: "info",
            changedByUsername: result.User.Username,
            cancellationToken: cancellationToken);
        return RedirectToLocal(model.ReturnUrl);
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignOutAsync(PendingMfaScheme);
        await _auditLogService.LogSecurityEventAsync(
            actionType: "logout",
            resource: "auth/logout",
            summary: $"Logout de {User.Identity?.Name ?? "usuario"}",
            details: "Sesion cerrada por el usuario.",
            result: "success",
            severity: "info",
            changedByUsername: User.Identity?.Name,
            cancellationToken: default);
        return RedirectToAction(nameof(Login));
    }

    [Authorize]
    [HttpPost("/api/auth/logout")]
    public async Task<IActionResult> ApiLogout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignOutAsync(PendingMfaScheme);
        await _auditLogService.LogSecurityEventAsync(
            actionType: "logout",
            resource: "auth/logout",
            summary: $"Logout API de {User.Identity?.Name ?? "usuario"}",
            details: "Sesion cerrada por API.",
            result: "success",
            severity: "info",
            changedByUsername: User.Identity?.Name,
            cancellationToken: default);
        return Ok(new { signedOut = true });
    }

    [HttpGet]
    public async Task<IActionResult> MfaSetup(string? returnUrl = null, bool reset = false, CancellationToken cancellationToken = default)
    {
        var pending = await GetPendingMfaContextAsync(cancellationToken);
        if (pending is null || !string.Equals(pending.Mode, MfaModeSetup, StringComparison.OrdinalIgnoreCase))
        {
            return RedirectToAction(nameof(Login), new { returnUrl });
        }

        if (reset)
        {
            var user = await _authService.GetUserByIdAsync(pending.UserId, cancellationToken);
            if (user is null)
            {
                await HttpContext.SignOutAsync(PendingMfaScheme);
                return RedirectToAction(nameof(Login), new { returnUrl });
            }

            var newSession = await _mfaService.BeginEnrollmentAsync(user, cancellationToken);
            if (newSession is null)
            {
                await HttpContext.SignOutAsync(PendingMfaScheme);
                return RedirectToAction(nameof(Login), new { returnUrl });
            }

            await SignInPendingMfaAsync(user, pending.RememberMe, MfaModeSetup, newSession.SetupKey, pending.ReturnUrl ?? returnUrl, cancellationToken);
            return RedirectToAction(nameof(MfaSetup), new { returnUrl = pending.ReturnUrl ?? returnUrl });
        }

        var session = _mfaService.GetEnrollmentSession(pending.SetupKey);
        if (session is null)
        {
            await HttpContext.SignOutAsync(PendingMfaScheme);
            return RedirectToAction(nameof(Login), new { returnUrl });
        }

        return View(new MfaSetupViewModel
        {
            Username = pending.Username,
            Issuer = session.Issuer,
            AccountName = session.AccountName,
            SecretDisplay = session.SecretDisplay,
            QrSvg = session.QrSvg,
            SetupKey = pending.SetupKey,
            ReturnUrl = pending.ReturnUrl ?? returnUrl,
            ExpiresAtUtc = session.ExpiresAtUtc,
            ServerTimeUtc = DateTimeOffset.UtcNow,
            DevelopmentExpectedCode = _environment.IsDevelopment()
                ? _mfaService.GetCurrentDevelopmentCode(pending.SetupKey)
                : null
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MfaSetup(MfaSetupViewModel model, CancellationToken cancellationToken)
    {
        var pending = await GetPendingMfaContextAsync(cancellationToken);
        if (pending is null || !string.Equals(pending.Mode, MfaModeSetup, StringComparison.OrdinalIgnoreCase))
        {
            return RedirectToAction(nameof(Login), new { returnUrl = model.ReturnUrl });
        }

        if (!ModelState.IsValid)
        {
            var session = _mfaService.GetEnrollmentSession(pending.SetupKey);
            if (session is not null)
            {
                model.Username = pending.Username;
                model.Issuer = session.Issuer;
                model.AccountName = session.AccountName;
                model.SecretDisplay = session.SecretDisplay;
                model.QrSvg = session.QrSvg;
                model.SetupKey = pending.SetupKey;
                model.ExpiresAtUtc = session.ExpiresAtUtc;
            }

            return View(model);
        }

        var success = await _mfaService.CompleteEnrollmentAsync(
            pending.UserId,
            pending.SetupKey,
            model.Code,
            cancellationToken);

        if (!success)
        {
            ModelState.AddModelError(string.Empty, "El codigo MFA no es valido. Verifica que estes usando la entrada nueva de SoteroMap/admin y que la hora del telefono este sincronizada automaticamente.");
            var session = _mfaService.GetEnrollmentSession(pending.SetupKey);
            if (session is not null)
            {
                model.Username = pending.Username;
                model.Issuer = session.Issuer;
                model.AccountName = session.AccountName;
                model.SecretDisplay = session.SecretDisplay;
                model.QrSvg = session.QrSvg;
                model.SetupKey = pending.SetupKey;
                model.ExpiresAtUtc = session.ExpiresAtUtc;
                model.ServerTimeUtc = DateTimeOffset.UtcNow;
                model.DevelopmentExpectedCode = _environment.IsDevelopment()
                    ? _mfaService.GetCurrentDevelopmentCode(pending.SetupKey)
                    : null;
            }

            return View(model);
        }

        await HttpContext.SignOutAsync(PendingMfaScheme);
        var user = await _authService.GetUserByIdAsync(pending.UserId, cancellationToken);
        if (user is null)
        {
            return RedirectToAction(nameof(Login), new { returnUrl = model.ReturnUrl });
        }

        await SignInFinalUserAsync(user, pending.RememberMe, cancellationToken);
        await _auditLogService.LogSecurityEventAsync(
            actionType: "mfa-enrolled",
            resource: "auth/mfa",
            summary: $"MFA enrolado para {user.Username}",
            details: "El usuario completo el enrolamiento MFA exitosamente.",
            result: "success",
            severity: "info",
            changedByUsername: user.Username,
            cancellationToken: cancellationToken);
        return RedirectToLocal(model.ReturnUrl);
    }

    [HttpGet]
    public async Task<IActionResult> MfaMethod(string? returnUrl = null, CancellationToken cancellationToken = default)
    {
        var pending = await GetPendingMfaContextAsync(cancellationToken);
        if (pending is null || !string.Equals(pending.Mode, MfaModeChallenge, StringComparison.OrdinalIgnoreCase))
        {
            return RedirectToAction(nameof(Login), new { returnUrl });
        }

        return View(new MfaMethodViewModel
        {
            Username = pending.Username,
            ReturnUrl = pending.ReturnUrl ?? returnUrl,
            CanResetInDevelopment = _environment.IsDevelopment()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MfaMethod(MfaMethodViewModel model, CancellationToken cancellationToken)
    {
        var pending = await GetPendingMfaContextAsync(cancellationToken);
        if (pending is null || !string.Equals(pending.Mode, MfaModeChallenge, StringComparison.OrdinalIgnoreCase))
        {
            return RedirectToAction(nameof(Login), new { returnUrl = model.ReturnUrl });
        }

        if (!string.Equals(model.Method, "authenticator", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(string.Empty, "Metodo MFA no disponible.");
            model.Username = pending.Username;
            model.CanResetInDevelopment = _environment.IsDevelopment();
            return View(model);
        }

        return RedirectToAction(nameof(MfaVerify), new { returnUrl = pending.ReturnUrl ?? model.ReturnUrl });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MfaResetDevelopment(MfaMethodViewModel model, CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var pending = await GetPendingMfaContextAsync(cancellationToken);
        if (pending is null || !string.Equals(pending.Mode, MfaModeChallenge, StringComparison.OrdinalIgnoreCase))
        {
            return RedirectToAction(nameof(Login), new { returnUrl = model.ReturnUrl });
        }

        var user = await _authService.GetUserByIdAsync(pending.UserId, cancellationToken);
        if (user is null)
        {
            await HttpContext.SignOutAsync(PendingMfaScheme);
            return RedirectToAction(nameof(Login), new { returnUrl = model.ReturnUrl });
        }

        await _mfaService.ResetEnrollmentAsync(user.Id, cancellationToken);
        await HttpContext.SignOutAsync(PendingMfaScheme);

        var session = await _mfaService.BeginEnrollmentAsync(user, cancellationToken);
        if (session is null)
        {
            return RedirectToAction(nameof(Login), new { returnUrl = model.ReturnUrl });
        }

        await SignInPendingMfaAsync(user, pending.RememberMe, MfaModeSetup, session.SetupKey, pending.ReturnUrl ?? model.ReturnUrl, cancellationToken);
        return RedirectToAction(nameof(MfaSetup), new { returnUrl = pending.ReturnUrl ?? model.ReturnUrl });
    }

    [HttpGet]
    public async Task<IActionResult> MfaVerify(string? returnUrl = null, CancellationToken cancellationToken = default)
    {
        var pending = await GetPendingMfaContextAsync(cancellationToken);
        if (pending is null || !string.Equals(pending.Mode, MfaModeChallenge, StringComparison.OrdinalIgnoreCase))
        {
            return RedirectToAction(nameof(Login), new { returnUrl });
        }

        return View(new MfaVerifyViewModel
        {
            Username = pending.Username,
            ReturnUrl = pending.ReturnUrl ?? returnUrl
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MfaVerify(MfaVerifyViewModel model, CancellationToken cancellationToken)
    {
        var pending = await GetPendingMfaContextAsync(cancellationToken);
        if (pending is null || !string.Equals(pending.Mode, MfaModeChallenge, StringComparison.OrdinalIgnoreCase))
        {
            return RedirectToAction(nameof(Login), new { returnUrl = model.ReturnUrl });
        }

        var user = await _authService.GetUserByIdAsync(pending.UserId, cancellationToken);
        if (user is null || !_mfaService.VerifyExistingCode(user, model.Code))
        {
            ModelState.AddModelError(string.Empty, "El codigo MFA no es valido.");
            model.Username = pending.Username;
            return View(model);
        }

        _mfaService.MarkMfaVerified(user);
        await _authService.UpdateUserAsync(user, cancellationToken);
        await HttpContext.SignOutAsync(PendingMfaScheme);
        await SignInFinalUserAsync(user, pending.RememberMe, cancellationToken);
        await _auditLogService.LogSecurityEventAsync(
            actionType: "mfa-verified",
            resource: "auth/mfa",
            summary: $"MFA verificado para {user.Username}",
            details: "El usuario completo la segunda validacion.",
            result: "success",
            severity: "info",
            changedByUsername: user.Username,
            cancellationToken: cancellationToken);
        return RedirectToLocal(model.ReturnUrl);
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> KeepAlive()
    {
        var authResult = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!authResult.Succeeded || authResult.Principal is null)
        {
            return Unauthorized();
        }

        var rememberMe = string.Equals(
            authResult.Principal.FindFirstValue(RememberMeClaimType),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var properties = authResult.Properties ?? new AuthenticationProperties();
        properties.AllowRefresh = true;
        properties.IsPersistent = rememberMe;
        properties.ExpiresUtc = rememberMe
            ? DateTimeOffset.UtcNow.AddDays(_configuration.GetValue<double?>("SessionSettings:RememberMeDays") ?? 30)
            : DateTimeOffset.UtcNow.AddMinutes(_configuration.GetValue<double?>("SessionSettings:IdleMinutes") ?? 15);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            authResult.Principal,
            properties);

        return NoContent();
    }

    [HttpGet]
    public IActionResult AccessDenied()
    {
        return View();
    }

    [HttpGet("/api/auth/session")]
    public IActionResult Session()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Ok(new { isAuthenticated = false, username = "", role = "", isAdmin = false });
        }

        var role = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
        var rememberMe = string.Equals(
            User.FindFirstValue(RememberMeClaimType),
            "true",
            StringComparison.OrdinalIgnoreCase);

        return Ok(new
        {
            isAuthenticated = true,
            username = User.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
            role,
            isAdmin = string.Equals(role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase),
            rememberMe
        });
    }

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return Redirect("/dashboard");
    }

    private async Task<IActionResult> BeginMfaFlowAsync(AuthUser user, LoginViewModel model, CancellationToken cancellationToken)
    {
        var rememberMe = model.RememberMe;
        var role = BackendAuthService.NormalizeRole(user.Role);

        if (!user.MfaEnabled || string.IsNullOrWhiteSpace(user.MfaSecretProtected))
        {
            var session = await _mfaService.BeginEnrollmentAsync(user, cancellationToken);
            if (session is null)
            {
                ModelState.AddModelError(string.Empty, "No se pudo iniciar el enrolamiento MFA.");
                return View(nameof(Login), model);
            }

            await SignInPendingMfaAsync(user, rememberMe, MfaModeSetup, session.SetupKey, model.ReturnUrl, cancellationToken);
            return RedirectToAction(nameof(MfaSetup), new { returnUrl = model.ReturnUrl });
        }

        await SignInPendingMfaAsync(user, rememberMe, MfaModeChallenge, setupKey: string.Empty, model.ReturnUrl, cancellationToken);
        return RedirectToAction(nameof(MfaMethod), new { returnUrl = model.ReturnUrl });
    }

    private async Task SignInFinalUserAsync(AuthUser user, bool rememberMe, CancellationToken cancellationToken)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, BackendAuthService.NormalizeRole(user.Role)),
            new(RememberMeClaimType, rememberMe ? "true" : "false")
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var expiresUtc = rememberMe
            ? DateTimeOffset.UtcNow.AddDays(_configuration.GetValue<double?>("SessionSettings:RememberMeDays") ?? 30)
            : DateTimeOffset.UtcNow.AddMinutes(_configuration.GetValue<double?>("SessionSettings:IdleMinutes") ?? 15);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = rememberMe,
                AllowRefresh = true,
                ExpiresUtc = expiresUtc
            });
    }

    private async Task SignInPendingMfaAsync(
        AuthUser user,
        bool rememberMe,
        string mode,
        string setupKey,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, BackendAuthService.NormalizeRole(user.Role)),
            new(RememberMeClaimType, rememberMe ? "true" : "false"),
            new(MfaModeClaimType, mode),
            new(MfaUserIdClaimType, user.Id.ToString()),
            new(MfaReturnUrlClaimType, returnUrl ?? string.Empty)
        };

        if (!string.IsNullOrWhiteSpace(setupKey))
        {
            claims.Add(new Claim(MfaSetupKeyClaimType, setupKey));
        }

        var identity = new ClaimsIdentity(claims, PendingMfaScheme);
        var principal = new ClaimsPrincipal(identity);
        var expiresUtc = DateTimeOffset.UtcNow.AddMinutes(_configuration.GetValue<double?>("MfaSettings:PendingMinutes") ?? 10);

        await HttpContext.SignInAsync(
            PendingMfaScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = false,
                ExpiresUtc = expiresUtc
            });
    }

    private async Task<PendingMfaContext?> GetPendingMfaContextAsync(CancellationToken cancellationToken)
    {
        var authResult = await HttpContext.AuthenticateAsync(PendingMfaScheme);
        if (!authResult.Succeeded || authResult.Principal is null)
        {
            return null;
        }

        var userIdValue = authResult.Principal.FindFirstValue(MfaUserIdClaimType);
        if (!int.TryParse(userIdValue, out var userId))
        {
            return null;
        }

        return new PendingMfaContext(
            userId,
            authResult.Principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
            authResult.Principal.FindFirstValue(ClaimTypes.Role) ?? string.Empty,
            authResult.Principal.FindFirstValue(MfaModeClaimType) ?? string.Empty,
            authResult.Principal.FindFirstValue(MfaSetupKeyClaimType) ?? string.Empty,
            authResult.Principal.FindFirstValue(MfaReturnUrlClaimType) ?? string.Empty,
            string.Equals(authResult.Principal.FindFirstValue(RememberMeClaimType), "true", StringComparison.OrdinalIgnoreCase),
            authResult.Properties?.ExpiresUtc);
    }

    private sealed record PendingMfaContext(
        int UserId,
        string Username,
        string Role,
        string Mode,
        string SetupKey,
        string ReturnUrl,
        bool RememberMe,
        DateTimeOffset? ExpiresUtc);
}
