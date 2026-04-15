using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoteroMap.API.Models;
using SoteroMap.API.Services;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Controllers;

[AllowAnonymous]
public class AuthController : Controller
{
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
            new(ClaimTypes.Role, result.User.Role)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var sessionMinutes = _configuration.GetValue<double?>("SessionSettings:IdleMinutes") ?? 15;

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = model.RememberMe,
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(sessionMinutes)
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
    [HttpGet]
    public async Task<IActionResult> KeepAlive()
    {
        var authResult = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!authResult.Succeeded || authResult.Principal is null)
        {
            return Unauthorized();
        }

        var sessionMinutes = _configuration.GetValue<double?>("SessionSettings:IdleMinutes") ?? 15;
        var properties = authResult.Properties ?? new AuthenticationProperties();
        properties.AllowRefresh = true;
        properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(sessionMinutes);

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

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction("Index", "Admin");
    }
}
