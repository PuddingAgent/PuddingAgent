using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using PuddingPlatform.Controllers.External.V1;
using PuddingPlatform.Services.Security;

namespace PuddingHost.Hosting;

internal static class AgentAdapterApiDocumentation
{
    private const string DocsPath = "/agent-adapters/docs";
    private const string SecurityScheme = "agentAdapter";
    private const string Document = ExternalAgentAdaptersController.DocumentName;

    internal static void AddServices(IServiceCollection services)
    {
        services.AddOpenApi(Document, options =>
        {
            options.ShouldInclude = endpoint => endpoint.GroupName == Document;
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Pudding External Agent API";
                document.Info.Version = "v1";
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    [SecurityScheme] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "pdt_v1_<AK>.<SK>",
                        Description = "Use the AK and SK issued for this Agent in Pudding administration.",
                    },
                };
                foreach (var path in document.Paths.Values)
                {
                    if (path.Operations is not { } operations)
                        continue;
                    foreach (var operation in operations.Values)
                    {
                        operation.Security ??= [];
                        operation.Security.Add(new OpenApiSecurityRequirement
                        {
                            [new OpenApiSecuritySchemeReference(SecurityScheme, document)] = [],
                        });
                    }
                }
                return Task.CompletedTask;
            });
        });
    }

    internal static void Map(WebApplication app)
    {
        app.MapOpenApi($"{DocsPath}/{{documentName}}.json").AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            var options = context.RequestServices.GetRequiredService<ExternalTaskApiOptionsProvider>().Current;
            if (!options.Enabled)
                return Results.NotFound();
            if (options.RequireHttps && !context.Request.IsHttps &&
                !IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None))
                return Results.BadRequest();
            return await next(invocation);
        });
    }
}
