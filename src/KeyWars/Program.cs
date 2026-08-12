using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Hubs;
using KeyWars.Infrastructure;
using KeyWars.Infrastructure.Cluster;
using KeyWars.Infrastructure.Observability;
using KeyWars.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using StackExchange.Redis;

if (args is ["healthcheck", ..])
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    var response = await client.GetAsync("http://127.0.0.1:8080/health/ready");
    return response.IsSuccessStatusCode ? 0 : 1;
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

var germanCulture = CultureInfo.GetCultureInfo("de-DE");

var startupLogger = LoggerFactory.Create(logging => logging.AddConsole()).CreateLogger("Startup");
var topology = RuntimeTopology.Resolve(builder.Configuration);
if (!topology.IsCluster || topology.HostsApplication)
{
    StartupValidator.Validate(
        builder.Configuration,
        builder.Environment,
        startupLogger,
        ensureDataDirectory: !topology.IsCluster);
}

string? dataDirectory = null;
string? databasePath = null;
ConnectionMultiplexer? clusterRedis = null;
ConfigurationOptions? redisConfiguration = null;
if (topology.IsCluster)
{
    redisConfiguration = ConfigurationOptions.Parse(topology.RedisConnectionString!, true);
    redisConfiguration.AbortOnConnectFail = true;
    redisConfiguration.ConnectTimeout = Math.Clamp(redisConfiguration.ConnectTimeout, 1_000, 15_000);
    clusterRedis = await ConnectionMultiplexer.ConnectAsync(redisConfiguration);
    builder.Services.AddSingleton<IConnectionMultiplexer>(clusterRedis);
    builder.Services.AddSingleton(services =>
        new RedisConnectionLifetime(services.GetRequiredService<IConnectionMultiplexer>()));
    builder.Services.AddSingleton<IMaintenanceLease, RedisMaintenanceLease>();
}
else
{
    dataDirectory = DataPaths.Resolve(builder.Configuration, builder.Environment);
    databasePath = DataPaths.DatabasePath(dataDirectory);
    builder.Services.AddSingleton<IMaintenanceLease, SingleNodeMaintenanceLease>();
}

builder.Services.AddSingleton(topology);
builder.Services.AddKeyWarsObservability(builder.Configuration);

builder.Services.Configure<LdapOptions>(options => ConfigurationAliases.BindLdap(builder.Configuration, options));
builder.Services.Configure<AuthOptions>(options => ConfigurationAliases.BindAuth(builder.Configuration, options));
builder.Services.Configure<ContentModerationOptions>(options => ConfigurationAliases.BindModeration(builder.Configuration, options));
builder.Services.Configure<LiveOptions>(options => ConfigurationAliases.BindLive(builder.Configuration, options));
builder.Services.Configure<ChallengeOptions>(options => ConfigurationAliases.BindChallenges(builder.Configuration, options));
builder.Services.Configure<ContentOptions>(options => ConfigurationAliases.BindContent(builder.Configuration, options));
builder.Services.Configure<RetentionOptions>(options => ConfigurationAliases.BindRetention(builder.Configuration, options));
ConfigurationAliases.GetRetention(builder.Configuration);
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(germanCulture);
    options.SupportedCultures = [germanCulture];
    options.SupportedUICultures = [germanCulture];
    options.ApplyCurrentCultureToResponseHeaders = true;
    options.RequestCultureProviders.Clear();
});

if (topology.DatabaseProvider == KeyWarsDatabaseProvider.Sqlite)
{
    builder.Services.AddDbContext<KeyWarsDbContext>(options =>
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            DefaultTimeout = 5
        }.ToString();
        options.UseSqlite(connectionString);
    });
}
else
{
    builder.Services.AddDbContext<PostgresKeyWarsDbContext>(options =>
        options.UseNpgsql(
            topology.DatabaseConnectionString,
            postgres => postgres.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null)));
    builder.Services.AddScoped<KeyWarsDbContext>(services =>
        services.GetRequiredService<PostgresKeyWarsDbContext>());
}

