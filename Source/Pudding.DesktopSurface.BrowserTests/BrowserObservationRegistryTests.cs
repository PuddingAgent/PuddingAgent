using PuddingBrowser.Abstractions;
using Xunit;

namespace DesktopSurfaceBrowserTests;

/// <summary>
/// 观察登记处（§8.2）：登记 → 解析；**版本或世代不符即失效并移除**；结构非法在登记时拒绝。全为纯逻辑。
/// （放在本工程而非 `PuddingBrowser.AutomationTests`：后者按 S2 只能引用被测组件。）
/// </summary>
public sealed class BrowserObservationRegistryTests
{
    private static BrowserObservation Observation(
        string id, long pageVersion = 5, long generation = 1, double zoom = 1)
        => new(
            new BrowserObservationId(id),
            pageVersion,
            generation,
            ScrollX: 0,
            ScrollY: 120,
            Zoom: zoom,
            ViewportWidth: 1280,
            ViewportHeight: 720,
            LayoutRevision: 3,
            ObservedAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public void RegisteredObservationResolvesWithinItsVersionAndGeneration()
    {
        var registry = new InMemoryBrowserObservationRegistry();
        registry.Register(Observation("obs-1"));

        Assert.True(registry.TryResolve(new BrowserObservationId("obs-1"), 5, 1, out var found));
        Assert.Equal("obs-1", found.Id.Value);
        Assert.Equal(120, found.ScrollY);
    }

    [Fact]
    public void ANewPageVersionExpiresTheObservationAndRemovesIt()
    {
        var registry = new InMemoryBrowserObservationRegistry();
        registry.Register(Observation("obs-1"));

        Assert.False(registry.TryResolve(new BrowserObservationId("obs-1"), pageVersion: 6, generation: 1, out _));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void ANewConnectionGenerationExpiresTheObservation()
    {
        var registry = new InMemoryBrowserObservationRegistry();
        registry.Register(Observation("obs-1"));

        Assert.False(registry.TryResolve(new BrowserObservationId("obs-1"), pageVersion: 5, generation: 2, out _));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void AnUnknownOrEmptyIdNeverResolves()
    {
        var registry = new InMemoryBrowserObservationRegistry();

        Assert.False(registry.TryResolve(new BrowserObservationId("never-registered"), 5, 1, out _));
        Assert.False(registry.TryResolve(new BrowserObservationId("  "), 5, 1, out _));
    }

    [Fact]
    public void StructurallyInvalidObservationsAreRefusedAtRegistration()
    {
        var registry = new InMemoryBrowserObservationRegistry();

        // 缩放必须为正（0 会让坐标变换失去意义）。
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.Register(Observation("obs-1", zoom: 0)));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void InvalidatingKeepsOnlyTheGivenScope()
    {
        var registry = new InMemoryBrowserObservationRegistry();
        registry.Register(Observation("old", pageVersion: 5));
        registry.Register(Observation("current", pageVersion: 6));

        registry.InvalidateForPage(pageVersion: 6, generation: 1);

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryResolve(new BrowserObservationId("current"), 6, 1, out _));
    }
}