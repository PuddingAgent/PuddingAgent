using Pudding.ToolPlugins.AgentSdk;

namespace Pudding.ToolPlugins.AgentSdkTests;

/// <summary>PA-07 07B plugin job contract: host-bound envelope validity and explicit result states.</summary>
[TestClass]
public sealed class PluginJobContractTests
{
    [TestMethod]
    public void Envelope_RequiresBoundedIdentityPayloadAndFutureDeadline()
    {
        var envelope = new PluginJobEnvelope("job-1", "plugin-a", 3, "review.audit", """{"id":"1"}""",
            "idem-1", DateTimeOffset.UtcNow.AddMinutes(1), 1);
        Assert.IsTrue(envelope.IsValid);
        Assert.IsFalse((envelope with { Generation = 0 }).IsValid);
        Assert.IsFalse((envelope with { IdempotencyKey = "" }).IsValid);
        Assert.IsFalse((envelope with { Deadline = DateTimeOffset.UtcNow.AddMinutes(-1) }).IsValid);
        Assert.IsFalse((envelope with { MaxAttempts = 9 }).IsValid);
    }

    [TestMethod]
    public void Result_RequiresFailureCodeForFailedAndForbidsItForCompleted()
    {
        Assert.IsTrue(new PluginJobResult(PluginJobState.Completed, "done", null).IsValid);
        Assert.IsTrue(new PluginJobResult(PluginJobState.Failed, "failed", "check.failed").IsValid);
        Assert.IsFalse(new PluginJobResult(PluginJobState.Failed, "failed", null).IsValid);
        Assert.IsFalse(new PluginJobResult(PluginJobState.Completed, "done", "check.failed").IsValid);
        Assert.IsFalse(new PluginJobResult(PluginJobState.Running, "running", null).IsValid,
            "A handler cannot return the internal Running state as a final result.");
        Assert.IsTrue(new PluginJobResult(PluginJobState.Unknown, "provider outcome unknown", null).IsValid,
            "Unknown is an explicit third outcome, not success and not failure.");
    }
}
