using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Observability;

namespace PuddingPlatform.Middleware;

public sealed class TraceableExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TraceableExceptionMiddleware> _logger;

    public TraceableExceptionMiddleware(
        RequestDelegate next,
        ILogger<TraceableExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await _next(ctx);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // A browser navigation, refresh, or closed tab cancels the request token. This is a
            // normal transport outcome, not an application failure and must not mint an errorId or
            // pollute the Error log with a synthetic HTTP 500.
            var trace = RuntimeTraceContextAccessor.Current;
            _logger.LogDebug(
                "[RequestCancelled] traceId={TraceId} sessionId={SessionId} path={Path}",
                trace?.TraceId ?? ctx.TraceIdentifier,
                trace?.SessionId,
                ctx.Request.Path);

            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 499;
            }
        }
        catch (Exception ex)
        {
            var trace = RuntimeTraceContextAccessor.Current;
            var errorId = Guid.NewGuid().ToString("N")[..12];
            var sessionId = trace?.SessionId;
            var traceId = trace?.TraceId;

            _logger.LogError(
                ex,
                "[UnhandledException] errorId={ErrorId} traceId={TraceId} sessionId={SessionId} path={Path}",
                errorId, traceId, sessionId, ctx.Request.Path);

            var response = new TraceableErrorResponse
            {
                ErrorId = errorId,
                TraceId = traceId,
                SessionId = sessionId,
                Message = "服务器内部错误，请提供 errorId 联系管理员",
                Timestamp = DateTimeOffset.UtcNow,
            };

            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            ctx.Response.ContentType = "application/json";

            if (!ctx.Response.HasStarted)
            {
                await ctx.Response.WriteAsync(
                    JsonSerializer.Serialize(response),
                    ctx.RequestAborted);
            }
        }
    }
}
