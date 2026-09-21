namespace PuddingCode.Operators;

/// <summary>
/// 算子公共面：只暴露稳定标识（用于缓存键、审计溯源与健康面聚合）。
/// </summary>
public interface IOperator
{
    /// <summary>算子稳定标识。</summary>
    string OperatorId { get; }
}

/// <summary>
/// 打分原语端口：只表达「多少分」，通过与否由阈值策略决定。
/// </summary>
/// <remarks>
/// 实现必须 fail-safe：<b>禁止向调用方冒泡异常</b>；不可用时按降级契约返回确定结论。
/// </remarks>
public interface IScorer : IOperator
{
    /// <summary>对一次输入打分。</summary>
    /// <param name="context">场景输入上下文。</param>
    /// <param name="ct">取消令牌；实现应在取消 / 超时时按降级契约返回结论，不得冒泡异常。</param>
    Task<ScoreResult> ScoreAsync(IOperatorContext context, CancellationToken ct = default);
}

/// <summary>
/// 判断原语端口：只表达三值结论（含 <see cref="JudgeOutcome.Abstain"/>）。
/// </summary>
/// <remarks>
/// 实现必须 fail-safe：禁止向调用方冒泡异常；不可用时按降级契约返回确定结论。
/// </remarks>
public interface IJudge : IOperator
{
    /// <summary>对一次输入做判断。</summary>
    /// <param name="context">场景输入上下文。</param>
    /// <param name="ct">取消令牌；实现应在取消 / 超时时按降级契约返回结论，不得冒泡异常。</param>
    Task<JudgeResult> JudgeAsync(IOperatorContext context, CancellationToken ct = default);
}

/// <summary>
/// 分类原语端口：只表达标签与分布，通过与否由调用方按阈值另行决定。
/// </summary>
/// <remarks>
/// 实现必须 fail-safe：禁止向调用方冒泡异常；不可用时按降级契约返回确定结论。
/// <para>
/// <b>禁止</b>一个类型同时实现两个原语端口——输出语义会再次混淆（
/// <see cref="ScoreResult"/> / <see cref="JudgeResult"/> / <see cref="ClassificationResult"/> 的存在意义就是强制分离）。
/// </para>
/// </remarks>
public interface IClassifier : IOperator
{
    /// <summary>对一次输入分类。</summary>
    /// <param name="context">场景输入上下文。</param>
    /// <param name="ct">取消令牌；实现应在取消 / 超时时按降级契约返回结论，不得冒泡异常。</param>
    Task<ClassificationResult> ClassifyAsync(IOperatorContext context, CancellationToken ct = default);
}

/// <summary>
/// 模型抽象（供应商隔离的<b>唯一</b>端口）：只有供应商适配器实现本接口。
/// <para>
/// 契约层不得出现任何供应商名词；供应商协议 / HTTP 细节全部留在适配器内部。
/// 实现必须 fail-safe：禁止向调用方冒泡异常（取消除外，取消按降级契约处理）。
/// </para>
/// </summary>
public interface IClassifierModel
{
    /// <summary>模型稳定标识（写入信封 <c>ModelId</c>，用于复现）。</summary>
    string ModelId { get; }

    /// <summary>请求一次模型判断。</summary>
    /// <param name="request">请求（含指令、版本、问题集、已投影的结构化输入）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<ModelJudgement> JudgeAsync(ModelJudgementRequest request, CancellationToken ct = default);
}

/// <summary>
/// 阈值策略来源（端口）：阈值必须外置，场景算子不得硬编码常量。
/// </summary>
public interface IThresholdPolicyProvider
{
    /// <summary>解析指定场景的阈值策略；无匹配为 null（调用方按无阈值语义处理）。</summary>
    /// <param name="sceneKey">场景键。</param>
    ThresholdPolicy? Resolve(string sceneKey);
}
