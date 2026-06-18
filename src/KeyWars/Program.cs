using System.Security.Claims;
using System.Text.Json.Serialization;
using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Hubs;
using KeyWars.Infrastructure;
using KeyWars.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

if (args is ["healthcheck", ..])
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    var response = await client.GetAsync("http://127.0.0.1:8080/health/ready");
    return response.IsSuccessStatusCode ? 0 : 1;
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

var startupLogger = LoggerFactory.Create(logging => logging.AddConsole()).CreateLogger("Startup");
StartupValidator.Validate(builder.Configuration, builder.Environment, startupLogger);

var dataDirectory = DataPaths.Resolve(builder.Configuration, builder.Environment);
var databasePath = DataPaths.DatabasePath(dataDirectory);

builder.Services.Configure<LdapOptions>(builder.Configuration.GetSection("KEYWARS:LDAP"));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("KEYWARS:AUTH"));
builder.Services.Configure<LiveOptions>(builder.Configuration.GetSection("KEYWARS:LIVE"));
builder.Services.Configure<ChallengeOptions>(builder.Configuration.GetSection("KEYWARS:CHALLENGES"));
builder.Services.Configure<ContentOptions>(builder.Configuration.GetSection("KEYWARS:CONTENT"));

builder.Services.AddDbContext<KeyWarsDbContext>(options =>
{
    options.UseSqlite($"Data Source={databasePath}");
});

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "dataprotection-keys")))
    .SetApplicationName("KeyWars");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<ProfileProvisioner>();
builder.Services.AddScoped<TextLibraryService>();
builder.Services.AddScoped<AttemptService>();
builder.Services.AddScoped<ChallengeService>();
builder.Services.AddScoped<MotivationService>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddSingleton<TypingEngine>();
builder.Services.AddSingleton<AttemptSessionStore>();
builder.Services.AddSingleton<LiveRoomManager>();
builder.Services.AddScoped<DatabaseInitializer>();

var developmentLogin = builder.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("KEYWARS:AUTH:DEVELOPMENT_LOGIN");
if (developmentLogin)
{
    builder.Services.AddScoped<ILdapAuthenticator, DevelopmentDirectoryAuthenticator>();
}
else
{
    builder.Services.AddScoped<ILdapAuthenticator, LdapAuthenticator>();
}

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        var authOptions = builder.Configuration.GetSection("KEYWARS:AUTH").Get<AuthOptions>() ?? new AuthOptions();
        options.Cookie.Name = builder.Environment.IsProduction() ? "__Host-KeyWars.Auth" : "KeyWars.Dev.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = TimeSpan.FromHours(Math.Clamp(authOptions.CookieLifetimeHours, 1, 12));
        options.SlidingExpiration = true;
        options.LoginPath = "/anmelden";
        options.LogoutPath = "/abmelden";
        options.AccessDeniedPath = "/anmelden";
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                context.Response.Redirect("/anmelden");
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                context.Response.Redirect("/anmelden");
                return Task.CompletedTask;
            },
            OnRedirectToLogout = context =>
            {
                context.Response.Redirect("/abmelden");
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = builder.Environment.IsProduction() ? "__Host-KeyWars.AntiForgery" : "KeyWars.Dev.AntiForgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.Cookie.Path = "/";
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Anmelden");
    options.Conventions.AllowAnonymousToPage("/Error");
});

builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 16 * 1024;
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
}).AddMessagePackProtocol();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

if (args is ["maintenance", "backup", ..])
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
    var backup = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateBackupAsync();
    Console.WriteLine(backup);
    return 0;
}

if (args is ["maintenance", "restore", var backupPath, ..])
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<BackupService>().RestoreAsync(backupPath);
    Console.WriteLine("Restore abgeschlossen.");
    return 0;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseForwardedHeaders();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/health/ready", async (KeyWarsDbContext db, CancellationToken cancellationToken) =>
{
    await db.Database.ExecuteSqlRawAsync("SELECT 1;", cancellationToken);
    return Results.Ok(new { status = "ok" });
}).AllowAnonymous();

app.MapKeyWarsApi();
app.MapHub<ArenaHub>("/hubs/arena");
app.MapRazorPages();

await using (var initializationScope = app.Services.CreateAsyncScope())
{
    await initializationScope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
}
await app.RunAsync();
return 0;

public partial class Program
{
}
