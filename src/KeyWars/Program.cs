using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
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
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;

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

builder.Services.Configure<LdapOptions>(options => ConfigurationAliases.BindLdap(builder.Configuration, options));
builder.Services.Configure<AuthOptions>(options => ConfigurationAliases.BindAuth(builder.Configuration, options));
builder.Services.Configure<LiveOptions>(options => ConfigurationAliases.BindLive(builder.Configuration, options));
builder.Services.Configure<ChallengeOptions>(options => ConfigurationAliases.BindChallenges(builder.Configuration, options));
builder.Services.Configure<ContentOptions>(options => ConfigurationAliases.BindContent(builder.Configuration, options));

builder.Services.AddDbContext<KeyWarsDbContext>(options =>
{
    var connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        DefaultTimeout = 5
    }.ToString();
    options.UseSqlite(connectionString);
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
builder.Services.AddScoped<ProfileInsightsService>();
builder.Services.AddScoped<ProfilePrivacyService>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddSingleton<TypingEngine>();
builder.Services.AddSingleton<AttemptSessionStore>();
builder.Services.AddSingleton<ILiveRoomCompletionWriter, SqliteLiveRoomCompletionWriter>();
builder.Services.AddSingleton<LiveRoomCompletionQueue>();
builder.Services.AddSingleton<ILiveRoomCompletionSink>(services => services.GetRequiredService<LiveRoomCompletionQueue>());
builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<LiveRoomCompletionQueue>());
builder.Services.AddSingleton<ILiveProgressSender, SignalRLiveProgressSender>();
builder.Services.AddSingleton<LiveProgressBroadcaster>();
builder.Services.AddSingleton<LiveRoomManager>();
builder.Services.AddSingleton<LivePresenceTracker>();
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddHostedService<LiveRoomSweepService>();

var configuredAuthOptions = ConfigurationAliases.GetAuth(builder.Configuration);
var developmentLogin = builder.Environment.IsDevelopment();
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
        var authOptions = ConfigurationAliases.GetAuth(builder.Configuration);
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
                var returnUrl = Uri.EscapeDataString(context.Request.PathBase + context.Request.Path + context.Request.QueryString);
                context.Response.Redirect($"/anmelden?ReturnUrl={returnUrl}");
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                var returnUrl = Uri.EscapeDataString(context.Request.PathBase + context.Request.Path + context.Request.QueryString);
                context.Response.Redirect($"/anmelden?ReturnUrl={returnUrl}");
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
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("keywars-api", httpContext =>
    {
        var key = httpContext.User.FindFirstValue(KeyWarsClaims.ProfileId)
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 180,
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(1),
            AutoReplenishment = true
        });
    });
    options.AddPolicy("keywars-login", httpContext =>
    {
        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(1),
            AutoReplenishment = true
        });
    });
});
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
})
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    })
    .AddMessagePackProtocol();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    var knownProxies = builder.Configuration["KEYWARS:PROXY:KNOWN_PROXIES"];
    if (!string.IsNullOrWhiteSpace(knownProxies))
    {
        options.KnownProxies.Clear();
        foreach (var value in knownProxies.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(value, out var address))
            {
                options.KnownProxies.Add(address);
            }
        }
    }

    var knownNetworks = builder.Configuration["KEYWARS:PROXY:KNOWN_NETWORKS"];
    if (!string.IsNullOrWhiteSpace(knownNetworks))
    {
        options.KnownIPNetworks.Clear();
        foreach (var value in knownNetworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (System.Net.IPNetwork.TryParse(value, out var network))
            {
                options.KnownIPNetworks.Add(network);
            }
        }
    }
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
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/health/ready", async (KeyWarsDbContext db, CancellationToken cancellationToken) =>
{
    await db.Database.ExecuteSqlRawAsync("SELECT 1;", cancellationToken);
    return Results.Ok(new { status = "ok" });
}).AllowAnonymous();
app.MapGet("/health/arena-persistence", (LiveRoomCompletionQueue queue) => Results.Ok(new
{
    pendingJobs = queue.PendingCount,
    capacity = queue.Capacity,
    failedAttempts = queue.FailedAttempts
})).AllowAnonymous();
app.MapGet("/health/arena-progress", (LiveProgressBroadcaster progress) => Results.Ok(progress.Snapshot())).AllowAnonymous();

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