var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName(topology.DataProtectionApplicationName);
if (topology.IsCluster)
{
    dataProtection.PersistKeysToStackExchangeRedis(clusterRedis!, "keywars:dataprotection:keys");
}
else
{
    dataProtection.PersistKeysToFileSystem(
        new DirectoryInfo(Path.Combine(dataDirectory!, "dataprotection-keys")));
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DatabaseRuntimeLock>();
if (topology.IsCluster)
{
    builder.Services.AddSingleton<IProfileAccessGate, RedisProfileAccessGate>();
    builder.Services.AddSingleton<ISharedRateLimiter, RedisSharedRateLimiter>();
    builder.Services.AddSingleton<IChallengeLockProvider, RedisChallengeLockProvider>();
}
else
{
    builder.Services.AddSingleton<ProfileAccessGate>();
    builder.Services.AddSingleton<IProfileAccessGate>(services =>
        services.GetRequiredService<ProfileAccessGate>());
    builder.Services.AddSingleton<ISharedRateLimiter, SingleNodeSharedRateLimiter>();
    builder.Services.AddSingleton<IChallengeLockProvider, LocalChallengeLockProvider>();
}
builder.Services.AddSingleton<ProfileAccessHubFilter>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<ProfileProvisioner>();
builder.Services.AddScoped<TextLibraryService>();
builder.Services.AddScoped<AttemptService>();
builder.Services.AddScoped<ChallengeService>();
builder.Services.AddScoped<GamificationEventWriter>();
builder.Services.AddScoped<MotivationService>();
builder.Services.AddScoped<ProfileInsightsService>();
builder.Services.AddScoped<CompetitionLeaderboardService>();
builder.Services.AddScoped<ProfilePrivacyService>();
builder.Services.AddScoped<ProfileExportService>();
builder.Services.AddScoped<ContentModerationService>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<DataRetentionService>();
builder.Services.AddSingleton<TypingEngine>();
if (topology.IsCluster)
{
    builder.Services.AddSingleton<IAttemptSessionStateStore, RedisAttemptSessionStateStore>();
}
else
{
    builder.Services.AddSingleton<AttemptSessionStore>();
    builder.Services.AddSingleton<IAttemptSessionStateStore>(services =>
        services.GetRequiredService<AttemptSessionStore>());
}

builder.Services.AddSingleton<ILiveRoomCompletionWriter, RelationalLiveRoomCompletionWriter>();
if (topology.IsCluster)
{
    builder.Services.AddSingleton<RedisLiveRoomCompletionQueue>();
    builder.Services.AddSingleton<ClusterLiveRoomCompletionSink>();
    builder.Services.AddSingleton<ILiveRoomCompletionSink>(services =>
        services.GetRequiredService<ClusterLiveRoomCompletionSink>());
    builder.Services.AddSingleton<ILiveRoomCompletionDrain>(services =>
        services.GetRequiredService<RedisLiveRoomCompletionQueue>());
    builder.Services.AddSingleton<ILiveRoomCompletionMonitor>(services =>
        services.GetRequiredService<RedisLiveRoomCompletionQueue>());
    if (topology.RunsWorkers)
    {
        builder.Services.AddSingleton<IHostedService>(services =>
            services.GetRequiredService<RedisLiveRoomCompletionQueue>());
    }
}
else
{
    builder.Services.AddSingleton<LiveRoomCompletionQueue>();
    builder.Services.AddSingleton<ILiveRoomCompletionSink>(services =>
        services.GetRequiredService<LiveRoomCompletionQueue>());
    builder.Services.AddSingleton<ILiveRoomCompletionDrain>(services =>
        services.GetRequiredService<LiveRoomCompletionQueue>());
    builder.Services.AddSingleton<ILiveRoomCompletionMonitor>(services =>
        services.GetRequiredService<LiveRoomCompletionQueue>());
    if (topology.HostsArena)
    {
        builder.Services.AddSingleton<IHostedService>(services =>
            services.GetRequiredService<LiveRoomCompletionQueue>());
    }
}

builder.Services.AddSingleton<ILiveProgressSender, SignalRLiveProgressSender>();
builder.Services.AddSingleton<LiveProgressBroadcaster>();
builder.Services.AddSingleton<ILiveRoomUpdateSender, SignalRLiveRoomUpdateSender>();
builder.Services.AddSingleton<LiveReactionService>();
builder.Services.AddSingleton<LiveRoomManager>();
if (topology.IsCluster)
{
    builder.Services.AddSingleton<RedisLiveProgressRelay>();
    builder.Services.AddSingleton<RedisLiveRoomDispatcher>();
    builder.Services.AddSingleton<ILiveRoomDispatcher>(services =>
        services.GetRequiredService<RedisLiveRoomDispatcher>());
    builder.Services.AddSingleton<ILiveRoomStateCoordinator>(services =>
        services.GetRequiredService<RedisLiveRoomDispatcher>());
    if (topology.HostsArena)
    {
        builder.Services.AddSingleton<IHostedService>(services =>
            services.GetRequiredService<RedisLiveProgressRelay>());
    }
}
else
{
    builder.Services.AddSingleton<ILiveRoomDispatcher, LocalLiveRoomDispatcher>();
    builder.Services.AddSingleton<ILiveRoomStateCoordinator, SingleNodeLiveRoomStateCoordinator>();
}

if (topology.IsCluster)
{
    builder.Services.AddSingleton<ILivePresenceStateStore, RedisLivePresenceStateStore>();
}
else
{
    builder.Services.AddSingleton<LivePresenceTracker>();
    builder.Services.AddSingleton<ILivePresenceStateStore>(services =>
        services.GetRequiredService<LivePresenceTracker>());
}

builder.Services.AddScoped<DatabaseInitializer>();
if (topology.HostsArena)
{
    builder.Services.AddHostedService<LiveRoomSweepService>();
}

if (!topology.IsCluster && topology.Role == RuntimeRole.All ||
    topology.IsCluster && topology.Role is RuntimeRole.All or RuntimeRole.Worker)
{
    builder.Services.AddHostedService<DataRetentionHostedService>();
}

var configuredAuthOptions = ConfigurationAliases.GetAuth(builder.Configuration);
var developmentLogin = builder.Environment.IsDevelopment() && configuredAuthOptions.DevelopmentLogin;
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
            OnValidatePrincipal = async context =>
            {
                var profileIdValue = context.Principal?.FindFirstValue(KeyWarsClaims.ProfileId);
                if (!Guid.TryParse(profileIdValue, out var profileId))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return;
                }

                var accessGate = context.HttpContext.RequestServices.GetRequiredService<IProfileAccessGate>();
                var profileIsValid = await accessGate.GetStateAsync(
                    profileId,
                    context.HttpContext.RequestAborted) != ProfileAccessState.Deleted;
                if (profileIsValid)
                {
                    var db = context.HttpContext.RequestServices.GetRequiredService<KeyWarsDbContext>();
                    profileIsValid = await db.UserProfiles
                        .AsNoTracking()
                        .AnyAsync(profile => profile.Id == profileId && !profile.Deleted, context.HttpContext.RequestAborted);
                }

                if (!profileIsValid)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            },
            OnRedirectToLogin = context =>
            {
                var returnUrl = Uri.EscapeDataString(context.Request.PathBase + context.Request.Path + context.Request.QueryString);
                context.Response.Redirect($"/anmelden?ReturnUrl={returnUrl}");
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                if (context.HttpContext.User.Identity?.IsAuthenticated == true)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }

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

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(KeyWarsPolicies.ContentModerator, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(KeyWarsClaims.ContentModerator, "true"));
});
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
        if (!HttpMethods.IsPost(httpContext.Request.Method))
        {
            return RateLimitPartition.GetFixedWindowLimiter("login-page", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10_000,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1),
                AutoReplenishment = true
            });
        }

        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = developmentLogin ? 200 : 10,
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

