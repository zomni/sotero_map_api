using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Infrastructure;
using SoteroMap.API.Data;
using SoteroMap.API.Models;
using SoteroMap.API.Services;

var builder = WebApplication.CreateBuilder(args);
var securitySettings = builder.Configuration.GetSection("SecuritySettings");
var corsSettings = builder.Configuration.GetSection("CorsSettings");
var mfaSettings = builder.Configuration.GetSection("MfaSettings");
var forceHttps = securitySettings.GetValue<bool?>("ForceHttps") ?? !builder.Environment.IsDevelopment();
var enableSwaggerInProduction = securitySettings.GetValue<bool?>("EnableSwaggerInProduction") ?? false;
var referrerPolicy = securitySettings["ReferrerPolicy"] ?? "strict-origin-when-cross-origin";
var permissionsPolicy = securitySettings["PermissionsPolicy"] ?? "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
var contentSecurityPolicy = securitySettings["ContentSecurityPolicy"] ?? "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; img-src 'self' data: blob: https:; style-src 'self' 'unsafe-inline' https:; script-src 'self' 'unsafe-inline' 'unsafe-eval' https:; connect-src 'self' https: http: ws: wss:; font-src 'self' data: https:";
var cookieSecurePolicyValue = securitySettings["CookieSecurePolicy"];
var cookieSameSiteValue = securitySettings["CookieSameSite"];

static CookieSecurePolicy ParseCookieSecurePolicy(string? value, CookieSecurePolicy fallback)
{
    if (Enum.TryParse<CookieSecurePolicy>(value, ignoreCase: true, out var parsed))
    {
        return parsed;
    }

    return fallback;
}

static SameSiteMode ParseSameSiteMode(string? value, SameSiteMode fallback)
{
    if (Enum.TryParse<SameSiteMode>(value, ignoreCase: true, out var parsed))
    {
        return parsed;
    }

    return fallback;
}

// Controllers con vistas Razor (MVC) + API
var mvcBuilder = builder.Services.AddControllersWithViews();
if (builder.Environment.IsDevelopment())
{
    mvcBuilder.AddRazorRuntimeCompilation();
}
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSwaggerGen();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        var sessionMinutes = builder.Configuration.GetValue<double?>("SessionSettings:IdleMinutes") ?? 15;
        options.LoginPath = "/Auth/Login";
        options.AccessDeniedPath = "/Auth/AccessDenied";
        options.Cookie.Name = "SoteroMap.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = ParseSameSiteMode(cookieSameSiteValue, SameSiteMode.Lax);
        options.Cookie.SecurePolicy = ParseCookieSecurePolicy(
            cookieSecurePolicyValue,
            builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always);
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(sessionMinutes);
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                var publicPath = context.Request.Headers.TryGetValue("X-Sotero-Public-Path", out var value)
                    ? value.ToString()
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(publicPath))
                {
                    var returnUrl = $"{publicPath}{context.Request.QueryString}";
                    var loginUrl = $"{options.LoginPath}?ReturnUrl={Uri.EscapeDataString(returnUrl)}";
                    context.Response.Redirect(loginUrl);
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthentication()
    .AddCookie("MfaPending", options =>
    {
        var pendingMinutes = mfaSettings.GetValue<double?>("PendingMinutes") ?? 10;
        options.Cookie.Name = "SoteroMap.MfaPending";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.SlidingExpiration = false;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(pendingMinutes);
        options.LoginPath = "/Auth/Login";
        options.AccessDeniedPath = "/Auth/Login";
    });
builder.Services.AddAuthorization();

// SQLite local: usa una ruta unica y portable, tambien en Docker.
var resolvedSqliteConnectionString = SqliteDatabasePathResolver.ResolveConnectionString(
    builder.Configuration,
    builder.Environment.ContentRootPath);
builder.Configuration["ConnectionStrings:Default"] = resolvedSqliteConnectionString;
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(resolvedSqliteConnectionString));
builder.Services.AddScoped<IPasswordHasher<AuthUser>, PasswordHasher<AuthUser>>();
builder.Services.AddScoped<LdapAuthenticationService>();
builder.Services.AddScoped<MfaService>();
builder.Services.AddScoped<BackendAuthService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<DatabaseBackupService>();
builder.Services.AddHostedService<DatabaseBackupHostedService>();
builder.Services.AddScoped<FrontendSyncService>();
builder.Services.AddScoped<ExcelInventoryImportService>();
builder.Services.AddScoped<InventoryReconciliationService>();
builder.Services.AddScoped<EquipmentDeliveryDocumentService>();

