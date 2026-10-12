using PuddingHost.BrowserBridge;

namespace PuddingHost.Tests.BrowserBridge;

/// <summary>
/// 连接注册表的**世代防误杀**语义（实现注释原文：
/// "Detaches only if the connectionId AND generation match the current connection.
/// Prevents a stale finally-block from killing a newer connection."）。
/// 审计发现该行为此前**没有用例** —— 陈旧 finally 块若凭旧 connectionId/世代就能摘除现役连接，
/// 会把活着的连接判成断开。断言用 `Current`（挂载状态），**不用 `IsDesktopConnected`**（那是握手状态）。
/// </summary>
public sealed class DesktopBrowserConnectionRegistryTests
{
    [Fact]
    public void DetachRequiresBothTheConnectionIdAndTheCurrentGeneration()
    {
        var registry = new DesktopBrowserConnectionRegistry();
        var generation = registry.NextGeneration();
        var current = new DesktopBrowserConnection(Guid.NewGuid(), generation);

        Assert.True(registry.TryAttach(current));
        Assert.Same(current, registry.Current);

        // 世代陈旧（同 id）⇒ 忽略（陈旧 finally 不得杀掉现役连接）。
        registry.Detach(current.ConnectionId, generation - 1);
        Assert.Same(current, registry.Current);

        // 连接陈旧（不同 id，当前世代）⇒ 也忽略。
        registry.Detach(Guid.NewGuid(), generation);
        Assert.Same(current, registry.Current);

        // 只有 (connectionId, generation) **都**匹配才真正摘除。
        registry.Detach(current.ConnectionId, generation);
        Assert.Null(registry.Current);
    }

    [Fact]
    public void AttachIsRefusedWhileAnyConnectionIsPresent()
    {
        var registry = new DesktopBrowserConnectionRegistry();
        var first = new DesktopBrowserConnection(Guid.NewGuid(), registry.NextGeneration());
        var second = new DesktopBrowserConnection(Guid.NewGuid(), registry.NextGeneration());

        Assert.True(registry.TryAttach(first));
        // 已有连接（哪怕 AwaitingHello）时不得静默替换 —— 否则会出现僵尸接收循环。
        Assert.False(registry.TryAttach(second));

        // 旧连接必须先 Detach，新连接才能挂上。
        registry.Detach(first.ConnectionId, first.Generation);
        Assert.True(registry.TryAttach(second));
        Assert.Same(second, registry.Current);
    }
}