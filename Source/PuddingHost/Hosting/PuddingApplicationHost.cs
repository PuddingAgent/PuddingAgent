using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using PuddingAgent.Services;
using PuddingCode.Configuration;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Services;
using Serilog;
using System.IO.Compression;
using System.Text;

namespace PuddingHost.Hosting;

/// <summary>
/// PuddingHost composition root.
/// Provides CreateBuilder, Build, InitializeAsync, and CaptureBoundAddresses
/// shared by Console and Desktop hosts.
/// 
/// Calling order: CreateBuilder → Build → InitializeAsync → StartAsync → CaptureBoundAddresses
/// </summary>
public static class PuddingApplicationHost
{
    /// <summary>
    /// Phase 1: Resolve DataRoot, configure Serilog, create WebApplicationBuilder,
    /// register all services, configure JWT/CORS/Controllers.
    /// </summary>
    public static WebApplicationBuilder CreateBuilder(
        string[] args,
        PuddingHostOptions options)
    {
        // ── DataRoot resolution and directory preparation ─────
        var dataRoot = string.IsNullOrWhiteSpace(options.DataRoot)
            ? PuddingDataRootBootstrapper.ResolveDataRoot(args)
            : options.DataRoot;

        var dataPaths = PuddingDataRootBootstrapper.Bootstrap(dataRoot);

        // ── Bootstrap configuration ─────────────────────────
        var aspnetcoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var bootstrapConfiguration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{aspnetcoreEnvironment}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        // ── Serilog ──────────────────────────────────────────
        PuddingLoggingBootstrapper.Configure(dataPaths, bootstrapConfiguration);

        // ── WebApplicationBuilder ────────────────────────────
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton(dataPaths);

        // ── DI validation ────────────────────────────────────
        builder.Host.UseDefaultServiceProvider(o =>
        {
            o.ValidateScopes = true;
            o.ValidateOnBuild = true;
        });
        builder.Host.UseSerilog();

        // ── URL binding ─────────────────────────────────────
        if (options.Urls.Count > 0)
        {
            builder.WebHost.UseUrls(options.Urls.ToArray());
        }
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
        {
            builder.WebHost.UseUrls("http://0.0.0.0:8080");
        }

        // ── HTTP 请求日志 ────────────────────────────────────
        builder.Services.AddHttpLogging(o =>
        {
            o.LoggingFields = Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestPath
                            | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestMethod
                            | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestQuery
                            | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.ResponseStatusCode
                            | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.Duration;
        });

        // ── Response compression ─────────────────────────────
        // Cold Workbench assets and diagnostic JSON responses are large enough
        // that avoiding multi-megabyte loopback copies improves startup/polling.
        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });
        builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
            options.Level = CompressionLevel.Fastest);
        builder.Services.Configure<GzipCompressionProviderOptions>(options =>
            options.Level = CompressionLevel.Fastest);

        // ── CORS ─────────────────────────────────────────────
        var corsOrigins = (builder.Configuration["Cors:AllowedOrigins"]
            ?? "http://localhost:8000;http://localhost:8001;http://localhost:8004;http://localhost:3000;http://localhost:8080")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        builder.Services.AddCors(opt =>
        {
            opt.AddPolicy("AdminSpa", policy =>
                policy.WithOrigins(corsOrigins)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials());
        });

        // ── Controllers with ApplicationParts ───────────────
        var mvcBuilder = builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(PuddingHostAssemblyMarker).Assembly)
            .AddApplicationPart(typeof(BootstrapApiController).Assembly)
            .AddApplicationPart(typeof(PuddingRuntime.Controllers.RuntimeSessionController).Assembly);

        // ── JWT ──────────────────────────────────────────────
        var jwtKey = builder.Configuration["Jwt:Key"]
            ?? "Pudding-Platform-JWT-DevKey-MUST-CHANGE-IN-PRODUCTION-32PLUS!";

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwtOpts =>
            {
                jwtOpts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "pudding-platform",
                    ValidateAudience = true,
                    ValidAudience = builder.Configuration["Jwt:Audience"] ?? "pudding-admin",
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
            });
        builder.Services.AddAuthorization();

        // ── Host options ────────────────────────────────────
        builder.Services.AddSingleton(options);

        // ── Server address accessor (Desktop mode) ──────────
        builder.Services.AddSingleton<IPuddingServerAddressAccessor, PuddingServerAddressAccessor>();

        // ── DesktopChild services: token validator + parent monitor ──
        builder.Services.AddDesktopChildServices(options);

        // ── Connector lifecycle as IHostedService ───────────
        builder.Services.AddHostedService<ConnectorHostLifecycleService>();

        // ── Business service registrations ─────────────────
        builder.AddPuddingApplicationServices(
            dataPaths,
            bootstrapConfiguration,
            aspnetcoreEnvironment,
            options);

        return builder;
    }

    /// <summary>
    /// Phase 2: Configure middleware pipeline and endpoint mapping.
    /// </summary>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        Console.WriteLine("[Startup] Host built, configuring middleware...");

        app.MapPuddingApplication();

        return app;
    }

    /// <summary>
    /// Phase 3: Idempotent database/schema initialization and catalog loading.
    /// Address capture is NOT done here — it must happen after StartAsync.
    /// </summary>
    public static async Task InitializeAsync(
        WebApplication application,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("[Startup] DB migration skipped — using pre-built database");
        await PuddingApplicationInitializer.InitializeAsync(application, cancellationToken);
    }

    /// <summary>
    /// Phase 4: Capture server bound addresses (ONLY valid after StartAsync).
    /// Resolves a loopback control address from the bound HTTP listener. A wildcard
    /// listener is projected to 127.0.0.1 with the same port for trusted local calls.
    /// </summary>
    public static Uri CaptureBoundAddresses(WebApplication application)
    {
        var addressAccessor = application.Services.GetService<IPuddingServerAddressAccessor>();

        var serverAddressesFeature = application.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();

        var addresses = serverAddressesFeature?.Addresses ?? [];
        addressAccessor?.SetBoundAddresses(addresses);

        var baseAddress = addressAccessor?.BaseAddress
            ?? throw new InvalidOperationException(
                "No local HTTP control address found after server start. " +
                $"Bound addresses: [{string.Join(", ", addresses)}]");

        Console.WriteLine(
            $"[Startup] Server bound addresses: [{string.Join(", ", addresses)}]; " +
            $"local control address: {baseAddress}");
        return baseAddress;
    }
}