var signalR = builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 16 * 1024;
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.AddFilter(typeof(ProfileAccessHubFilter));
})
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    })
    .AddMessagePackProtocol();
if (topology.IsCluster)
{
    signalR.AddStackExchangeRedis(options => options.Configuration = redisConfiguration!);
}

var forwardedHeaders = ConfigurationAliases.GetForwardedHeaders(builder.Configuration);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = forwardedHeaders.ForwardedHeaders;
    options.ForwardLimit = forwardedHeaders.ForwardLimit;
    options.KnownProxies.Clear();
    foreach (var address in forwardedHeaders.KnownProxies)
    {
        options.KnownProxies.Add(address);
    }

    options.KnownIPNetworks.Clear();
    foreach (var network in forwardedHeaders.KnownIPNetworks)
    {
        options.KnownIPNetworks.Add(network);
    }
});

var app = builder.Build();
if (topology.IsCluster)
{
    _ = app.Services.GetRequiredService<RedisConnectionLifetime>();
}

if (args is ["maintenance", "backup", ..])
{
    if (topology.IsCluster)
    {
        throw new InvalidOperationException(
            "maintenance backup ist nur für SQLite vorgesehen. PostgreSQL muss mit pg_dump gesichert werden.");
    }

    await using var scope = app.Services.CreateAsyncScope();
    var backup = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateBackupAsync();
    Console.WriteLine(backup);
    return 0;
}

if (args is ["maintenance", "restore", var backupPath, ..])
{
    if (topology.IsCluster)
    {
        throw new InvalidOperationException(
            "maintenance restore ist nur für SQLite vorgesehen. PostgreSQL muss mit pg_restore wiederhergestellt werden.");
    }

    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<BackupService>().RestoreAsync(backupPath);
    Console.WriteLine("Restore abgeschlossen.");
    return 0;
}

