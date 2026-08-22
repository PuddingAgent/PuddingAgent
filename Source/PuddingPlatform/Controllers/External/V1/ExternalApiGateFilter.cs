using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PuddingPlatform.Services.Security;

namespace PuddingPlatform.Controllers.External.V1;

/// <summary>
/// ADR-075: External API 门控 — ExternalTaskApi.Enabled=false 返回 404（不暴露端点存在）；
/// 非 Loopback 明文 HTTP 返回 400。以 ServiceFilter 挂载，需 DI 注册。
/// </summary>
public sealed class ExternalApiGateFilter(ExternalTaskApiOptionsProvider optionsProvider)
    : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        var options = optionsProvider.Current;
        var http = context.HttpContext;

        if (!options.Enabled)
        {
            context.Result = new NotFoundObjectResult(
                new ExternalErrorResponse { Code = "external.not_enabled" });
            return;
        }

        if (options.RequireHttps
            && !http.Request.IsHttps
            && !IPAddress.IsLoopback(http.Connection.RemoteIpAddress ?? IPAddress.None))
        {
            context.Result = new BadRequestObjectResult(
                new ExternalErrorResponse { Code = "external.https_required" });
            return;
        }

        await next();
    }
}
