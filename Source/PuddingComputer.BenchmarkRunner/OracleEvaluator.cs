using PuddingComputer.Benchmark;
using PuddingComputer.UIAutomation;

namespace PuddingComputer.BenchmarkRunner;

/// <summary>
/// 把「任务声明的 oracle」与「实际观察到的快照」对上，给出三态判定。
/// <para>
/// 这是整条链路里第一次真正产生 <see cref="OracleEvaluation"/> ——
/// 在此之前，任务定义和观察结果各自存在，没有任何东西能回答
/// "这个任务在当前界面上到底算不算完成"。
/// </para>
/// <para>
/// 纯函数：不碰窗口、不碰 UIA、不做 IO。所有不确定的输入都走 <c>Unknown</c>，
/// 绝不猜成 <c>Pass</c> —— 猜对一次会让整套判定失去可信度。
/// </para>
/// </summary>
public static class OracleEvaluator
{
    /// <summary>评估一个 oracle。</summary>
    public static OracleEvaluation Evaluate(OutcomeOracle oracle, UiaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(oracle);
        ArgumentNullException.ThrowIfNull(snapshot);

        return oracle switch
        {
            UiaPropertyOracle p => EvaluateProperty(p, snapshot),
            ElementStateOracle e => EvaluateState(e, snapshot),
            _ => OracleEvaluation.Unknown(
                $"该 oracle 类型尚未实现评估：{oracle.GetType().Name}",
                oracle.Description),
        };
    }

    private static OracleEvaluation EvaluateProperty(UiaPropertyOracle oracle, UiaSnapshot snapshot)
    {
        // 目前只支持 name 属性：UiaNode 只归一化了 Name，没有任意属性读取。
        // 其余属性返回 Unknown 而不是 Fail —— "读不到"和"读到了但不对"是两件事。
        if (!string.Equals(oracle.Property, "name", StringComparison.OrdinalIgnoreCase))
        {
            return OracleEvaluation.Unknown(
                $"属性 '{oracle.Property}' 尚未支持（当前只支持 name）",
                oracle.Description);
        }

        if (!snapshot.TryFindUnique(oracle.Selector, out var node, out var reason))
            return OracleEvaluation.Unknown(reason ?? "未定位到元素", oracle.Description);

        var actual = node!.Name ?? "";
        bool matched = Compare(actual, oracle.Operator, oracle.Expected);

        var evidence = $"实际 name=\"{actual}\" {oracle.Operator} \"{oracle.Expected}\" => {matched}";

        return matched
            ? OracleEvaluation.Pass(evidence, oracle.Description)
            : OracleEvaluation.Fail(evidence, oracle.Description);
    }

    private static OracleEvaluation EvaluateState(ElementStateOracle oracle, UiaSnapshot snapshot)
    {
        if (!snapshot.TryFindUnique(oracle.Selector, out var node, out var reason))
            return OracleEvaluation.Unknown(reason ?? "未定位到元素", oracle.Description);

        bool? actual = oracle.State.ToLowerInvariant() switch
        {
            "enabled" => node!.IsEnabled,
            "offscreen" => node!.IsOffscreen,
            "focused" => node!.HasKeyboardFocus,
            "actionable" => node!.IsActionable,
            _ => null,
        };

        if (actual is null)
        {
            return OracleEvaluation.Unknown(
                $"状态 '{oracle.State}' 尚未支持（支持：enabled/offscreen/focused/actionable）",
                oracle.Description);
        }

        var evidence = $"实际 {oracle.State}={actual.Value}，期望 {oracle.Expected}";

        return actual.Value == oracle.Expected
            ? OracleEvaluation.Pass(evidence, oracle.Description)
            : OracleEvaluation.Fail(evidence, oracle.Description);
    }

    /// <summary>
    /// 字符串比较。操作符大小写不敏感（清单是人写的，容易写错大小写），
    /// 但被比较的值大小写敏感（它是应用给的真实值）。
    /// </summary>
    private static bool Compare(string actual, string op, string expected) =>
        op.ToLowerInvariant() switch
        {
            "equals" => string.Equals(actual, expected, StringComparison.Ordinal),
            "contains" => actual.Contains(expected, StringComparison.Ordinal),
            "endswith" => actual.EndsWith(expected, StringComparison.Ordinal),
            "startswith" => actual.StartsWith(expected, StringComparison.Ordinal),
            _ => false,
        };
}
