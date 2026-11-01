namespace PuddingToolGuidance.Rules;

/// <summary>
/// 低效循环规则：同一调用**连续失败**到一定次数后，提示换观测手段而不是继续换参数重试。
/// <para>
/// 为什么这条值钱：前面那些规则治「一次性用错」，这条治「反复用错同一个错」。
/// 模型在局部最优里打转时，往往每轮都在微调参数，看起来在努力，实际零进展。
/// </para>
/// <para>
/// 阈值取 3：1 次是偶然，2 次是巧合，到 3 次就该停手换方法 —— 读实现、dump 对象、查文档，
/// 都比第 4 次重试便宜。
/// </para>
/// </summary>
public sealed class RepeatedFailureRule : IGuidanceRule
{
    /// <summary>触发阈值。</summary>
    public const int Threshold = 3;

    public string Id => "loop.repeated_failure";

    public GuidanceAdvice? Evaluate(GuidanceInput input)
    {
        if (input.ConsecutiveFailureCount < Threshold)
            return null;

        return new GuidanceAdvice(Id,
            $"同一调用已连续失败 {input.ConsecutiveFailureCount} 次。停止微调参数，改为换观测手段：" +
            "读实现/日志、打印对象、或用 --blame-hang 之类的专门工具。");
    }
}
