using PuddingCode.SubAgents;

namespace PuddingPlatformTests.Services;

/// <summary>
/// SubAgentRunCancellationRegistry 契约测试。
///
/// 这些用例的存在理由：缺陷 D1 的根因是「真正驱动执行的 CancellationTokenSource 无法被外部取得」，
/// 因此修复的第一层保障必须是——登记进来的令牌**确实能被取消**，且取消后仍可查询、可按序移除。
/// 每条用例都能取红：删掉 Cancel 里的 cts.Cancel()、把 Register 改成不联结 external、
/// 或把 Cancel 改成顺手移除条目，都会让对应用例失败。
/// </summary>
[TestClass]
public sealed class SubAgentRunCancellationRegistryTests
{
    // ═══════════════════════════════════════════════════════════
    // Register
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public void Register_ReturnsTokenLinkedToExternal()
    {
        var registry = new SubAgentRunCancellationRegistry();
        using var external = new CancellationTokenSource();

        var token = registry.Register("sub-1", external.Token);
        Assert.IsFalse(token.IsCancellationRequested, "Newly registered run must start uncancelled.");

        external.Cancel();

        Assert.IsTrue(token.IsCancellationRequested,
            "Cancelling the external source must propagate to the registered run token.");
    }

    [TestMethod]
    public void Register_SameKeyTwice_CancelsPreviousToken()
    {
        var registry = new SubAgentRunCancellationRegistry();
        var first = registry.Register("sub-1", CancellationToken.None);
        Assert.IsFalse(first.IsCancellationRequested);

        var second = registry.Register("sub-1", CancellationToken.None);

        Assert.IsTrue(first.IsCancellationRequested,
            "Re-registering the same run must cancel the stale token, otherwise the old loop keeps spending.");
        Assert.IsFalse(second.IsCancellationRequested);
        Assert.AreEqual(1, registry.Count);
    }

    [TestMethod]
    public void Register_EmptyKey_Throws()
    {
        var registry = new SubAgentRunCancellationRegistry();

        AssertThrows<ArgumentException>(() => registry.Register("", CancellationToken.None));
        AssertThrows<ArgumentException>(() => registry.Register("   ", CancellationToken.None));
    }

    // ═══════════════════════════════════════════════════════════
    // Cancel —— D1 的核心断言
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public void Cancel_CancelsRegisteredToken()
    {
        var registry = new SubAgentRunCancellationRegistry();
        var token = registry.Register("sub-1", CancellationToken.None);

        var cancelled = registry.Cancel("sub-1");

        Assert.IsTrue(cancelled, "Cancel must report a hit for a registered run.");
        Assert.IsTrue(token.IsCancellationRequested,
            "D1: cancel must actually signal the running token. Status-only bookkeeping is the defect.");
    }

    [TestMethod]
    public void Cancel_UnknownKey_ReturnsFalseWithoutThrowing()
    {
        var registry = new SubAgentRunCancellationRegistry();

        Assert.IsFalse(registry.Cancel("never-registered"));
        Assert.IsFalse(registry.Cancel(""));
        Assert.IsFalse(registry.Cancel("   "));
    }

    [TestMethod]
    public void Cancel_DoesNotRemoveEntry()
    {
        var registry = new SubAgentRunCancellationRegistry();
        registry.Register("sub-1", CancellationToken.None);

        registry.Cancel("sub-1");

        Assert.IsTrue(registry.IsRegistered("sub-1"),
            "Cancel must not remove the entry: terminal bookkeeping happens later and needs to observe it.");
        Assert.IsTrue(registry.IsCancellationRequested("sub-1"));
    }

    [TestMethod]
    public void Cancel_IsIdempotent()
    {
        var registry = new SubAgentRunCancellationRegistry();
        registry.Register("sub-1", CancellationToken.None);

        Assert.IsTrue(registry.Cancel("sub-1"));
        Assert.IsTrue(registry.Cancel("sub-1"), "Second cancel on a still-registered run must still report a hit.");
    }

    // ═══════════════════════════════════════════════════════════
    // IsCancellationRequested / IsRegistered
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public void IsCancellationRequested_UnknownKey_ReturnsFalse()
    {
        var registry = new SubAgentRunCancellationRegistry();

        Assert.IsFalse(registry.IsCancellationRequested("unknown"),
            "Unknown must not be reported as cancelled — D2 taught us not to fold 'unknown' into a concrete value.");
    }

    [TestMethod]
    public void IsRegistered_ReflectsLifecycle()
    {
        var registry = new SubAgentRunCancellationRegistry();
        Assert.IsFalse(registry.IsRegistered("sub-1"));

        registry.Register("sub-1", CancellationToken.None);
        Assert.IsTrue(registry.IsRegistered("sub-1"));

        registry.TryRemove("sub-1");
        Assert.IsFalse(registry.IsRegistered("sub-1"));
    }

    // ═══════════════════════════════════════════════════════════
    // TryRemove
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public void TryRemove_ReleasesEntry()
    {
        var registry = new SubAgentRunCancellationRegistry();
        var token = registry.Register("sub-1", CancellationToken.None);

        Assert.IsTrue(registry.TryRemove("sub-1"));

        Assert.AreEqual(0, registry.Count);
        Assert.IsFalse(registry.IsRegistered("sub-1"));
        Assert.IsFalse(registry.Cancel("sub-1"), "After removal there is nothing to cancel.");
        Assert.IsFalse(token.IsCancellationRequested,
            "TryRemove is cleanup after a terminal state, not a cancellation.");
    }

    [TestMethod]
    public void TryRemove_UnknownKey_ReturnsFalse()
    {
        var registry = new SubAgentRunCancellationRegistry();
        Assert.IsFalse(registry.TryRemove("unknown"));
        Assert.IsFalse(registry.TryRemove(""));
    }

    // ═══════════════════════════════════════════════════════════
    // 并发
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public void Concurrent_RegisterAndCancel_DoesNotThrow()
    {
        var registry = new SubAgentRunCancellationRegistry();
        var tokens = new System.Collections.Concurrent.ConcurrentBag<CancellationToken>();

        Parallel.For(0, 200, i =>
        {
            var key = "sub-" + (i % 20);
            tokens.Add(registry.Register(key, CancellationToken.None));
            registry.Cancel(key);
            registry.IsCancellationRequested(key);
            if (i % 7 == 0) registry.TryRemove(key);
        });

        Assert.IsTrue(registry.Count <= 20, $"Count={registry.Count} exceeded the number of distinct keys.");
        Assert.IsTrue(tokens.Count == 200);
    }

    // ═══════════════════════════════════════════════════════════
    // helper
    // ═══════════════════════════════════════════════════════════

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            Assert.Fail($"Expected {typeof(TException).Name} but no exception was thrown.");
        }
        catch (TException)
        {
            // expected
        }
    }
}
