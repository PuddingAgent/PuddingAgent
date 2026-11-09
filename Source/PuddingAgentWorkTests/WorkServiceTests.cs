using Pudding.Api.Contracts;
using PuddingAgentWork;
using PuddingAgentWork.Contracts;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
namespace PuddingAgentWorkTests;

[TestClass]
public sealed class WorkServiceTests
{
    private static readonly AgentRef Target = new("workspace", "agent");
    [TestMethod]
    public async Task StableKeys_AreScopedToActorAgentWorkspace_AndTextIsNotTrimmed()
    {
        var backend = new Backend();
        var service = new AgentWorkService(backend, new Grant(true));
        var api = service.ForCaller(new("owner"));
        var a = await api.SubmitAsync(Target, new("op", " hello "));
        var hash = backend.Last!.RequestHash;
        var b = await api.SubmitAsync(Target, new("op", "hello"));
        Assert.AreEqual(a.WorkId, b.WorkId); Assert.AreNotEqual(hash, backend.Last!.RequestHash);
        Assert.AreNotEqual(a.WorkId, (await service.ForCaller(new("another")).SubmitAsync(Target, new("op", "hello"))).WorkId);
        Assert.AreNotEqual(a.WorkId, (await api.SubmitAsync(new("other", "agent"), new("op", "hello"))).WorkId);
        Assert.AreNotEqual(a.WorkId, (await api.SubmitAsync(new("workspace", "other"), new("op", "hello"))).WorkId);
        Assert.IsNull(await api.GetAsync(Target, "op"));
        Assert.AreEqual(a.WorkId, backend.ReadKey!.WorkId);
    }

    [TestMethod]
    public async Task DeniedCaller_NeverReachesBackend_EvenOnRead()
    {
        var backend = new Backend(); var api = new AgentWorkService(backend, new Grant(false)).ForCaller(new("owner"));
        Assert.AreEqual("permission.denied", (await Assert.ThrowsExactlyAsync<AgentWorkException>(() => api.SubmitAsync(Target, new("op", "hello")))).Code);
        await Assert.ThrowsExactlyAsync<AgentWorkException>(() => api.GetAsync(Target, "op"));
        Assert.IsNull(backend.Last); Assert.IsNull(backend.ReadKey);
    }

    [TestMethod]
    public async Task InvalidRequests_DoNotSubmit()
    {
        var backend = new Backend(); var api = new AgentWorkService(backend, new Grant(true)).ForCaller(new("owner"));
        foreach (var request in new[] { new AgentWorkRequest("../x", "text"), new("op", " "), new("op", new string('x', 32769)) })
            Assert.AreEqual("request.invalid", (await Assert.ThrowsExactlyAsync<AgentWorkException>(() => api.SubmitAsync(Target, request))).Code);
        Assert.IsNull(backend.Last);
    }

    [TestMethod]
    public async Task LostResponseAndCancellationAfterHandoff_AreUnknown()
    {
        foreach (var error in new Exception[] { new IOException("secret"), new OperationCanceledException() })
        {
            var backend = new Backend { Failure = error }; var api = new AgentWorkService(backend, new Grant(true)).ForCaller(new("owner"));
            var ex = await Assert.ThrowsExactlyAsync<AgentWorkException>(() => api.SubmitAsync(Target, new("op", "hello")));
            Assert.AreEqual("unknown", ex.EffectOutcome); Assert.DoesNotContain(ex.Message, "secret");
        }
    }

    [TestMethod]
    public async Task CancellationBeforeHandoff_DoesNotSubmit()
    {
        var backend = new Backend(); var api = new AgentWorkService(backend, new Grant(true)).ForCaller(new("owner"));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => api.SubmitAsync(Target, new("op", "hello"), new(true)));
        Assert.IsNull(backend.Last);
    }

    [TestMethod]
    public async Task ForeignReceipt_IsNeverAccepted()
    {
        var backend = new Backend { CorruptReceipt = true }; var api = new AgentWorkService(backend, new Grant(true)).ForCaller(new("owner"));
        Assert.AreEqual("receipt.invalid", (await Assert.ThrowsExactlyAsync<AgentWorkException>(() => api.SubmitAsync(Target, new("op", "hello")))).Code);
    }

    private sealed class Grant(bool allowed) : IAgentWorkAuthorizer
    {
        public ValueTask<bool> IsAllowedAsync(WorkCaller caller, AgentRef agent, WorkAccess access, CancellationToken ct) => ValueTask.FromResult(allowed);
    }
    private sealed class Backend : IAgentWorkBackend
    {
        public WorkSubmission? Last; public WorkKey? ReadKey; public Exception? Failure; public bool CorruptReceipt;
        public Task<AgentWorkReceipt> SubmitAsync(WorkSubmission submission, CancellationToken ct)
        {
            Last = submission;
            if (Failure is not null) throw Failure;
            var key = submission.Key;
            return Task.FromResult(new AgentWorkReceipt(key.Agent, key.OperationId, CorruptReceipt ? "foreign" : key.WorkId, "session", "input", "turn", "command", 1, DateTimeOffset.UtcNow));
        }
        public Task<AgentWorkSnapshot?> ReadAsync(WorkKey key, CancellationToken ct) { ReadKey = key; return Task.FromResult<AgentWorkSnapshot?>(null); }
    }
}
