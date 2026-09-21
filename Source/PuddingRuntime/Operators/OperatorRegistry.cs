using System.Collections.Concurrent;
using PuddingCode.Operators;

namespace PuddingRuntime.Operators;

/// <summary>
/// 进程内场景算子注册表（S1b 交付物 1）：把「场景键 → 算子实例」收成单一装配点，并在<b>注册期</b>执行
/// 三条 fail-closed 守卫（见 <see cref="IOperatorRegistry"/> 文档），使装配错误在启动 / 组合期就暴露，
/// 而不是等到某次真实判定时才发现「这个场景没人实现」。
/// <para>
/// 读路径无锁（<see cref="ConcurrentDictionary{TKey,TValue}"/>），写路径用一把小锁把「查重 → 落库」
/// 做成原子操作——否则并发注册同一场景键时，两个线程可能都通过查重、后写覆盖先写（正是守卫 1 要拦的静默覆盖）。
/// </para>
/// <para>
/// 本类<b>不做</b>生命周期管理（不持有 / 不释放算子实例）：算子由组合根按自身生命周期注册，
/// 注册表只是索引。这与既有「服务由 DI 拥有、由容器释放」的约定一致。
/// </para>
/// </summary>
public sealed class OperatorRegistry : IOperatorRegistry
{
    /// <summary>
    /// 原语端口集合（守卫 2 的判定域）。<b>新增原语必须显式加进来</b>——否则守卫会静默漏判。
    /// </summary>
    private static readonly Type[] PrimitivePorts =
    [
        typeof(IScorer),
        typeof(IJudge),
        typeof(IClassifier),
    ];

    private readonly ConcurrentDictionary<string, IOperator> _operators = new(StringComparer.Ordinal);
    private readonly object _registerGate = new();

    /// <summary>已注册算子数量（诊断用）。</summary>
    public int Count => _operators.Count;

    /// <inheritdoc />
    public IReadOnlyCollection<string> SceneKeys =>
        _operators.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// 注册一个场景算子（守卫 3 → 2 → 1 依次检查；任一违反即抛，<b>不做部分写入</b>）。
    /// </summary>
    /// <typeparam name="TPort">本次注册所声明的原语端口类型。</typeparam>
    /// <param name="sceneKey">场景键（非空、非空白）。</param>
    /// <param name="op">算子实例（非 null）。</param>
    /// <exception cref="ArgumentException"><paramref name="sceneKey"/> 为 null / 空 / 空白。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="op"/> 为 null。</exception>
    /// <exception cref="InvalidOperationException">
    /// 同一 <paramref name="sceneKey"/> 已注册（守卫 1）；或 <typeparamref name="TPort"/> 的类型实现了多于一个原语端口（守卫 2）。
    /// </exception>
    public void Register<TPort>(string sceneKey, TPort op)
        where TPort : class, IOperator
    {
        // 守卫 3：空 / 空白场景键。空键会退化成「隐式默认场景」，把漏配伪装成命中。
        if (string.IsNullOrWhiteSpace(sceneKey))
        {
            throw new ArgumentException(
                "sceneKey 不得为 null / 空 / 空白：空键会退化为隐式默认场景，掩盖漏配。",
                nameof(sceneKey));
        }

        ArgumentNullException.ThrowIfNull(op);

        // 守卫 2：一个类型最多实现一个原语端口（否则「分」与「是 / 否」的输出语义会再次混淆）。
        EnsureSinglePrimitivePort(op.GetType());

        lock (_registerGate)
        {
            // 守卫 1：重复注册必须显式失败（静默覆盖会让装配错误无声生效）。
            if (_operators.TryGetValue(sceneKey, out var existing))
            {
                throw new InvalidOperationException(
                    $"场景键 '{sceneKey}' 已被 {existing.GetType().FullName} 注册：重复注册必须显式失败，" +
                    "不得静默覆盖（覆盖会让装配错误静默生效）。");
            }

            _operators[sceneKey] = op;
        }
    }

    /// <summary>已注册场景键数量为零时返回 true（供组合根做「至少装配一个场景」自检）。</summary>
    public bool IsEmpty => _operators.IsEmpty;

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="sceneKey"/> 为 null / 空 / 空白。</exception>
    public bool TryResolve<TPort>(string sceneKey, out TPort? op)
        where TPort : class, IOperator
    {
        if (string.IsNullOrWhiteSpace(sceneKey))
        {
            throw new ArgumentException(
                "sceneKey 不得为 null / 空 / 空白：空键不按「未命中」处理，避免把漏配伪装成未命中。",
                nameof(sceneKey));
        }

        // 端口不匹配按「未命中该端口」处理：调用方查的是「这个场景有没有会打分的算子」，
        // 注册的是分类算子时答案就是「没有」，而不是把一个语义不符的实例硬塞回去。
        if (_operators.TryGetValue(sceneKey, out var registered) && registered is TPort typed)
        {
            op = typed;
            return true;
        }

        op = null;
        return false;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="sceneKey"/> 为 null / 空 / 空白。</exception>
    /// <exception cref="InvalidOperationException">场景未注册，或该场景的算子未实现 <typeparamref name="TPort"/>。</exception>
    public TPort Resolve<TPort>(string sceneKey)
        where TPort : class, IOperator
    {
        if (string.IsNullOrWhiteSpace(sceneKey))
        {
            throw new ArgumentException(
                "sceneKey 不得为 null / 空 / 空白：空键不按「未命中」处理，避免把漏配伪装成未命中。",
                nameof(sceneKey));
        }

        if (!_operators.TryGetValue(sceneKey, out var registered))
        {
            var known = string.Join("、", SceneKeys);
            throw new InvalidOperationException(
                $"场景键 '{sceneKey}' 未注册任何算子（已注册场景：{(known.Length == 0 ? "<无>" : known)}）：" +
                "按 fail-closed 契约拒绝继续，不得在缺少场景算子时静默放行。");
        }

        if (registered is TPort typed)
        {
            return typed;
        }

        throw new InvalidOperationException(
            $"场景键 '{sceneKey}' 注册的算子 {registered.GetType().FullName} 未实现端口 {typeof(TPort).Name}：" +
            "按 fail-closed 契约拒绝继续（端口不符说明装配写错，不得按未命中降级）。");
    }

    /// <summary>
    /// 守卫 2：拒绝「一个类型实现多于一个原语端口」。
    /// <para>
    /// 实现 0 个原语端口<b>不</b>在此拒绝（规格只要求拒绝 &gt;1）：这类实例能在注册表里存在，
    /// 但任何原语端口都解析不到它，属可观测的装配冗余，而不是语义混淆风险。
    /// </para>
    /// </summary>
    private static void EnsureSinglePrimitivePort(Type operatorType)
    {
        var implemented = PrimitivePorts.Where(port => port.IsAssignableFrom(operatorType)).ToArray();
        if (implemented.Length <= 1)
        {
            return;
        }

        var names = string.Join("、", implemented.Select(port => port.Name));
        throw new InvalidOperationException(
            $"算子类型 {operatorType.FullName} 同时实现了 {implemented.Length} 个原语端口（{names}）：" +
            "一个类型最多实现一个原语端口——否则「分」与「是 / 否」的输出语义会再次混淆；" +
            "请拆成多个各自只实现一个端口的类型。");
    }
}
