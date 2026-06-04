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
    private const string RememberMeClaimType = "sotero:remember_me";
    private readonly BackendAuthService _authService;
    private readonly IConfiguration _configuration;

    public AuthController(BackendAuthService authService, IConfiguration configuration)
    {
        _authService = authService;
        _configuration = configuration;
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
            if (result.LockedUntilUtc.HasValue)
            {
                ModelState.AddModelError(string.Empty,
                    $"Cuenta bloqueada hasta {result.LockedUntilUtc.Value.ToLocalTime():dd/MM/yyyy HH:mm}.");
            }
            else
            {
                ModelState.AddModelError(string.Empty, result.ErrorMessage);
            }

            return View(model);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.User.Id.ToString()),
            new(ClaimTypes.Name, result.User.Username),
            new(ClaimTypes.Role, result.User.Role),
            new(RememberMeClaimType, model.RememberMe ? "true" : "false")
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var expiresUtc = model.RememberMe
            ? DateTimeOffset.UtcNow.AddDays(_configuration.GetValue<double?>("SessionSettings:RememberMeDays") ?? 30)
            : DateTimeOffset.UtcNow.AddMinutes(_configuration.GetValue<double?>("SessionSettings:IdleMinutes") ?? 15);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = model.RememberMe,
                AllowRefresh = true,
                ExpiresUtc = expiresUtc
            });

        return RedirectToLocal(model.ReturnUrl);
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [Authorize]
    [HttpPost("/api/auth/logout")]
    public async Task<IActionResult> ApiLogout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { signedOut = true });
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
}
