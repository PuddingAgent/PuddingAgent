using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Middleware;

namespace PuddingPlatformTests.Middleware;

[TestClass]
public sealed class TraceableExceptionMiddlewareTests
{
    [TestMethod]
    public async Task RequestAbort_DoesNotBecomeTraceable500()
    {
        using var abort = new CancellationTokenSource();
        abort.Cancel();
        var context = new DefaultHttpContext
        {
            RequestAborted = abort.Token,
        };
        context.Request.Path = "/api/workspaces/default/agents/status";
        context.Response.Body = new MemoryStream();
        var middleware = new TraceableExceptionMiddleware(
            _ => Task.FromException(new OperationCanceledException(abort.Token)),
            NullLogger<TraceableExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.AreEqual(499, context.Response.StatusCode);
        Assert.AreEqual(0, context.Response.Body.Length);
    }

    [TestMethod]
    public async Task NonRequestCancellation_RemainsTraceable500()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/backend-timeout";
        context.Response.Body = new MemoryStream();
        var middleware = new TraceableExceptionMiddleware(
            _ => Task.FromException(new TaskCanceledException("backend timeout")),
            NullLogger<TraceableExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.AreEqual(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.IsTrue(context.Response.Body.Length > 0);
    }
}
