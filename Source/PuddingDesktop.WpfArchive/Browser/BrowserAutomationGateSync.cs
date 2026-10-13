namespace PuddingDesktop.Browser;

/// <summary>
/// 「接管 / 暂停」的**执行闸门**端口：读它的路径（Bridge dispatcher、能力通道准入）决定
/// 要不要拒绝新的 Agent 动作。
///
/// 为什么需要这个端口（审计 2026-10-08 结论 #1）：进程内曾有三份互不相通的接管表示，
/// 而产品 Shell 只写其中最不具执行权威的一份 —— 用户点「人工接管」只改了 UI 状态机，
/// 两条执行闸门读的是各自的字段，于是按钮对 Agent **没有约束力**。
/// 把「谁读它」收敛到一个端口，Write 侧就不可能只写一半。
/// </summary>
public interface IBrowserAutomationGate
{
    /// <summary>用户是否接管了浏览器；为真时执行路径必须拒绝新的变更类动作。</summary>
    void SetUserTakeover(bool takenOver);

    /// <summary>工具运行时是否被暂停。</summary>
    void SetPaused(bool paused);

    /// <summary>
    /// 用户把某页设为（或清除）Agent 目标。
    ///
    /// 这是**唯一的授权动作**，它必须能到达准入侧：否则 Bridge 闸门永远读不到「用户已选页」，
    /// Agent 就只能被拒（实测：`page_grant_required` 与 UI 上按钮点击无任何关联）。
    /// 默认实现为空，让不关心目标语义的闸门无需改动。
    /// </summary>
    void SetAgentTarget(string? contextId, string? pageId)
    {
    }
}

/// <summary>
/// 用两个委托实现的闸门：给「不方便实现本端口」的执行路径用（典型是
/// <c>Pudding.DesktopService.DesktopInteractionState</c>——它属于更低层，不能反向引用本组件）。
///
/// 这样 Shell 侧只剩一行装配，而映射语义（谁读它、读什么）仍由各闸门自己拥有。
/// </summary>
public sealed class DelegateAutomationGate : IBrowserAutomationGate
{
    private readonly Action<bool> _setUserTakeover;
    private readonly Action<bool> _setPaused;
    private readonly Action<string?, string?>? _setAgentTarget;

    public DelegateAutomationGate(Action<bool> setUserTakeover, Action<bool> setPaused, Action<string?, string?>? setAgentTarget = null)
    {
        _setUserTakeover = setUserTakeover ?? throw new ArgumentNullException(nameof(setUserTakeover));
        _setPaused = setPaused ?? throw new ArgumentNullException(nameof(setPaused));
        _setAgentTarget = setAgentTarget;
    }

    public void SetUserTakeover(bool takenOver) => _setUserTakeover(takenOver);

    public void SetPaused(bool paused) => _setPaused(paused);

    public void SetAgentTarget(string? contextId, string? pageId) => _setAgentTarget?.Invoke(contextId, pageId);
}

/// <summary>
/// 接管/暂停的**唯一写入点**：UI 改一次控制状态，所有执行闸门一起改。
///
/// 语义要点：
/// · 注册与撤销都可用（能力通道启停时会注册/撤销它自己的闸门）；
/// · 某个闸门抛异常**不得**阻止其它闸门同步（否则一个坏闸门会让接管静默失效）；
/// · 幂等：重复注册同一个实例只保留一份。
/// </summary>
public sealed class BrowserAutomationGateSync
{
    private readonly object _sync = new();
    private readonly List<IBrowserAutomationGate> _gates = [];

    /// <summary>当前已注册的闸门数量（诊断与测试用）。</summary>
    public int GateCount
    {
        get
        {
            lock (_sync)
            {
                return _gates.Count;
            }
        }
    }

    /// <summary>注册闸门；同一个实例重复注册只保留一份。</summary>
    public void Register(IBrowserAutomationGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);

        lock (_sync)
        {
            if (!_gates.Contains(gate))
            {
                _gates.Add(gate);
            }
        }
    }

    /// <summary>撤销闸门（能力通道停止时调用，避免向已释放的路径推送状态）。</summary>
    public bool Unregister(IBrowserAutomationGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);

        lock (_sync)
        {
            return _gates.Remove(gate);
        }
    }

    /// <summary>把「用户接管」推给全部闸门。</summary>
    public void ApplyUserTakeover(bool takenOver) => Apply(gate => gate.SetUserTakeover(takenOver));

    /// <summary>把「运行时暂停」推给全部闸门。</summary>
    public void ApplyPaused(bool paused) => Apply(gate => gate.SetPaused(paused));

    /// <summary>
    /// 把「用户选中的 Agent 目标页」推给全部闸门（<c>null</c> 表示清除目标）。
    ///
    /// 与接管/暂停同理：这是 UI 的唯一授权写入点，不下发就会出现
    /// 「按钮点了、准入侧不知道」的分叉（实测：同一意图产生三种拒绝码）。
    /// </summary>
    public void ApplyAgentTarget(string? contextId, string? pageId) =>
        Apply(gate => gate.SetAgentTarget(contextId, pageId));

    /// <summary>
    /// 把完整控制状态推给**单个**闸门：新闸门注册时对齐用，
    /// 否则它会停在构造时的旧值上（例如能力通道在用户已接管之后才启动）。
    /// </summary>
    public void ApplyTo(IBrowserAutomationGate gate, bool takenOver, bool paused)
    {
        ArgumentNullException.ThrowIfNull(gate);

        ApplyTo(gate, () =>
        {
            gate.SetUserTakeover(takenOver);
            gate.SetPaused(paused);
        });
    }

    private void Apply(Action<IBrowserAutomationGate> apply)
    {
        IBrowserAutomationGate[] snapshot;
        lock (_sync)
        {
            snapshot = _gates.ToArray();
        }

        foreach (var gate in snapshot)
        {
            ApplyTo(gate, () => apply(gate));
        }
    }

    /// <summary>
    /// 单个闸门失败不得影响其它闸门：接管是安全动作，宁可继续推给其余闸门，
    /// 也不能因为一个抛异常而让「用户接管」在半数路径上失效。
    /// </summary>
    private static void ApplyTo(IBrowserAutomationGate gate, Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // 有意吞掉：见上方说明。闸门自身负责记录诊断。
        }
    }
}
