using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Pudding.CapabilityBroker;
using Pudding.CapabilityBroker.AspNetCore;
using PuddingAgent.Services;
using PuddingCode.Configuration;
using PuddingCode.Security;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Security;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Security;
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
        PuddingHostOptions options,
        StartupPhaseTracker? phases = null)
    {
        // ── DataRoot resolution and directory preparation ─────
        var dataRoot = string.IsNullOrWhiteSpace(options.DataRoot)
            ? PuddingDataRootBootstrapper.ResolveDataRoot(args)
            : options.DataRoot;

        var dataPaths = PuddingDataRootBootstrapper.Bootstrap(dataRoot);
        phases?.Mark(StartupPhases.DataRootBootstrapped);

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
        phases?.Mark(StartupPhases.LoggingReady);

        // ── WebApplicationBuilder ────────────────────────────
        var builder = WebApplication.CreateBuilder(args);
        // Product/user-owned runtime policy lives below DataRoot. Add it after
        // the packaged appsettings defaults, then restore environment/CLI as
        // the highest-precedence operational overrides.
        builder.Configuration
            .AddJsonFile(dataPaths.SystemConfigFile("system.json"), optional: true, reloadOnChange: true)
            .AddEnvironmentVariables();
        if (args.Length > 0)
            builder.Configuration.AddCommandLine(args);
        builder.Services.AddSingleton(dataPaths);

        // ── DI validation ────────────────────────────────────
        builder.Host.UseDefaultServiceProvider(o =>
        {
            o.ValidateScopes = true;
            o.ValidateOnBuild = true;
        });
        builder.Host.UseSerilog();

        // ── URL binding ─────────────────────────────────────
        // ADR-094 C-1：解析结果与既往实现**逐字等价**（行为不变），仅把「哪个来源生效」纳入启动审计。
        // 修正「配置链 urls 被硬编码默认值覆盖」属 C-2，需二次批准。
        var urlBinding = StartupConfigurationAudit.ResolveUrlBinding(
            options.Urls,
            Environment.GetEnvironmentVariable("ASPNETCORE_URLS"),
            builder.Configuration["urls"]);
        if (urlBinding.ShouldCallUseUrls)
        {
            builder.WebHost.UseUrls(urlBinding.ExplicitUrls!.ToArray());
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

        // ── ADR-094 C-1：生效来源审计（观测专用，零行为变更）──
        StartupConfigurationAudit.Emit(
            Console.WriteLine,
            StartupConfigurationAudit.Build(
                urlBinding,
                options.Urls,
                Environment.GetEnvironmentVariable("ASPNETCORE_URLS"),
                corsOrigins,
                builder.Configuration["Cors:AllowedOrigins"],
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                dataRoot,
                string.IsNullOrWhiteSpace(options.DataRoot)
                    ? "PuddingDataRootBootstrapper.ResolveDataRoot(args)"
                    : "PuddingHostOptions.DataRoot",
                bootstrapConfiguration["Serilog:MinimumLevel"]),
            urlBinding.Warnings);

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
            })
            // ADR-075: 第三方 External Access Token 独立认证 scheme。
            // JWT 保持全局默认 scheme；External scheme 只由 /api/external/v1 Policy 显式选择。
            .AddScheme<ExternalAccessTokenOptions, ExternalAccessTokenHandler>(
                ExternalAccessTokenAuthentication.Scheme,
                _ => { })
            // SKILL Hub 机器凭据 scheme（skill_hub 工具 X-Admin-Api-Key 通路）：
            // 仅由 SkillHubClient 策略显式选择，全局默认认证 scheme 保持 JWT 不变。
            .AddScheme<SkillHubApiKeyOptions, SkillHubApiKeyAuthenticationHandler>(
                SkillHubApiKeyAuthentication.Scheme,
                _ => { });
        builder.Services.AddAuthorization(authorization =>
        {
            authorization.AddPolicy(
                PuddingAuthorizationPolicies.StorageManagement,
                policy => policy.AddRequirements(new StorageManagementRequirement()));

            // ── ADR-075/082 External Access Token policies ──────
            // Token 管理锁定 JWT scheme + admin role：External Token 无 admin role
            // 且 scheme 不匹配，永远无法进入管理 API。
            authorization.AddPolicy(
                ExternalAccessTokenPolicyNames.AdminAccessTokenManagement,
                policy => policy
                    .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                    .RequireRole("admin"));

            AddExternalTaskApiPolicy(
                authorization,
                ExternalAccessTokenPolicyNames.ExternalApiAuthenticated,
                scope: null,
                requireWorkspace: false);
            AddExternalTaskApiPolicy(
                authorization,
                ExternalAccessTokenPolicyNames.ExternalTasksRead,
                ExternalTaskApiScopes.TasksRead,
                requireWorkspace: true);
            AddExternalTaskApiPolicy(
                authorization,
                ExternalAccessTokenPolicyNames.ExternalTasksWrite,
                ExternalTaskApiScopes.TasksWrite,
                requireWorkspace: true);
            AddExternalTaskApiPolicy(
                authorization,
                ExternalAccessTokenPolicyNames.ExternalTasksComment,
                ExternalTaskApiScopes.TasksComment,
                requireWorkspace: true);
            AddExternalTaskApiPolicy(
                authorization,
                ExternalAccessTokenPolicyNames.ExternalTasksEvaluate,
                ExternalTaskApiScopes.TasksEvaluate,
                requireWorkspace: true);
            AddExternalTaskApiPolicy(
                authorization,
                ExternalAccessTokenPolicyNames.ExternalTasksCommand,
                ExternalTaskApiScopes.TasksCommand,
                requireWorkspace: true);
            AddExternalTaskApiPolicy(
                authorization,
                ExternalAccessTokenPolicyNames.ExternalWorkspacesRead,
                ExternalTaskApiScopes.WorkspacesRead,
                requireWorkspace: false);
            AddExternalTaskApiPolicy(
                authorization,
                ExternalAccessTokenPolicyNames.ExternalWorkspaceRead,
                ExternalTaskApiScopes.WorkspacesRead,
                requireWorkspace: true);
            AddExternalTaskApiPolicy(
                authorization,
                ExternalAccessTokenPolicyNames.ExternalAgentsRead,
                ExternalTaskApiScopes.AgentsRead,
                requireWorkspace: true);
            AddExternalTaskApiPolicy(
                authorization,
                ExternalAccessTokenPolicyNames.ExternalMessagesSend,
                ExternalTaskApiScopes.MessagesSend,
                requireWorkspace: true);

            // ── SKILL Hub 机器凭据策略（api/skill-hub 专用）──────
            // JwtBearer（现有管理界面，行为零变化）+ SkillHubApiKey（机器凭据）双通道；
            // 不授 admin 角色，不改默认授权策略，不影响其它端点。
            AddSkillHubClientPolicy(authorization);
        });
        builder.Services.AddSingleton<IAuthorizationHandler, ExternalAccessTokenAuthorizationHandler>();

        // ── Host options ────────────────────────────────────
        builder.Services.AddSingleton(options);

        // ── Server address accessor (Desktop mode) ──────────
        builder.Services.AddSingleton<IPuddingServerAddressAccessor, PuddingServerAddressAccessor>();

        // ── DesktopChild services: token validator + parent monitor ──
        builder.Services.AddDesktopChildServices(options);
        builder.Services.AddSingleton<IAuthorizationHandler, StorageManagementAuthorizationHandler>();

        // ── 切片 C-3：桌面能力通道（Desktop 主动 gRPC 双向流，Core 经该流下发能力命令）──
        // **默认关闭**：`Desktop:CapabilityChannel:Enabled` 缺省 false ⇒ 不注册服务、不监听新端点、
        // 不改变任何既有绑定，产品行为与今天逐字一致。回滚 = 置 false 并重启。
        var capabilityChannel = CapabilityChannelConfiguration.Bind(builder.Configuration);
        if (capabilityChannel.Enabled)
        {
            var capabilityUserScope = DesktopCapabilityChannelWiring.ResolveUserScope();
            var capabilityProductInstance = DesktopCapabilityChannelWiring.ResolveProductInstanceId(dataRoot);

            // Core 实例 ID 每次启动新生成：Desktop 用它识别「Core 是否换了实例」（重连语义）。
            var capabilityCoreInstance = $"core-{Guid.NewGuid():N}";

            var channelOptions = capabilityChannel.CreateOptions(
                capabilityUserScope,
                capabilityProductInstance,
                capabilityCoreInstance);

            // 显式重绑 REST：约束①「Listen* 覆盖 UseUrls」——不重绑会让 REST 在启用通道后静默消失。
            var restEndpoints = DesktopCapabilityChannelWiring.ParseRestEndpoints(
                urlBinding.ShouldCallUseUrls
                    ? urlBinding.ExplicitUrls!
                    : (urlBinding.EffectiveUrls ?? string.Empty)
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            builder.WebHost.ConfigureKestrel(kestrel =>
                DesktopCapabilityChannelWiring.BindRestAndCapabilityChannel(kestrel, restEndpoints, channelOptions));

            // 认证：复用既有 ControlToken 校验（常量时间、令牌轮换无需重启）。
            var capabilityAuthenticator =
                new ControlTokenCapabilityAuthenticator(new DesktopControlTokenValidator(dataRoot));

            // 授权：由**可信运行上下文**取调用方身份，再按能力 traits 逐条判定（全部 fail closed）。
            // 这是「身份门禁」阶段：把 Tool Runtime 的权限/审批链也接进来属切片 D 的调用点迁移；
            // 在接好之前不放行来源不明的调用（也不用 AllowAll 顶上）。
            builder.Services.AddCapabilityChannel(
                channelOptions,
                capabilityAuthenticator,
                new TrustedCallerDesktopCapabilityAuthorizer(new RuntimeTraceContextCallerIdentityProvider()));

            builder.Services.AddSingleton(capabilityChannel);
            builder.Services.AddSingleton(new DesktopCapabilityChannelRuntime(
                capabilityChannel,
                channelOptions,
                capabilityChannel.Describe(
                    capabilityUserScope,
                    capabilityProductInstance,
                    capabilityCoreInstance)!,
                restEndpoints
                    // 端口 0 = 动态端口：实际端口启动后才知道，无法逐字比对，故不列入期望（预检仍校验能力端点）。
                    .Where(endpoint => endpoint.Port != 0)
                    .Select(endpoint => $"http://{endpoint.Address}:{endpoint.Port}")
                    .ToArray()));

            Console.WriteLine(
                "[CapabilityChannel] 已启用：显式重绑 REST 与能力通道；授权器=默认拒绝（待切片 D 接入 Tool Runtime 准入）");
        }


        // ── Connector lifecycle as IHostedService ───────────
        builder.Services.AddHostedService<ConnectorHostLifecycleService>();

        // ── Business service registrations ─────────────────
        builder.AddPuddingApplicationServices(
            dataPaths,
            bootstrapConfiguration,
            aspnetcoreEnvironment,
            options);

        phases?.Mark(StartupPhases.ServicesRegistered);

        return builder;
    }

    /// <summary>
    /// External Task API policy helper：显式 PuddingExternalAccessToken scheme +
    /// 已认证 + 可选 scope + 可选 workspace allow-list（route value 比对）。
    /// </summary>
    private static void AddExternalTaskApiPolicy(
        AuthorizationOptions options,
        string policyName,
        string? scope,
        bool requireWorkspace)
    {
        options.AddPolicy(policyName, policy =>
        {
            policy.AddAuthenticationSchemes(ExternalAccessTokenAuthentication.Scheme);
            policy.RequireAuthenticatedUser();
            if (scope is not null)
                policy.AddRequirements(new ExternalScopeRequirement(scope));
            if (requireWorkspace)
                policy.AddRequirements(new ExternalWorkspaceRequirement());
        });
    }

    /// <summary>
    /// SKILL Hub 端点策略：显式 JwtBearer + SkillHubApiKey 双 scheme + RequireAuthenticatedUser。
    /// JWT 保证现有管理界面行为零变化；SkillHubApiKey 供进程内 skill_hub 工具无 JWT 发布技能，
    /// 认证成功只建立机器身份（不含任何角色）。
    /// </summary>
    private static void AddSkillHubClientPolicy(AuthorizationOptions options)
    {
        options.AddPolicy(SkillHubApiPolicyNames.SkillHubClient, policy =>
        {
            policy.AddAuthenticationSchemes(
                JwtBearerDefaults.AuthenticationScheme,
                SkillHubApiKeyAuthentication.Scheme);
            policy.RequireAuthenticatedUser();
        });
    }

    /// <summary>
    /// Phase 2: Configure middleware pipeline and endpoint mapping.
    /// </summary>
    public static WebApplication Build(WebApplicationBuilder builder, StartupPhaseTracker? phases = null)
    {
        var app = builder.Build();
        phases?.Mark(StartupPhases.HostBuilt);
        Console.WriteLine("[Startup] Host built, configuring middleware...");

        app.MapPuddingApplication();

        // 只有启用时才映射能力通道端点（关闭时不注册任何新端点）。
        if (app.Services.GetService<DesktopCapabilityChannelRuntime>() is not null)
        {
            app.MapCapabilityChannel();
        }

        phases?.Mark(StartupPhases.MiddlewareMapped);

        return app;
    }

    /// <summary>
    /// Phase 3: Idempotent database/schema initialization and catalog loading.
    /// Address capture is NOT done here — it must happen after StartAsync.
    /// </summary>
    public static async Task InitializeAsync(
        WebApplication application,
        CancellationToken cancellationToken,
        StartupPhaseTracker? phases = null)
    {
        Console.WriteLine("[Startup] DB migration skipped — using pre-built database");
        await PuddingApplicationInitializer.InitializeAsync(application, cancellationToken, phases);
    }

    /// <summary>
    /// 能力通道启用时返回「可供 Desktop 严格解析」的端点描述，关闭时返回 <c>null</c>。
    ///
    /// 放在这里而不是让 <c>Program.cs</c> 直接读 DI：<see cref="DesktopCapabilityChannelRuntime"/>
    /// 是本程序集的内部类型（它的<b>存在</b>即表示通道已启用），入口点只需要一个字符串。
    /// 关闭时返回 null ⇒ 就绪信号里不出现该字段 ⇒ 行为与今天逐字一致。
    /// </summary>
    public static string? GetCapabilityEndpointDescription(IServiceProvider services) =>
        services.GetService<DesktopCapabilityChannelRuntime>() is { } runtime
            ? CapabilityChannelReadySignal.Describe(runtime.Description)
            : null;

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

        // 能力通道启用时：校验「REST 与能力端点都真的绑上了」——约束①的守门人。
        // 不健康**立即失败**（fail closed）：REST 静默消失的产品比启动失败更难排查。
        if (application.Services.GetService<DesktopCapabilityChannelRuntime>() is { } runtime)
        {
            var preflight = DesktopCapabilityChannelWiring.VerifyBinding(
                addresses, runtime.ExpectedRestAddresses, runtime.Description);

            Console.WriteLine($"[CapabilityChannel] {preflight.Summary}");
            // 就绪端点描述**必须能被 Desktop 侧的严格解析器接受**：这里的文本既是人读日志，
            // 也是 PUDDING_DESKTOP_READY 里 capabilityEndpoint 字段的来源（同一个函数，杜绝两套格式）。
            Console.WriteLine(CapabilityChannelReadySignal.DescribeLogLine(runtime.Description));

            if (!preflight.IsHealthy)
            {
                throw new InvalidOperationException(
                    $"[CapabilityChannel] 启动预检失败：{string.Join("；", preflight.Failures)}"
                    + "。请把 Desktop:CapabilityChannel:Enabled 置为 false 回滚，或修正绑定配置。");
            }
        }

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
