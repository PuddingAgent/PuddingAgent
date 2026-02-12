using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PuddingWebApiTests;

[TestClass]
public sealed class BootstrapApiControllerTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [TestMethod]
    public async Task Complete_CreatesAdminProviderAndDefaultModel()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();

        var payload = new
        {
            admin = new
            {
                userId = "admin",
                email = "admin@example.com",
                displayName = "Administrator",
                password = "Admin12345",
            },
            provider = new
            {
                mode = "custom",
                providerId = "bootstrap-openai",
                name = "Bootstrap OpenAI",
                protocol = "openai",
                baseUrl = "https://api.example.com/v1",
                apiKey = "test-key",
                chatModelId = "gpt-test",
                memoryModelId = "gpt-test-mini",
            },
            defaults = new
            {
                workspaceName = "默认工作空间",
                agentName = "默认助手",
            },
        };

        var response = await client.PostAsJsonAsync("/api/bootstrap/complete", payload);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<BootstrapCompleteResult>(JsonOpts);
        Assert.IsNotNull(result);
        Assert.AreEqual("ok", result!.Status);
        Assert.AreEqual("admin", result.CurrentAuthority);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Token));
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", result.Token);
        Assert.AreEqual("bootstrap-openai", result.ProviderId);
        Assert.AreEqual("gpt-test", result.ChatModelId);

        var providerResponse = await client.GetAsync("/api/llm/providers/bootstrap-openai");
        Assert.AreEqual(HttpStatusCode.OK, providerResponse.StatusCode);
        var provider = await providerResponse.Content.ReadFromJsonAsync<LlmProviderDetail>(JsonOpts);
        Assert.IsNotNull(provider);
        Assert.AreEqual("Bootstrap OpenAI", provider!.Name);
        Assert.IsTrue(provider.Models.Any(m => m.ModelId == "gpt-test" && m.IsDefault));
        Assert.IsTrue(provider.Models.Any(m => m.ModelId == "gpt-test-mini"));

        var agentsResponse = await client.GetAsync("/api/workspaces/default/agents");
        Assert.AreEqual(HttpStatusCode.OK, agentsResponse.StatusCode);
        var agents = await agentsResponse.Content.ReadFromJsonAsync<List<WorkspaceAgent>>(JsonOpts);
        Assert.IsNotNull(agents);
        Assert.HasCount(1, agents!);
        Assert.AreEqual("默认助手", agents[0].Name);
        Assert.AreEqual("global:general-assistant", agents[0].SourceTemplateId);
        Assert.AreEqual("bootstrap-openai", agents[0].PreferredProviderId);
        Assert.AreEqual("gpt-test", agents[0].PreferredModelId);
    }

    private sealed record BootstrapCompleteResult(
        string Status,
        string CurrentAuthority,
        string Token,
        string? ProviderId,
        string? ChatModelId,
        string? MemoryModelId,
        string WorkspaceId
    );

    private sealed record LlmProviderDetail(
        string ProviderId,
        string Name,
        List<LlmModel> Models
    );

    private sealed record LlmModel(
        string ModelId,
        bool IsDefault
    );

    private sealed record WorkspaceAgent(
        string AgentId,
        string Name,
        string? SourceTemplateId,
        string? PreferredProviderId,
        string? PreferredModelId
    );
}
