using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 外部观察事实的读取（外部一方交回来的 JSON）：逐字段 fail closed ——
/// 拼错字段名、结果写法不认识、证据缺失，都必须**如实报错**，而不是被默认值悄悄当成"通过"。
/// </summary>
public sealed class ExternalAcceptanceFactsTests
{
    private static DeliveryManifest Manifest() => new(
        "build-2026-10-09",
        "3e89490",
        ["PuddingDesktop.exe"],
        [new DeliveryCheck("全部套件", 2_000, 0)],
        [],
        0,
        DeliveryManifest.RequiredLifecycleSteps);

    private static string FullJson(string outcome = "passed", string? evidence = "已按清单观察") =>
        $$"""
        {
          "observedBuildId": "build-2026-10-09",
          "smoke": [
        {{string.Join(",\n", BrowserSmokePlan.Checks.Select(check =>
            $$"""    { "checkId": "{{check.Id}}", "outcome": "{{outcome}}", "evidence": {{(evidence is null ? "null" : $"\"{evidence}\"")}} }"""))}}
          ],
          "lifecycle": [
        {{string.Join(",\n", DeliveryManifest.RequiredLifecycleSteps.Select(step =>
            $$"""    { "step": "{{step}}", "passed": true, "evidence": "外部控制器记录了 {{step}}" }"""))}}
          ]
        }
        """;

    [Fact]
    public void ACompleteFactsFileIsParsedIntoAConcludableInput()
    {
        Assert.True(
            ExternalAcceptanceFacts.TryParse(FullJson(), Manifest(), out var input, out var problems),
            string.Join("; ", problems));

        Assert.NotNull(input);
        Assert.Equal("build-2026-10-09", input!.ObservedBuildId);
        Assert.Equal(BrowserSmokePlan.Checks.Count, input.Observations.Count);
        Assert.Equal(DeliveryManifest.RequiredLifecycleSteps.Count, input.Lifecycle.Count);

        // 端到端：读出来的事实直接喂给判定组件 ⇒ 可以下结论。
        Assert.True(ExternalAcceptanceSession.TryConclude(input, out var verdictProblems), string.Join("; ", verdictProblems));
    }

    [Fact]
    public void UnknownOutcomeWordsAreRefusedInsteadOfTreatedAsPassed()
    {
        // "ok"/"pass"/"done" 这类写法必须拒绝 —— 宽容地把它们当通过是最容易出错的。
        foreach (var word in new[] { "ok", "pass", "done", "success" })
        {
            Assert.False(ExternalAcceptanceFacts.TryParse(FullJson(outcome: word), Manifest(), out _, out var problems));
            Assert.Contains(problems, problem => problem.Contains("allowed values", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void MissingFieldsAreNamedInsteadOfSilentlyDefaulted()
    {
        const string missingBuild = """{ "smoke": [], "lifecycle": [] }""";
        Assert.False(ExternalAcceptanceFacts.TryParse(missingBuild, Manifest(), out _, out var buildProblems));
        Assert.Contains(buildProblems, problem => problem.Contains("observedBuildId", StringComparison.Ordinal));

        const string noSmoke = """{ "observedBuildId": "build-2026-10-09", "lifecycle": [] }""";
        Assert.False(ExternalAcceptanceFacts.TryParse(noSmoke, Manifest(), out _, out var smokeProblems));
        Assert.Contains(smokeProblems, problem => problem.Contains("smoke must be an array", StringComparison.Ordinal));

        const string noLifecycle = """{ "observedBuildId": "build-2026-10-09", "smoke": [] }""";
        Assert.False(ExternalAcceptanceFacts.TryParse(noLifecycle, Manifest(), out _, out var lifecycleProblems));
        Assert.Contains(
            lifecycleProblems, problem => problem.Contains("lifecycle must be an array", StringComparison.Ordinal));

        const string noCheckId = """{ "observedBuildId": "b", "smoke": [ { "outcome": "passed" } ], "lifecycle": [] }""";
        Assert.False(ExternalAcceptanceFacts.TryParse(noCheckId, Manifest(), out _, out var idProblems));
        Assert.Contains(idProblems, problem => problem.Contains("no checkId", StringComparison.Ordinal));

        const string noPassed = """
            { "observedBuildId": "b", "smoke": [], "lifecycle": [ { "step": "startup", "evidence": "x" } ] }
            """;
        Assert.False(ExternalAcceptanceFacts.TryParse(noPassed, Manifest(), out _, out var passedProblems));
        Assert.Contains(passedProblems, problem => problem.Contains("passed = true|false", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidOrEmptyDocumentsAreRefused()
    {
        Assert.False(ExternalAcceptanceFacts.TryParse(null, Manifest(), out _, out var empty));
        Assert.Contains(empty, problem => problem.Contains("empty", StringComparison.Ordinal));

        Assert.False(ExternalAcceptanceFacts.TryParse("{ not json", Manifest(), out _, out var invalid));
        Assert.Contains(invalid, problem => problem.Contains("not valid JSON", StringComparison.Ordinal));

        Assert.Throws<ArgumentNullException>(
            () => ExternalAcceptanceFacts.TryParse("{}", null!, out _, out _));
    }

    [Fact]
    public void FactsFromAnotherBuildStillFailTheVerdict()
    {
        // 读取层只管形状；"构建不一致"由判定层拒绝（两层职责分开）。
        var json = FullJson().Replace("build-2026-10-09", "build-2026-10-01", StringComparison.Ordinal);
        Assert.True(ExternalAcceptanceFacts.TryParse(json, Manifest(), out var input, out _));

        Assert.False(ExternalAcceptanceSession.TryConclude(input!, out var problems));
        Assert.Contains(problems, problem => problem.Contains("build-2026-10-01", StringComparison.Ordinal));
    }
}
