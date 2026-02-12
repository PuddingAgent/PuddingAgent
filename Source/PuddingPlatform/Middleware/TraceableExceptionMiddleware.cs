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
