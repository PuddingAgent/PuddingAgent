namespace PuddingComputer.Benchmark;

/// <summary>
/// 一个基准任务：给定什么应用、什么目标、多大风险、最多试几次、怎么判成功。
/// <para>
/// <see cref="Setup"/> 必须是**可复现的**（例如 <c>launch-clean</c>、<c>open-file:foo.txt</c>）。
/// 不可复现的初始状态会让基准结果无法比较，那比没有基准更糟 ——
/// 它会给出一个看起来客观的数字。
/// </para>
/// </summary>
/// <param name="Id">稳定标识，报告里用它对齐。</param>
/// <param name="App">目标应用标识，例如 <c>calculator</c>。</param>
/// <param name="Setup">可复现的初始状态。</param>
/// <param name="Goal">自然语言目标，例如 <c>Compute 17 + 25</c>。</param>
/// <param name="Risk">动作风险等级。</param>
/// <param name="MaxAttempts">每步最多尝试次数，必须 ≥1。</param>
/// <param name="Oracle">结果判定方式。</param>
public sealed record BenchmarkTask(
    string Id,
    string App,
    string Setup,
    string Goal,
    RiskClass Risk,
    int MaxAttempts,
    OutcomeOracle Oracle)
{
    /// <summary>字段完整性自检。返回问题列表，空表示通过。</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Id)) problems.Add("Id 不能为空");
        if (string.IsNullOrWhiteSpace(App)) problems.Add("App 不能为空");
        if (string.IsNullOrWhiteSpace(Setup)) problems.Add("Setup 不能为空");
        if (string.IsNullOrWhiteSpace(Goal)) problems.Add("Goal 不能为空");
        if (Oracle is null) problems.Add("Oracle 不能为空");
        if (MaxAttempts < 1) problems.Add($"MaxAttempts 必须 ≥1，实际 {MaxAttempts}");

        return problems;
    }
}
