using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingHost.Hosting;
using PuddingPlatform.Services;

namespace PuddingAgent.Services;

/// <summary>
/// Configures the HTTP middleware and endpoint surface for the Pudding host.
/// Ordering in this method is part of the runtime contract.
/// </summary>
public static class PuddingWebApplicationExtensions
{
    public static WebApplication MapPuddingApplication(this WebApplication app)
    {
        // ── HTTP 请求日志（最先执行，记录所有请求）───────────
        app.UseHttpLogging();

        // ── 错误处理 ─────────────────────────────────────────
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Home/Error");
        }
        app.UseMiddleware<PuddingPlatform.Middleware.TraceableExceptionMiddleware>();

        // ── CORS（必须在 Routing 前）────────────────────────
        app.UseCors("AdminSpa");

        // ── Routing ──────────────────────────────────────────
        app.UseRouting();

        // ── WebSocket ────────────────────────────────────────
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

        // ── Auth ─────────────────────────────────────────────
        app.UseAuthentication();

        // ── Session ──────────────────────────────────────────
        app.UseSession();

        app.UseAuthorization();

        // ── 请求诊断（Auth 之后）─ 记录到达控制器管线的请求 ──
        app.Use(async (ctx, next) =>
        {
            var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("HttpPipeline.Diag");
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                logger.LogDebug(
                    "[Pipeline] {Method} {Path} auth={Auth} ct={ContentType} len={Len}",
                    ctx.Request.Method,
                    ctx.Request.Path,
                    ctx.User?.Identity?.IsAuthenticated ?? false,
                    ctx.Request.ContentType ?? "-",
                    ctx.Request.ContentLength ?? 0);
            }
            await next();
        });

        // ── 静态文件（同时从输出目录 wwwroot/ 和项目 wwwroot/ 提供）─
        // Desktop 将 Core 嵌套发布到 core/。这里必须以物理输出目录为准，
        // 不能依赖 Static Web Assets 清单；嵌套 OutDir 会让清单端点返回空内容。
        var outputWwwRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(outputWwwRoot))
        {
            var fileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(outputWwwRoot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
        }
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // ── API 路由（必须在 Fallback 前）────────────────────
        app.MapControllers();

        // ── DesktopChild shutdown endpoint (registered after controllers) ──
        var hostOptions = app.Services.GetRequiredService<PuddingHostOptions>();
        app.MapDesktopChildEndpoints(hostOptions);

        // ── 路由后诊断 ── 确认请求到达了控制器路由层 ──
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                var endpoint = ctx.GetEndpoint();
                var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("HttpPipeline.Diag");
                logger.LogDebug(
                    "[Pipeline:AfterRoute] {Method} {Path} endpoint={Endpoint} status={Status}",
                    ctx.Request.Method,
                    ctx.Request.Path,
                    endpoint?.DisplayName ?? "(none)",
                    ctx.Response.StatusCode);
            }
            await next();
        });

        // ── MVC Controller 路由 ──────────────────────────────
        app.MapControllerRoute(
            name: "platform",
            pattern: "platform/{controller=Home}/{action=Index}/{id?}");

        // ── 健康检查（liveness 只检查进程；readiness 检查 Conversation 执行链）────
        app.MapGet("/health/live", () => Results.Ok(new
        {
            status = "alive",
            timestamp = DateTimeOffset.UtcNow
        }));

        app.MapGet("/health/ready", async (
            PlatformReadinessProbe probe,
            CancellationToken ct) =>
        {
            var readiness = await probe.CheckAsync(ct);
            return Results.Json(new
            {
                status = readiness.IsReady ? "ready" : "not_ready",
                errorId = readiness.ErrorId,
                timestamp = DateTimeOffset.UtcNow
            }, statusCode: readiness.IsReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });

        app.MapGet("/health", async (
            PlatformReadinessProbe probe,
            CancellationToken ct) =>
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version?.ToString() ?? "0.0.0";
            string? imageHash = null;
            string? buildTime = null;
            try
            {
                var dlls = new[] { "PuddingRuntime.dll", "PuddingMemoryEngine.dll" };
                using var sha = System.Security.Cryptography.SHA256.Create();
                foreach (var dll in dlls)
                {
                    var path = Path.Combine(AppContext.BaseDirectory, dll);
                    if (File.Exists(path))
                    {
                        using var stream = File.OpenRead(path);
                        var hash = sha.ComputeHash(stream);
                        buildTime = File.GetLastWriteTimeUtc(path).ToString("o");
                    }
                }
                imageHash = Convert.ToHexString(sha.Hash!) [^8..];
            }
            catch { imageHash = "unknown"; }

            var readiness = await probe.CheckAsync(ct);
            return Results.Json(new
            {
                status = readiness.IsReady ? "healthy" : "not_ready",
                errorId = readiness.ErrorId,
                version,
                imageHash,
                buildTime = buildTime ?? "unknown",
                timestamp = DateTimeOffset.UtcNow
            }, statusCode: readiness.IsReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });

        // ── 配置热重载接口 ───────
        app.MapMethods("/admin/reload", new[] { "GET", "POST" }, () =>
        {
            return Results.Ok(new { status = "file-backed", timestamp = DateTimeOffset.UtcNow });
        });

        // ── 潜意识 LLM 状态 ──────────────────────
        app.MapGet("/health/subconscious", async (
            IDbContextFactory<PuddingMemoryEngine.Data.MemoryDbContext> dbFactory,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("HealthCheck");
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);

                var recentJobs = await db.SubconsciousJobLogs
                    .AsNoTracking()
                    .OrderByDescending(j => j.CreatedAt)
                    .Take(10)
                    .Select(j => new
                    {
                        j.JobId,
                        j.SessionId,
                        j.Status,
                        j.FactsExtracted,
                        j.FactsMerged,
                        j.FactsDiscarded,
                        j.ElapsedMs,
                        j.LlmModelId,
                        j.ErrorMessage,
                        j.CreatedAt
                    })
                    .ToListAsync(ct);

                var totalFacts = await db.MemoryFacts.CountAsync(f => f.Status == "active", ct);
                var totalPrefs = await db.MemoryPreferences.CountAsync(ct);

                return Results.Ok(new
                {
                    recentJobs,
                    summary = new
                    {
                        totalJobs = recentJobs.Count,
                        successCount = recentJobs.Count(j => j.Status == "completed"),
                        failCount = recentJobs.Count(j => j.Status == "failed"),
                        totalFacts,
                        totalPreferences = totalPrefs
                    },
                    timestamp = DateTimeOffset.UtcNow
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[HealthCheck] Subconscious status query failed");
                return Results.Ok(new { status = "unavailable", error = ex.Message });
            }
        });

        // ── Legacy Chat API ──────────────────────────────────
        app.MapPost("/api/chat", () => Results.Problem(
            statusCode: StatusCodes.Status410Gone,
            title: "Legacy chat endpoint is not executable",
            detail:
                "Use POST /api/v1/conversations/{conversationId}/turns with an Agent instance. " +
                "Agent LLM routing is read from manifest.json preferredProviderId/preferredModelId; " +
                "the LLM resource pool has no default route."));

        // ── Admin SPA fallback ───────────
        // MapFallbackToFile uses IWebHostEnvironment.WebRootFileProvider, which may not
        // point at Desktop's nested core/wwwroot. Return the physical publish artifact.
        var adminIndexPath = Path.Combine(outputWwwRoot, "admin", "index.html");
        if (File.Exists(adminIndexPath))
        {
            app.MapFallback(
                "/admin/{*path:nonfile}",
                () => Results.File(adminIndexPath, "text/html; charset=utf-8"));
        }

        // ── Chat SPA fallback ──────
        var chatIndexPath = Path.Combine(outputWwwRoot, "index.html");
        if (File.Exists(chatIndexPath))
        {
            app.MapFallback(() => Results.File(chatIndexPath, "text/html; charset=utf-8"));
        }

        return app;
    }

}
