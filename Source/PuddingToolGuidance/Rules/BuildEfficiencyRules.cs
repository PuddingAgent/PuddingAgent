namespace PuddingToolGuidance.Rules;

/// <summary>
/// 构建/测试效率规则组（P0）。
/// <para>
/// 2026-10-31 夜间实测：一次没带 <c>--no-build</c> 的 <c>dotnet test</c> 把 20 分钟耗在构建上；
/// 一次测试挂死又耗掉 20 分钟才想到用挂起检测。两条都属于「命令发出的那一刻就能判定」，
/// 因此适合放在这里。
/// </para>
/// </summary>
public static class BuildEfficiencyRules
{
    public static IReadOnlyList<IGuidanceRule> All { get; } =
    [
        new DotnetTestWithoutNoBuildRule(),
        new RepeatedTimeoutRule()
    ];

    /// <summary>命令里是否含某个短语（不区分大小写，按空白归一）。</summary>
    private static bool Contains(string command, string phrase) =>
        command.Contains(phrase, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 重复 <c>dotnet test</c> 未带 <c>--no-build</c>。
    /// <para>
    /// 只在<b>第二次及以后</b>提示：第一次跑测试时工程本来就要构建，那一步跳不掉；
    /// 从第二次起（尤其是带 <c>--filter</c> 的定向重跑）构建几乎总是白花的。
    /// </para>
    /// </summary>
    private sealed class DotnetTestWithoutNoBuildRule : IGuidanceRule
    {
        public string Id => "build.dotnet_test_without_no_build";

        public GuidanceAdvice? Evaluate(GuidanceInput input)
        {
            if (string.IsNullOrWhiteSpace(input.Command))
                return null;

            if (!Contains(input.Command, "dotnet test"))
                return null;

            if (Contains(input.Command, "--no-build"))
                return null;

            if (input.RepeatedCommandCount < 2)
                return null;

            return new GuidanceAdvice(Id,
                "这是本会话里第 " + input.RepeatedCommandCount + " 次 `dotnet test` 且没带 `--no-build`：" +
                "工程已经构建过，加上 `--no-build` 可跳过构建（实测一次可省数分钟到二十分钟）。");
        }
    }

    /// <summary>
    /// 同一调用连续超时：提示改用挂起检测，而不是再等一次。
    /// <para>
    /// 判据是「错误文本像超时」+「连续失败 ≥ 2」：单次超时可能只是慢，连续两次就该换手段。
    /// 2026-11-01 实测：<c>--blame-hang --blame-hang-timeout 120s</c> 一次就点名了挂死的测试，
    /// 而人工二分跑了 20 分钟还没结论。
    /// </para>
    /// </summary>
    private sealed class RepeatedTimeoutRule : IGuidanceRule
    {
        private static readonly string[] TimeoutMarkers =
        [
            "timed out", "timeout", "超时", "has timed out"
        ];

        public string Id => "build.repeated_timeout_use_blame_hang";

        public GuidanceAdvice? Evaluate(GuidanceInput input)
        {
            if (input.Succeeded)
                return null;

            if (input.ConsecutiveFailureCount < 2)
                return null;

            var error = input.Error;
            if (string.IsNullOrWhiteSpace(error))
                return null;

            var looksLikeTimeout = false;
            foreach (var marker in TimeoutMarkers)
            {
                if (error.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    looksLikeTimeout = true;
                    break;
                }
            }

            if (!looksLikeTimeout)
                return null;

            return new GuidanceAdvice(Id,
                "连续 " + input.ConsecutiveFailureCount + " 次超时。不要再加长等待：" +
                "改用 `--blame-hang --blame-hang-timeout <秒>`，它会直接点名挂住的用例并留下进程转储。");
        }
    }
}
