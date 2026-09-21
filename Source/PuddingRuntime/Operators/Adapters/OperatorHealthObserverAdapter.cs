using Microsoft.Extensions.Logging;
using PuddingCode.Operators;
using PuddingRuntime.Classification;

namespace PuddingRuntime.Operators.Adapters;

/// <summary>
/// 算子健康旁挂的<b>生产实现</b>（S2b 交付物 2）：把 S1a 接缝 <see cref="IOperatorHealthObserver"/>
/// 接到<b>既有</b>健康面 <see cref="ClassifierHealthReporter"/> 上，并按 <c>(SceneKey, OperatorId)</c> 分区。
/// <para>
/// <b>为什么需要它</b>：S1a 定义了端口却<b>没有任何生产实现</b>——接缝因此是惰性的（定义了端口但没人实现
/// ⇒ 永远不会被调用）。本适配器是该接缝的第一个真实消费者，也是「旁挂按场景键泛化」的落点。
/// </para>
/// <para>
/// <b>映射（纯映射，不改任何被包装语义）</b>：
/// <list type="bullet">
/// <item><c>Succeeded=true</c> ⇒ <see cref="ClassifierHealthReporter.RecordSuccess"/>（既有语义：成功重置该
/// <b>分类器</b>名下全部键，即跨场景重置——本次不改为按场景重置，避免动既有重置语义）；</item>
/// <item><c>Succeeded=false</c> ⇒ <see cref="ClassifierHealthReporter.RecordDeferred"/>，键 =
/// <c>(OperatorId, SceneKey, <see cref="OperatorSampleToolId"/>, argsHash)</c>。算子采样<b>不带工具/参数维度</b>，
/// 因此工具位用显式具名常量占位、参数位传 <c>null</c>（既有算法语义：无参数 ⇒ 空哈希）——不伪造工具 id，
/// 也不落空串<b>场景</b>键（空场景键会把不同来源混进同一桶）。</item>
/// </list>
/// </para>
/// <para>
/// <b>旁挂纪律（接缝契约：实现抛异常不得影响裁决）</b>：本类<b>吞掉</b>自身异常，
/// <b>但绝不静默</b>——吞掉时记 <see cref="LogLevel.Warning"/>，并同时把失败计数与最近失败摘要暴露为
/// 只读属性（<see cref="SwallowedFailureCount"/> / <see cref="LastSwallowedFailure"/>），供探查与测试断言。
/// </para>
/// <para>
/// <b>哪一层是权威兜底（§3.3，禁止后人误删）：</b>
/// <list type="number">
/// <item><b>权威兜底层 = <c>OperatorBase</c></b>（S1a 基类）。它包住<b>任意</b>端口实现：只要有实现违反契约
/// 把异常抛出来，基类就会捕获并保证「裁决不受影响」。这层是<b>契约被违反时的唯一防线</b>，不可删。</item>
/// <item><b>本适配器 = 纵深防御的第二层</b>，且是<b>唯一记日志层</b>：本层在自己的协作里就把异常吞掉并记
/// Warning，因此对<b>本适配器路径</b>而言不存在「两层各记一条」的重复日志——基类的 catch 只会在异常真的
/// 冒泡时触发（即实现没按契约吞掉，例如第三方实现）。两层同时存在不是冗余：一层保证<b>裁决</b>安全，
/// 一层保证<b>故障可见</b>。</item>
/// </list>
/// </para>
/// </summary>
public sealed class OperatorHealthObserverAdapter : IOperatorHealthObserver
{
    /// <summary>
    /// 算子采样的「工具位」占位：算子健康采样没有工具维度（<see cref="OperatorHealthSample"/> 不含
    /// tool id / arguments），用一个显式具名常量占位，使每个 <c>(算子, 场景)</c> 恰好一个计数桶。
    /// <b>不用</b>空串：空串会让不同维度挤进同一桶，也会让 <c>classifier_status</c> 的读数难以解释。
    /// </summary>
    public const string OperatorSampleToolId = "operator";

    private readonly ClassifierHealthReporter _health;
    private readonly ILogger? _logger;

    private int _swallowedFailures;
    private string? _lastSwallowedFailure;

    /// <summary>构造适配器。</summary>
    /// <param name="health">既有健康面（进程内单例；本类不改它一行）。</param>
    /// <param name="logger">日志；null 表示不记日志（此时仍可通过 <see cref="SwallowedFailureCount"/> 探查）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="health"/> 为 null。</exception>
    public OperatorHealthObserverAdapter(ClassifierHealthReporter health, ILogger? logger = null)
    {
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _logger = logger;
    }

    /// <summary>被吞掉的内部失败次数（只读；「吞掉但可探查」的机器可读面）。</summary>
    public int SwallowedFailureCount => Volatile.Read(ref _swallowedFailures);

    /// <summary>最近一次被吞掉的内部失败摘要（<c>异常类型: 消息</c>）；从未失败为 null。</summary>
    public string? LastSwallowedFailure => _lastSwallowedFailure;

    /// <inheritdoc />
    public void Report(OperatorHealthSample sample)
    {
        if (sample is null)
        {
            return;
        }

        try
        {
            if (sample.Succeeded)
            {
                _health.RecordSuccess(sample.OperatorId, sample.LatencyMs);
                return;
            }

            _health.RecordDeferred(
                classifierId: sample.OperatorId,
                toolId: OperatorSampleToolId,
                argumentsJson: null,
                reasonCode: sample.ReasonCode,
                latencyMs: sample.LatencyMs,
                sceneKey: sample.SceneKey);
        }
        catch (Exception ex)
        {
            // 吞掉：接缝契约要求实现抛异常不得影响裁决（权威兜底在基类，见类注释）。
            Swallow(ex, sample);
        }
    }

    private void Swallow(Exception ex, OperatorHealthSample sample)
    {
        Interlocked.Increment(ref _swallowedFailures);
        _lastSwallowedFailure = $"{ex.GetType().Name}: {ex.Message}";

        _logger?.LogWarning(
            ex,
            "算子健康旁挂适配器内部失败（已吞掉，不影响裁决）：operator={OperatorId} scene={SceneKey}",
            sample.OperatorId,
            sample.SceneKey);
    }
}
