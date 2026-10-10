namespace PuddingBrowser.Abstractions;

/// <summary>观察 id（§8.2）：截图成功时签发，用于把"截图坐标系里的点"还原成当前 CSS viewport 坐标。</summary>
public readonly record struct BrowserObservationId(string Value)
{
    public bool IsKnown => !string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value;
}

/// <summary>
/// 一次观察的**事实快照**（只增不改）：截图当时的页面世代/版本/滚动/缩放/布局。
/// 后续这些事实中**任一变化**，该观察即失效 —— 用过期的观察去点击会点到错误位置。
/// </summary>
public sealed record BrowserObservation(
    BrowserObservationId Id,
    long PageVersion,
    long Generation,
    double ScrollX,
    double ScrollY,
    double Zoom,
    double ViewportWidth,
    double ViewportHeight,
    long LayoutRevision,
    DateTimeOffset ObservedAt)
{
    /// <summary>结构校验（在登记时调用）：id 必须已知、版本/世代/缩放必须为正。</summary>
    public void Validate()
    {
        if (!Id.IsKnown)
        {
            throw new ArgumentException("An observation must carry a non-empty id.", nameof(Id));
        }

        if (PageVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PageVersion), PageVersion, "Page version must be positive.");
        }

        if (Generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Generation), Generation, "Connection generation must be positive.");
        }

        if (Zoom <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Zoom), Zoom, "Zoom must be positive.");
        }
    }
}

/// <summary>
/// 观察登记处（§8.2）。**未登记或已失效的 id 一律解析失败** —— 调用方必须据此**明确拒绝**，
/// 绝不能用过期观察去点击。
/// </summary>
public interface IBrowserObservationRegistry
{
    BrowserObservationId Register(BrowserObservation observation);

    /// <summary>仅当 id 存在且**页面版本与连接世代完全一致**时解析成功。</summary>
    bool TryResolve(BrowserObservationId id, long pageVersion, long generation, out BrowserObservation observation);

    /// <summary>页面换代/换版本时整体作废该页的观察。</summary>
    void InvalidateForPage(long pageVersion, long generation);

    int Count { get; }
}

/// <summary>最小可用实现（线程安全）。观察只增不改；失效即**移除**（不保留"过期但仍可查"的状态）。</summary>
public sealed class InMemoryBrowserObservationRegistry : IBrowserObservationRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BrowserObservation> _observations =
        new(StringComparer.Ordinal);

    public int Count => _observations.Count;

    public BrowserObservationId Register(BrowserObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        observation.Validate();
        _observations[observation.Id.Value] = observation;
        return observation.Id;
    }

    public bool TryResolve(
        BrowserObservationId id, long pageVersion, long generation, out BrowserObservation observation)
    {
        observation = null!;
        if (!id.IsKnown)
        {
            return false;
        }

        if (!_observations.TryGetValue(id.Value, out var found))
        {
            return false;
        }

        // 世代或版本不符 ⇒ 视为失效并**移除**（避免过期观察被反复命中）。
        if (found.PageVersion != pageVersion || found.Generation != generation)
        {
            _observations.TryRemove(id.Value, out _);
            return false;
        }

        observation = found;
        return true;
    }

    public void InvalidateForPage(long pageVersion, long generation)
    {
        foreach (var pair in _observations)
        {
            if (pair.Value.PageVersion != pageVersion || pair.Value.Generation != generation)
            {
                _observations.TryRemove(pair.Key, out _);
            }
        }
    }
}