if (args is ["maintenance", "retention", ..])
{
    var apply = args is ["maintenance", "retention", "--apply"];
    if (!apply && args is not ["maintenance", "retention"])
    {
        throw new InvalidOperationException("Erlaubt sind maintenance retention und maintenance retention --apply.");
    }

    using var retentionFileLease = apply && topology.UsesRuntimeFileLock
        ? app.Services.GetRequiredService<DatabaseRuntimeLock>().Acquire("retention")
        : null;
    await using var retentionClusterLease = apply && topology.IsCluster
        ? await app.Services.GetRequiredService<IMaintenanceLease>().TryAcquireAsync("retention")
            ?? throw new InvalidOperationException("Ein anderer Cluster-Worker führt bereits Retention aus.")
        : null;
    await using var scope = app.Services.CreateAsyncScope();
    var report = await scope.ServiceProvider
        .GetRequiredService<DataRetentionService>()
        .RunAsync(dryRun: !apply);
    Console.WriteLine(JsonSerializer.Serialize(
        report,
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    return 0;
}

if (topology.Role == RuntimeRole.Migrate)
{
    using var migrationLease = topology.UsesRuntimeFileLock
        ? app.Services.GetRequiredService<DatabaseRuntimeLock>().Acquire("migrate")
        : null;
    await using var migrationScope = app.Services.CreateAsyncScope();
    await migrationScope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
    return 0;
}

using var databaseRuntimeLease = topology.UsesRuntimeFileLock
    ? app.Services.GetRequiredService<DatabaseRuntimeLock>().Acquire("webhost")
    : null;

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseForwardedHeaders();
app.UseRequestLocalization();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<ClusterRateLimitMiddleware>();
app.UseMiddleware<ProfileAccessMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

if (topology.Role == RuntimeRole.Web)
{
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/arena") ||
            path.StartsWithSegments("/hubs/arena") ||
            path.StartsWithSegments("/api/arena") ||
            path.StartsWithSegments("/profil/loeschen") ||
            path.StartsWithSegments("/profil/statistik-zuruecksetzen"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    });
}

app.MapGet("/health/live", () => Results.Ok(new
{
    status = "ok",
    role = topology.Role.ToString().ToLowerInvariant()
})).AllowAnonymous();
app.MapGet("/health/ready", async (
    KeyWarsDbContext db,
    KeyWarsTelemetry telemetry,
    ILiveRoomCompletionSink completionSink,
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    var stopwatch = Stopwatch.StartNew();
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1;", cancellationToken);
        var pendingMigrations = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
        var redisReady = true;
        if (topology.IsCluster)
        {
            var redis = services.GetRequiredService<IConnectionMultiplexer>();
            redisReady = await redis.GetDatabase().PingAsync() < TimeSpan.FromSeconds(2);
        }

        var arenaReady = !topology.HostsArena ||
            services.GetRequiredService<ILiveRoomStateCoordinator>().IsAuthoritative;
        var drainReady = !topology.HostsArena ||
            completionSink.CanAcceptNewRoom(0);
        var ready = pendingMigrations.Length == 0 && redisReady && arenaReady && drainReady;
        telemetry.RecordDatabaseOperation(
            topology.DatabaseProvider.ToString().ToLowerInvariant(),
            "readiness",
            ready ? "success" : "not_ready",
            stopwatch.Elapsed);
        return Results.Json(
            new
            {
                status = ready ? "ok" : "not-ready",
                role = topology.Role.ToString().ToLowerInvariant(),
                database = topology.DatabaseProvider.ToString().ToLowerInvariant(),
                pendingMigrations = pendingMigrations.Length,
                redis = redisReady,
                arenaAuthority = arenaReady,
                completionDrain = drainReady
            },
            statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        telemetry.RecordDatabaseOperation(
            topology.DatabaseProvider.ToString().ToLowerInvariant(),
            "readiness",
            "failure",
            stopwatch.Elapsed);
        return Results.Json(
            new
            {
                status = "not-ready",
                role = topology.Role.ToString().ToLowerInvariant(),
                database = topology.DatabaseProvider.ToString().ToLowerInvariant()
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous();
if (topology.HostsArena)
{
    app.MapGet("/health/arena-persistence", (ILiveRoomCompletionMonitor monitor) =>
    {
        var metrics = monitor.GetMetrics();
        return Results.Ok(new
        {
            metrics.PendingJobs,
            monitor.Capacity,
            failedAttempts = monitor.FailedAttempts,
            metrics.FailedRecords,
            metrics.RetryAttempts,
            metrics.PersistedCompletions,
            metrics.FailedCompletions,
            metrics.AbortedUnconfirmedCompletions,
            metrics.AveragePersistenceDurationMilliseconds
        });
    }).AllowAnonymous();
    app.MapGet("/health/arena-progress", (LiveProgressBroadcaster progress) => Results.Ok(progress.Snapshot())).AllowAnonymous();
}

app.MapPrometheusScrapingEndpoint("/metrics");
if (topology.HostsApplication)
{
    app.MapKeyWarsApi();
    if (topology.HostsArena)
    {
        app.MapHub<ArenaHub>("/hubs/arena");
    }

    app.MapRazorPages();
}

if (topology.RunsMigrations)
{
    await using var initializationScope = app.Services.CreateAsyncScope();
    await initializationScope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
}
await app.RunAsync();
return 0;

public partial class Program
{
}
