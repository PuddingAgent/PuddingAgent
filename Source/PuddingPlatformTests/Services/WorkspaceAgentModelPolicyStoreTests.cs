using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingModels.Contracts;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
using PuddingPlatformTests;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-06 configuration adapter over the existing Agent manifest: host authorization is required, the policy is stored in
/// the same manifest (display name/journal untouched), CAS writes round-trip, and a stale revision conflicts.
/// </summary>
[TestClass]
public sealed class WorkspaceAgentModelPolicyStoreTests
{
    [TestMethod]
    public async Task WithoutAuthorization_ReadsAndWritesFailClosed()
    {
        await using var host = await TestHost.CreateAsync(authorization: null);
        var store = (IAgentModelPolicyStore)host.Service;
        var scope = host.Scope();
        var read = await Assert.ThrowsExactlyAsync<ModelException>(async () => await store.ReadAsync(scope));
        Assert.AreEqual(ModelErrorCodes.ScopeForbidden, read.Code);
        var write = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await store.WriteAsync(scope, Command(0, new ModelPolicyPatch("Review", "model-review", false))));
        Assert.AreEqual(ModelErrorCodes.ScopeForbidden, write.Code);
    }

    [TestMethod]
    public async Task AuthorizedPolicy_RoundTripsInTheExistingManifest()
    {
        await using var host = await TestHost.CreateAsync(authorization: new AllowAll());
        var store = (IAgentModelPolicyStore)host.Service;
        var scope = host.Scope();
        Assert.AreEqual(0L, (await store.ReadAsync(scope)).InstanceRevision);

        var set = await store.WriteAsync(scope, Command(0, new ModelPolicyPatch("Review", "model-review", false)));
        Assert.AreEqual(1L, set.PersistedRevision);
        var snapshot = await store.ReadAsync(scope);
        var instance = snapshot.Purposes.Single(p => p.Purpose == "Review").Instance!;
        Assert.AreEqual("model-review", instance.ModelId);
        Assert.IsFalse(instance.InheritPrimary);

        var inherit = await store.WriteAsync(scope, Command(1, new ModelPolicyPatch("Review", null, false,
            InheritPrimary: true)));
        Assert.AreEqual(2L, inherit.PersistedRevision);
        Assert.IsTrue((await store.ReadAsync(scope)).Purposes.Single(p => p.Purpose == "Review").Instance!.InheritPrimary);

        var clear = await store.WriteAsync(scope, Command(2, new ModelPolicyPatch("Review", null, Clear: true)));
        Assert.AreEqual(3L, clear.PersistedRevision);
        Assert.AreEqual(0, (await store.ReadAsync(scope)).Purposes.Length);

        // The existing manifest fields are untouched by policy writes.
        var manifestJson = await File.ReadAllTextAsync(Path.Combine(
            PuddingDataPaths.FromRoot(host.Root).AgentInstanceRoot(host.AgentId), "manifest.json"));
        StringAssert.Contains(manifestJson, "Policy Agent");
    }

    [TestMethod]
    public async Task StaleRevision_Conflicts()
    {
        await using var host = await TestHost.CreateAsync(authorization: new AllowAll());
        var store = (IAgentModelPolicyStore)host.Service;
        var scope = host.Scope();
        await store.WriteAsync(scope, Command(0, new ModelPolicyPatch("Review", "model-review", false)));
        var conflict = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await store.WriteAsync(scope, Command(0, new ModelPolicyPatch("Review", "model-other", false))));
        Assert.AreEqual(ModelErrorCodes.ConfigRevisionConflict, conflict.Code);
    }

    private static UpdateModelPolicyCommand Command(long expected, params ModelPolicyPatch[] patches)
        => new("op-policy", expected, [.. patches], ApplyPolicy: "next-run");

    private sealed class AllowAll : IAgentModelPolicyAuthorization
    {
        public ValueTask<bool> AuthorizeReadAsync(AgentProfileScope scope, CancellationToken ct = default)
            => ValueTask.FromResult(true);
        public ValueTask<bool> AuthorizeWriteAsync(AgentProfileScope scope, long expectedRevision,
            CancellationToken ct = default) => ValueTask.FromResult(true);
    }

    private sealed class TestHost(string root, WorkspaceAgentFileService service, string agentId) : IAsyncDisposable
    {
        public WorkspaceAgentFileService Service { get; } = service;
        public string AgentId { get; } = agentId;
        public string Root { get; } = root;

        public AgentProfileScope Scope() => new("default", AgentId, "actor-1");

        public static async Task<TestHost> CreateAsync(IAgentModelPolicyAuthorization? authorization)
        {
            var root = Path.Combine(Path.GetTempPath(), "pudding-policy-store", Guid.NewGuid().ToString("N"));
            var paths = PuddingDataPaths.FromRoot(root);
            const string agentId = "agent-policy";
            Directory.CreateDirectory(paths.AgentInstanceRoot(agentId));
            await using (var stream = File.Create(Path.Combine(paths.AgentInstanceRoot(agentId), "manifest.json")))
            {
                await System.Text.Json.JsonSerializer.SerializeAsync(stream, new AgentInstanceManifest
                {
                    AgentInstanceId = agentId,
                    WorkspaceId = "default",
                    TemplateId = "general-assistant",
                    DisplayName = "Policy Agent",
                }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                {
                    WriteIndented = true,
                });
            }
            var services = new ServiceCollection();
            if (authorization is not null) services.AddSingleton(authorization);
            var provider = services.BuildServiceProvider();
            var service = new WorkspaceAgentFileService(paths, null!, null!, provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WorkspaceAgentFileService>.Instance);
            return new(root, service, agentId);
        }

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
            return ValueTask.CompletedTask;
        }
    }
}
