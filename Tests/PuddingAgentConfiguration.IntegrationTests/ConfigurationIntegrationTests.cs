using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Pudding.Api.Contracts;
using Pudding.Api.Sdk;
using PuddingAgentConfiguration.Contracts;
using PuddingCode.Configuration;
using PuddingCode.Security;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Services;

[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

namespace PuddingAgentConfiguration.IntegrationTests;

[TestClass]
public sealed class ConfigurationIntegrationTests
{
    [TestMethod]
    public async Task FileCommit_PreservesOtherConfiguration_AndSurvivesServiceRecreation()
    {
        using var fixture = await Fixture.Create();
        var api = fixture.Api(); var original = await api.GetAsync(Fixture.Agent);
        var receipt = await api.UpdateAsync(Fixture.Agent, new() { DisplayName = "鲸鲸" }, new(original.Revision, "rename"));
        var manifest = await fixture.Manifest();
        Assert.AreEqual("鲸鲸", manifest.DisplayName);
        Assert.AreEqual("provider-owned-by-instance", manifest.PreferredProviderId);
        Assert.AreEqual("unchanged prompt", manifest.SystemPrompt);
        Assert.IsNotNull(manifest.ConfigurationControl);
        fixture.RecreateService();
        var restarted = fixture.Api();
        Assert.AreEqual(receipt.PersistedRevision, (await restarted.GetAsync(Fixture.Agent)).Revision);
        Assert.AreEqual(receipt, await restarted.UpdateAsync(Fixture.Agent, new() { DisplayName = "鲸鲸" }, new(original.Revision, "rename")));
        Assert.HasCount(1, (await restarted.ReadChangesAsync(Fixture.Agent)).Items);
        Assert.AreEqual("鲸鲸", (await fixture.Files.GetAgentAsync("workspace", "agent"))!.Name);
    }

    [TestMethod]
    public async Task LegacyWriter_PreservesReceiptAndInvalidatesPreviouslyReadRevision()
    {
        using var fixture = await Fixture.Create(); var api = fixture.Api();
        var before = await api.GetAsync(Fixture.Agent);
        var receipt = await api.UpdateAsync(Fixture.Agent, new() { Description = "New description" }, new(before.Revision, "op"));
        await fixture.Files.SetFrozenAsync("workspace", "agent", true);
        Assert.AreNotEqual(receipt.PersistedRevision, (await api.GetAsync(Fixture.Agent)).Revision);
        var conflict = await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => api.UpdateAsync(Fixture.Agent,
            new() { DisplayName = "stale" }, new(receipt.PersistedRevision, "stale")));
        Assert.AreEqual("revision.conflict", conflict.Code);
        Assert.AreEqual(receipt, await api.UpdateAsync(Fixture.Agent, new() { Description = "New description" }, new(before.Revision, "op")));
        Assert.IsTrue((await fixture.Manifest()).IsFrozen);
        Assert.HasCount(1, (await api.ReadChangesAsync(Fixture.Agent)).Items);
    }

    [TestMethod]
    public async Task MissingOrForgedWorkspace_DoesNotReadOrWriteAnotherAgent()
    {
        using var fixture = await Fixture.Create();
        var wrong = new AgentRef("other", "agent");
        var api = new AgentConfigurationApiFactory(fixture.Files).ForManagementUser(Fixture.User, wrong);
        var failure = await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => api.GetAsync(wrong));
        Assert.AreEqual("resource.not_found", failure.Code);
        var scoped = fixture.Api();
        Assert.AreEqual("permission.denied", (await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => scoped.GetAsync(wrong))).Code);
        Assert.AreEqual("Old", (await fixture.Manifest()).DisplayName);
    }

    [TestMethod]
    public async Task ConcurrentWrites_CommitOneSnapshotReceiptAndEvent()
    {
        using var fixture = await Fixture.Create(); var api = fixture.Api();
        var before = await api.GetAsync(Fixture.Agent);
        async Task<string> Attempt(int index)
        {
            try { await api.UpdateAsync(Fixture.Agent, new() { DisplayName = $"Agent {index}" }, new(before.Revision, $"op-{index}")); return "ok"; }
            catch (ConfigurationApiException ex) { return ex.Code; }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(Attempt));
        Assert.AreEqual(1, results.Count(x => x == "ok"));
        Assert.AreEqual(11, results.Count(x => x == "revision.conflict"));
        fixture.RecreateService();
        Assert.HasCount(1, (await fixture.Api().ReadChangesAsync(Fixture.Agent)).Items);
    }

    [TestMethod]
    public async Task ThrowInsideTransaction_LeavesManifestByteForByteUnchanged()
    {
        using var fixture = await Fixture.Create();
        var before = await File.ReadAllBytesAsync(fixture.ManifestPath);
        var store = (IAgentConfigurationStore)fixture.Files;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.TransactAsync<int>(Fixture.Agent,
            _ => throw new InvalidOperationException("Injected before commit"), default));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.ManifestPath));
    }

    [TestMethod]
    public async Task CorruptJournal_FailsClosedWithoutRewritingIt()
    {
        using var fixture = await Fixture.Create();
        var manifest = await fixture.Manifest();
        await AtomicFileWriter.WriteJsonAsync(fixture.ManifestPath, manifest with
        { ConfigurationControl = JsonSerializer.SerializeToElement(new { version = 999 }) }, Fixture.Json);
        var before = await File.ReadAllBytesAsync(fixture.ManifestPath);
        var failure = await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => fixture.Api().GetAsync(Fixture.Agent));
        Assert.AreEqual("store.invalid", failure.Code);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.ManifestPath));
    }

    [TestMethod]
    public async Task ExternalTokenAndUnauthenticatedCaller_AreDenied()
    {
        using var fixture = await Fixture.Create(); var factory = new AgentConfigurationApiFactory(fixture.Files);
        Assert.ThrowsExactly<ConfigurationApiException>(() => factory.ForManagementUser(new(), Fixture.Agent));
        var external = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ExternalAccessTokenClaimNames.TokenId, "token")], "test"));
        Assert.ThrowsExactly<ConfigurationApiException>(() => factory.ForManagementUser(external, Fixture.Agent));
    }

    [TestMethod]
    public async Task RealHttpController_Authenticates_RejectsUnknownFields_AndSdkRoundTrips()
    {
        using var fixture = await Fixture.Create();
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
        using var server = new TestServer(new WebHostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(new AgentConfigurationApiFactory(fixture.Files));
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
                options.TokenValidationParameters = new() { ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = true,
                    ValidateIssuerSigningKey = true, IssuerSigningKey = key });
            services.AddAuthorization();
            services.AddControllers().AddApplicationPart(typeof(AgentConfigurationApiController).Assembly);
        }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(x => x.MapControllers()); }));
        using var http = server.CreateClient();
        const string route = "/api/workspaces/workspace/agents/agent/configuration";
        using var unauth = await http.GetAsync(route);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauth.StatusCode);
        var token = new JwtSecurityToken(claims: [new Claim(ClaimTypes.NameIdentifier, "manager")], expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new(key, SecurityAlgorithms.HmacSha256));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        var agent = await new PuddingClient(new HttpAgentConfigurationApi(http)).Workspace("workspace").Agents.GetAsync("agent");
        var before = await agent.Configuration.GetAsync();
        var receipt = await agent.Configuration.UpdateAsync(new() { DisplayName = "HTTP 名称" }, new(before.Revision, "http"));
        Assert.AreEqual("HTTP 名称", (await agent.Configuration.GetAsync()).DisplayName);
        Assert.AreEqual(receipt.PersistedRevision, (await agent.Configuration.ReadChangesAsync()).ObservedRevision);
        using var rejected = await http.PatchAsJsonAsync(route, new { patch = new { status = "Completed" }, options = new { expectedRevision = receipt.PersistedRevision, operationId = "bad" } });
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.AreEqual(receipt.PersistedRevision, (await agent.Configuration.GetAsync()).Revision);
    }

    private sealed class Fixture : IDisposable
    {
        public static readonly AgentRef Agent = new("workspace", "agent");
        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        public static ClaimsPrincipal User => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "manager")], "test"));
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pudding-configuration-" + Guid.NewGuid().ToString("N"));
        public PuddingDataPaths Paths { get; }
        public WorkspaceAgentFileService Files { get; private set; } = null!;
        public string ManifestPath => Path.Combine(Paths.AgentInstanceRoot("agent"), "manifest.json");
        private Fixture() { Paths = PuddingDataPaths.FromRoot(_root); RecreateService(); }
        public static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            await AtomicFileWriter.WriteJsonAsync(fixture.ManifestPath, new AgentInstanceManifest
            {
                AgentInstanceId = "agent", WorkspaceId = "workspace", TemplateId = "template", DisplayName = "Old",
                Description = "Description", SystemPrompt = "unchanged prompt", PreferredProviderId = "provider-owned-by-instance"
            }, Json);
            await AtomicFileWriter.WriteJsonAsync(fixture.Paths.WorkspaceAgentRefFile("workspace", "agent"),
                new WorkspaceAgentRef { AgentInstanceId = "agent", WorkspaceId = "workspace" }, Json);
            return fixture;
        }
        public void RecreateService()
        {
            var avatars = new AgentAvatarCatalog(NullLogger<AgentAvatarCatalog>.Instance);
            Files = new(Paths, new AgentTemplateFileService(Paths, avatars, NullLogger<AgentTemplateFileService>.Instance),
                avatars, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkspaceAgentFileService>.Instance);
        }
        public IAgentConfigurationApi Api() => new AgentConfigurationApiFactory(Files).ForManagementUser(User, Agent);
        public async Task<AgentInstanceManifest> Manifest() => (await AtomicFileWriter.ReadJsonAsync<AgentInstanceManifest>(ManifestPath, Json))!;
        public void Dispose()
        {
            _services.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
