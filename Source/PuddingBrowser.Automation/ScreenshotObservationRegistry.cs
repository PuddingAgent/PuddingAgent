namespace PuddingBrowser.Automation;

/// <summary>
/// 截图观察登记处（设计 §8.2 的"**截图坐标必须携带 observationId**"这一半）：
/// 截图成功时登记元数据并签发 id；之后带 id 的坐标请求按 id 解析。
///
/// **单一真源**：值就是既有的 <see cref="BrowserScreenshotMetadata"/>（不另造平行类型）；
/// 失效判据就是既有的 <see cref="BrowserPageGenerations"/>。
/// </summary>
public interface IScreenshotObservationRegistry
{
    /// <summary>登记一次观察并签发 id（写入前做结构校验）。</summary>
    string Register(BrowserScreenshotMetadata metadata);

    /// <summary>
    /// 仅当 id 存在且**页面世代完全一致**（版本/文档修订/视口修订）时解析成功。
    /// 不一致 ⇒ **视为过期、移除并失败**（绝不用过期几何去点击）。
    /// </summary>
    bool TryResolve(string? observationId, BrowserPageGenerations current, out BrowserScreenshotMetadata metadata);

    /// <summary>页面换代/换版本时整体作废。</summary>
    void InvalidateForPage(long pageVersion);

    int Count { get; }
}

/// <summary>线程安全的内存实现。过期即**移除**（不留"过期但可查"的条目）。</summary>
public sealed class InMemoryScreenshotObservationRegistry : IScreenshotObservationRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BrowserScreenshotMetadata> _observations =
        new(StringComparer.Ordinal);

    public int Count => _observations.Count;

    public string Register(BrowserScreenshotMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (string.IsNullOrWhiteSpace(metadata.MimeType) || metadata.ByteCount <= 0)
        {
            throw new ArgumentException("An observation must carry a MIME type and a positive byte count.", nameof(metadata));
        }

        // 调用方应已给出 id；若为空则**明确失败**（不悄悄补一个，避免"身份来源"含混）。
        if (string.IsNullOrWhiteSpace(metadata.ObservationId))
        {
            throw new ArgumentException("An observation must carry a non-empty observation id.", nameof(metadata));
        }

        _observations[metadata.ObservationId] = metadata;
        return metadata.ObservationId;
    }

    public bool TryResolve(string? observationId, BrowserPageGenerations current, out BrowserScreenshotMetadata metadata)
    {
        metadata = null!;
        if (string.IsNullOrWhiteSpace(observationId))
        {
            return false;
        }

        if (!_observations.TryGetValue(observationId, out var found))
        {
            return false;
        }

        // 世代三元组任一不同 ⇒ 过期：移除并失败。
        var stale = found.PageVersion != current.PageVersion
            || found.DomRevision != current.DomRevision
            || found.ViewportRevision != current.ViewportRevision;
        if (stale)
        {
            _observations.TryRemove(observationId, out _);
            return false;
        }

        metadata = found;
        return true;
    }

    public void InvalidateForPage(long pageVersion)
    {
        foreach (var pair in _observations)
        {
            if (pair.Value.PageVersion != pageVersion)
            {
                _observations.TryRemove(pair.Key, out _);
            }
        }
    }
}