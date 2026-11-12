using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PuddingPlatform.Controllers.Api;

namespace PuddingAgentConfiguration.IntegrationTests;

[TestClass]
public sealed class WorkspaceAgentRouteTests
{
    [TestMethod]
    [DataRow("GET", "agents", "WorkspaceAgentApi.List")]
    [DataRow("GET", "agents/existing", "WorkspaceAgentApi.Get")]
    [DataRow("POST", "agents", "WorkspaceAgentApi.Create")]
    [DataRow("GET", "agent-instances?maxCount=1", "AgentDirectory.List")]
    [DataRow("GET", "agent-instances/existing", "AgentDirectory.Get")]
    [DataRow("POST", "agent-instances", "AgentFactory.Create")]
    [DataRow("POST", "agent-instances/existing/archive", "AgentFactory.Archive")]
    [DataRow("POST", "agents/existing/archive", "AgentFactory.Archive")]
    public async Task RouteSelectsSingleIntendedAction(string method, string path, string action)
    {
        using var server = new TestServer(new WebHostBuilder()
            .ConfigureServices(services => services.AddControllers().AddApplicationPart(typeof(WorkspaceAgentApiController).Assembly))
            .Configure(app =>
            {
                app.UseRouting();
                // Inspect routing before authentication or action execution; no request can mutate an agent.
                app.Use(next => async context =>
                {
                    var selected = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
                    if (selected is null) await next(context);
                    else await context.Response.WriteAsync($"{selected.ControllerName}.{selected.ActionName}");
                });
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            }));
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/workspaces/default/" + path);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.AreEqual(action, await response.Content.ReadAsStringAsync());
    }
}
