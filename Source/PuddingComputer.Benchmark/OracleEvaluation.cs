namespace PuddingComputer.Benchmark;

/// <summary>
/// oracle 的判定结果。**三态而不是二态** —— 这是刻意的。
/// <para>
/// 把"没观察到"强行归成 <see cref="Fail"/> 会制造两种错误：
/// ① 明明成功却报失败，导致无谓重试（对非幂等动作是事故）；
/// ② 明明失败却因为"没证据"而被当成通过。
/// </para>
/// <para>
/// <see cref="Unknown"/> 的正确含义是"当前证据不足以判定"，它**既不允许声明成功**，
/// 也**不允许对非幂等动作重试**。
/// </para>
/// </summary>
public enum OracleVerdict
{
    /// <summary>已观察到任务预期的结果。只有它允许声明成功。</summary>
    Pass,

    /// <summary>已观察到与预期不符的结果。</summary>
    Fail,

    /// <summary>证据不足，无法判定。既不是成功也不是失败。</summary>
    Unknown,
}

/// <summary>
/// 一次 oracle 评估的产物：判定 + 可追溯的证据。
/// <para>
/// 证据不是可选的装饰。没有 <paramref name="Evidence"/> 的判定在事后无法复核，
/// 无法复核的"通过"等于没通过。
/// </para>
/// </summary>
/// <param name="Verdict">三态判定。</param>
/// <param name="Evidence">支持该判定的具体观察，例如实际读到的属性值、变化的像素比例。</param>
/// <param name="OracleDescription">被评估的 oracle 描述，便于报告里定位。</param>
public sealed record OracleEvaluation(
    OracleVerdict Verdict,
    string Evidence,
    string OracleDescription)
{
    /// <summary>构造一个通过结果。</summary>
    public static OracleEvaluation Pass(string evidence, string oracle) =>
        new(OracleVerdict.Pass, evidence, oracle);

    /// <summary>构造一个失败结果。</summary>
    public static OracleEvaluation Fail(string evidence, string oracle) =>
        new(OracleVerdict.Fail, evidence, oracle);

    /// <summary>构造一个无法判定结果。</summary>
    public static OracleEvaluation Unknown(string reason, string oracle) =>
        new(OracleVerdict.Unknown, reason, oracle);
}
