using Pudding.Contracts.Computer;

namespace PuddingComputer.Automation;

/// <summary>
/// 观察登记表：判断某个观察在**当前事实**下是否仍然可信。
/// <para>
/// 设计 §7.1 的规则不是"超过 N 秒就作废"这么简单。观察携带四个独立 revision
/// （显示布局 / 窗口几何 / 内容）与一个到期时刻；任何一项变了都可能让旧观察失去意义：
/// </para>
/// <list type="bullet">
/// <item>窗口一移动，坐标就偏了 —— 但此时可能才过去 300ms，只看时间完全察觉不到；</item>
/// <item>显示布局变化（插拔显示器、改缩放）会让整块屏幕的坐标系改变；</item>
/// <item>写动作会改变界面本身，所以**本窗口既有观察一律失效**（保守处理部分派发与结果未知）。</item>
/// </list>
/// <para>
/// 有界：超过 <see cref="Capacity"/> 时淘汰最旧的一条。观察登记表不该无限增长 ——
/// 它记录的是"刚刚发生过什么"，不是历史。
/// </para>
/// </summary>
public sealed class ComputerObservationRegistry
{
    public const int Capacity = 256;

    private readonly Dictionary<ComputerObservationId, ComputerObservation> _entries = new();
    private readonly Queue<ComputerObservationId> _order = new();

    public int Count => _entries.Count;

    /// <summary>登记一次观察。重复登记同一 id 会覆盖（视为同一观察的更新）。</summary>
    public void Register(ComputerObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var id = observation.ObservationId;

        if (_entries.ContainsKey(id))
        {
            _entries[id] = observation;
            return;
        }

        _entries[id] = observation;
        _order.Enqueue(id);

        // 有界：按登记顺序淘汰最旧
        while (_order.Count > Capacity)
        {
            var oldest = _order.Dequeue();
            _entries.Remove(oldest);
        }
    }

    /// <summary>
    /// 该观察在当前事实下是否仍然可信。
    /// <para>
    /// 判据是**事实相等**而不是时间差：三个 revision 任一变化即失效，再叠加到期时刻。
    /// 时间只作为最后一道保险，不作为主要判据。
    /// </para>
    /// </summary>
    public bool IsValid(
        ComputerObservationId id,
        long displayLayoutRevision,
        long windowGeometryRevision,
        long contentRevision,
        DateTimeOffset now)
    {
        if (!_entries.TryGetValue(id, out var o))
            return false;

        if (now >= o.ExpiresAtUtc)
            return false;

        // 内容变化也判失效：元素 ref 指向的控件可能已经不存在了。
        // 这里选择保守——宁可多截一次图，也不要在旧 ref 上执行动作。
        return o.DisplayLayoutRevision == displayLayoutRevision
            && o.WindowGeometryRevision == windowGeometryRevision
            && o.ContentRevision == contentRevision;
    }

    /// <summary>
    /// 写动作之后让该窗口的全部既有观察失效。
    /// <para>
    /// 保守范围包括"部分派发"和"结果未知"：动作可能已经生效了一部分，
    /// 此时界面与刚才看到的已经不一定是同一份画面。
    /// </para>
    /// </summary>
    public int InvalidateWindow(ComputerWindowId windowId)
    {
        var doomed = _entries
            .Where(kv => kv.Value.Window is { } w && w.WindowId == windowId)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in doomed) _entries.Remove(id);
        return doomed.Count;
    }

    /// <summary>丢弃某条观察（例如调用方主动放弃）。</summary>
    public bool Forget(ComputerObservationId id) => _entries.Remove(id);

    public void Clear()
    {
        _entries.Clear();
        _order.Clear();
    }
}
