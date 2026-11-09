using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Runtime;

namespace Pudding.ToolPlugins.RuntimeTests;

[TestClass]
public class HostToolMountTests
{
    [TestMethod]
    public async Task RevokeBeforeDrain_RejectsStaleAndCrossSessionCalls()
    {
        await using var mounts = new HostToolMounts();
        var pack = new HostToolPackage("pudding.files", "1.0.0", new() { ["file_read"] = "FileRead" }, "hash");
        var scope = new PluginScope("workspace", "agent", "session");
        var a = mounts.Load(pack, scope, "load", 0);
        Assert.AreEqual(a, mounts.Load(pack, scope, "load", 0));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = mounts.RunAsync(a.ActivationId, scope, async ct =>
        {
            entered.SetResult();
            await finish.Task;
            return ct.IsCancellationRequested;
        }, default);
        await entered.Task;
        await Assert.ThrowsExactlyAsync<PluginException>(() =>
            mounts.RunAsync(a.ActivationId, scope with { SessionId = "other" }, _ => Task.FromResult(1), default));
        var stop = mounts.UnloadAsync(a.ActivationId, scope, a.Revision, default);
        Assert.IsFalse(stop.IsCompleted);
        await Assert.ThrowsExactlyAsync<PluginException>(() =>
            mounts.RunAsync(a.ActivationId, scope, _ => Task.FromResult(1), default));
        finish.SetResult();
        Assert.IsTrue(await running);
        Assert.AreEqual("Unloaded", (await stop).State);
        Assert.AreEqual("Unloaded", mounts.Load(pack, scope, "load", null).State);
        Assert.AreNotEqual(a.ActivationId, mounts.Load(pack, scope, "reload", null).ActivationId);
    }
}
