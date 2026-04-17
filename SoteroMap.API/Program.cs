using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Infrastructure;
using SoteroMap.API.Data;
using SoteroMap.API.Models;
using SoteroMap.API.Services;

var builder = WebApplication.CreateBuilder(args);

// Controllers con vistas Razor (MVC) + API
var mvcBuilder = builder.Services.AddControllersWithViews();
if (builder.Environment.IsDevelopment())
{
    mvcBuilder.AddRazorRuntimeCompilation();
}
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddMemoryCache();
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
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(sessionMinutes);
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
builder.Services.AddScoped<BackendAuthService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<FrontendSyncService>();
builder.Services.AddScoped<ExcelInventoryImportService>();
builder.Services.AddScoped<InventoryReconciliationService>();
builder.Services.AddScoped<EquipmentDeliveryDocumentService>();

// CORS para que el frontend (sotero_map) pueda consumir la API
builder.Services.AddCors(options =>
{
    options.AddPolicy("FrontendPolicy", policy =>
    {
        var allowedOrigins = (builder.Configuration["AllowedOrigins"] ?? "http://localhost:3000")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod();
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
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var path = context.Request.Path;
        var disableCache = path == "/"
            || path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase)
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
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

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

