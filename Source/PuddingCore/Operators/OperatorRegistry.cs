namespace PuddingCode.Operators;

/// <summary>
/// 场景算子注册表（<b>键控</b>）：按「场景键 + 原语端口」解析算子实例。
/// <para>
/// 存在的理由：S1a 的三原语端口是<b>惰性抽象</b>——端口本身不解决「谁来选算子」。
/// 没有注册表时，选算子会散落成调用方的 <c>if (scene == ...)</c>，抽象随即腐化；
/// 注册表把「场景 → 算子」收成<b>一处显式装配</b>，并让守卫在<b>注册期</b>（而非运行期）失败。
/// </para>
/// <para>
/// <b>fail-closed 语义（调用方必须遵守）</b>：解析不到算子一律不返回 <c>null</c> 让调用方自己猜，
/// 而是抛异常；「不确定就升级」的档位由调用方按 <see cref="JudgeOutcome"/> / 降级原因码处理。
/// </para>
/// <para>
/// <b>注册期守卫（由实现强制，违反即抛）</b>：
/// <list type="number">
/// <item>同一 <c>sceneKey</c> 重复注册 ⇒ 拒绝（<b>不得</b>静默覆盖：覆盖会让装配错误静默生效）；</item>
/// <item>同一个算子类型实现多于一个原语端口（如同时 <see cref="IJudge"/> 与 <see cref="IScorer"/>）⇒ 拒绝。
/// 理由（母文档 §4.3.2）：一个类同时实现两个原语，会让「分」与「是 / 否」的输出语义再次混淆；
/// 这条规则因此被做成<b>注册期守卫</b>，而不是文档里的口头约定；</item>
/// <item><c>sceneKey</c> 为空 / 空白 ⇒ 拒绝（空键会变成隐式默认场景，掩盖漏配）。</item>
/// </list>
/// </para>
/// </summary>
public interface IOperatorRegistry
{
    /// <summary>已注册的场景键（确定性顺序的快照；只读）。</summary>
    IReadOnlyCollection<string> SceneKeys { get; }

    /// <summary>
    /// 尝试解析指定场景下实现了 <typeparamref name="TPort"/> 的算子。
    /// </summary>
    /// <typeparam name="TPort">原语端口类型（<see cref="IScorer"/> / <see cref="IJudge"/> / <see cref="IClassifier"/>）。</typeparam>
    /// <param name="sceneKey">场景键；空 / 空白 ⇒ 抛（不静默当未命中）。</param>
    /// <param name="op">命中的算子；未命中为 null。</param>
    /// <returns>命中且端口匹配为 true；场景未注册或端口不匹配为 false（<b>不抛</b>）。</returns>
    bool TryResolve<TPort>(string sceneKey, out TPort? op) where TPort : class, IOperator;

    /// <summary>
    /// 解析指定场景下实现了 <typeparamref name="TPort"/> 的算子；<b>缺失 ⇒ 抛</b>（fail-closed）。
    /// </summary>
    /// <typeparam name="TPort">原语端口类型。</typeparam>
    /// <param name="sceneKey">场景键。</param>
    /// <exception cref="InvalidOperationException">场景未注册或该场景的算子未实现 <typeparamref name="TPort"/>。</exception>
    TPort Resolve<TPort>(string sceneKey) where TPort : class, IOperator;
}