// CORS para que el frontend (sotero_map) pueda consumir la API
builder.Services.AddCors(options =>
{
    options.AddPolicy("FrontendPolicy", policy =>
    {
        var allowedOrigins = (corsSettings["AllowedOrigins"] ?? builder.Configuration["AllowedOrigins"] ?? "http://localhost:8080,http://localhost:3000")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

// Seed data al iniciar
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // Si el proyecto aun no tiene migraciones EF, EnsureCreated permite
    // levantar la base SQLite por primera vez sin romper el arranque.
    if (context.Database.GetMigrations().Any())
    {
        await context.Database.MigrateAsync();
    }
    else
    {
        await context.Database.EnsureCreatedAsync();
    }

    await ExtendedSchemaInitializer.EnsureAsync(context);
    await SeedData.InitializeAsync(context);
    var authService = scope.ServiceProvider.GetRequiredService<BackendAuthService>();
    await authService.EnsureSeedUsersAsync();
}

app.UseCors("FrontendPolicy");
if (forceHttps && !app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/dashboard", out var dashboardRemaining))
    {
        context.Request.Headers["X-Sotero-Public-Path"] = context.Request.Path.ToString();
        context.Request.Path = new PathString($"/admin{dashboardRemaining}");
        await next();
        return;
    }

    if (
        HttpMethods.IsGet(context.Request.Method) &&
        context.Request.Path.StartsWithSegments("/admin", out var adminRemaining))
    {
        var target = $"/dashboard{adminRemaining}{context.Request.QueryString}";
        context.Response.Redirect(target, permanent: false);
        return;
    }

    await next();
});
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var path = context.Request.Path;
        var disableCache = path == "/"
            || path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/dashboard", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/Auth", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

        if (disableCache)
        {
            context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["Expires"] = "0";
        }

        return Task.CompletedTask;
    });

    await next();
});
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = referrerPolicy;
        context.Response.Headers["Permissions-Policy"] = permissionsPolicy;
        context.Response.Headers["Content-Security-Policy"] = contentSecurityPolicy;
        return Task.CompletedTask;
    });

    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.Use(async (context, next) =>
{
    await next();

    if (context.Response.StatusCode == StatusCodes.Status403Forbidden && context.User.Identity?.IsAuthenticated == true)
    {
        try
        {
            var auditLogService = context.RequestServices.GetRequiredService<AuditLogService>();
            await auditLogService.LogSecurityEventAsync(
                actionType: "access-denied",
                resource: context.Request.Path.Value ?? string.Empty,
                summary: $"Acceso denegado a {context.Request.Path.Value ?? "/"}",
                details: $"Metodo {context.Request.Method}; Query {context.Request.QueryString}",
                result: "failure",
                severity: "warning",
                changedByUsername: context.User.Identity?.Name ?? "usuario",
                cancellationToken: context.RequestAborted);
        }
        catch
        {
            // No interrumpimos la respuesta si la auditoria falla.
        }
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseWhen(
        context => context.Request.Path.StartsWithSegments("/swagger"),
        branch =>
        {
            branch.Use(async (context, next) =>
            {
                if (context.User.Identity?.IsAuthenticated != true)
                {
                    await context.ChallengeAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return;
                }

                await next();
            });
        });

    app.UseSwagger();
    app.UseSwaggerUI();
}
else if (enableSwaggerInProduction)
{
    app.UseWhen(
        context => context.Request.Path.StartsWithSegments("/swagger"),
        branch =>
        {
            branch.Use(async (context, next) =>
            {
                if (context.User.Identity?.IsAuthenticated != true)
                {
                    await context.ChallengeAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return;
                }

                if (!context.User.IsInRole(AppRoles.Admin))
                {
                    await context.ForbidAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return;
                }

                await next();
            });
        });

    app.UseSwagger();
    app.UseSwaggerUI();
}

// Ruta para Admin (MVC con Razor)
app.MapControllerRoute(
    name: "admin",
    pattern: "admin/{action=Index}/{id?}",
    defaults: new { controller = "Admin" });

// Ruta default
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Admin}/{action=Index}/{id?}");

// Ruta API REST
app.MapControllers();

app.Run();

