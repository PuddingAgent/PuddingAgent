using PuddingCode.Goals;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// ADR-092 §5.3：版本化检查定义注册表。
/// <para>
/// 检查不接受模型自由文本的 shell 字符串：调用方只能引用已登记的 DefinitionRef，
/// 由注册表把 <see cref="GoalCheckSpec.InputRefs"/> 解析成规范化命令。
/// 未登记的定义直报 UnsupportedCheckKind，绝不放行任意命令。
/// </para>
/// </summary>
public static class GoalCheckDefinitionRegistry
{
    public sealed record GoalCheckDefinition(
        string DefinitionRef,
        string Kind,
        string CommandTemplate,
        bool RequiresTestEvidence);

    private const string BuildRef = "checks/build.md#dotnet-build";
    private const string TestRef = "checks/test.md#dotnet-test";

    private static readonly Dictionary<string, GoalCheckDefinition> Definitions =
        new(StringComparer.Ordinal)
        {
            [BuildRef] = new(
                BuildRef,
                GoalVerificationSpecKinds.Build,
                "dotnet build {0} --no-restore --nologo",
                RequiresTestEvidence: false),
            [TestRef] = new(
                TestRef,
                GoalVerificationSpecKinds.Test,
                "dotnet test {0} --no-restore --nologo",
                RequiresTestEvidence: true),
        };

    public static IReadOnlyCollection<string> RegisteredDefinitionRefs => Definitions.Keys;

    /// <summary>
    /// 版本化定义的内容 hash。定义内容（引用/kind/命令模板）变化 => hash 变化 => 旧报告失效。
    /// </summary>
    public static string ComputeDefinitionHash(GoalCheckDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var payload = $"{definition.DefinitionRef}|{definition.Kind}|{definition.CommandTemplate}";
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(payload));
        return $"sha256:{Convert.ToHexStringLower(bytes)}";
    }

    /// <summary>取定义 hash；未登记返回 false。</summary>
    public static bool TryGetDefinitionHash(string? definitionRef, out string definitionHash)
    {
        definitionHash = string.Empty;
        if (!TryResolve(definitionRef, out var definition))
            return false;
        definitionHash = ComputeDefinitionHash(definition);
        return true;
    }

    public static bool TryResolve(
        string? definitionRef,
        out GoalCheckDefinition definition)
    {
        definition = null!;
        if (string.IsNullOrWhiteSpace(definitionRef))
            return false;
        return Definitions.TryGetValue(definitionRef.Trim(), out definition!);
    }

    /// <summary>
    /// 把检查输入解析成命令。只接受单个、相对、无元字符的目标路径，
    /// 任何越界输入一律拒绝（由调用方转成 unsupported/failed 报告）。
    /// </summary>
    public static bool TryBuildCommand(
        GoalCheckSpec spec,
        out string command,
        out string failureCode)
    {
        command = string.Empty;
        failureCode = string.Empty;

        if (!TryResolve(spec.DefinitionRef, out var definition))
        {
            failureCode = GoalCheckFailureCodes.UnsupportedCheckKind;
            return false;
        }

        if (!string.Equals(definition.Kind, spec.Kind, StringComparison.Ordinal))
        {
            failureCode = GoalCheckFailureCodes.UnsupportedCheckKind;
            return false;
        }

        if (spec.InputRefs.Count != 1 || string.IsNullOrWhiteSpace(spec.InputRefs[0]))
        {
            failureCode = GoalCheckFailureCodes.InputFingerprintMissing;
            return false;
        }

        var target = spec.InputRefs[0].Trim();
        if (!IsSafeTarget(target))
        {
            failureCode = GoalCheckFailureCodes.UnsupportedCheckKind;
            return false;
        }

        command = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            definition.CommandTemplate,
            target);
        return true;
    }

    public static bool IsSafeTarget(string target)
    {
        if (target.Contains("..", StringComparison.Ordinal))
            return false;
        if (Path.IsPathRooted(target))
            return false;
        if (target.IndexOfAny([' ', '\t', '"', '\'', '&', '|', ';', '<', '>', '`', '$', '\n', '\r']) >= 0)
            return false;
        var extension = Path.GetExtension(target);
        return extension is ".csproj" or ".sln" or ".slnx";
    }
}

/// <summary>检查执行阶段的失败码（与证据策略的失败码保持同一词汇）。</summary>
public static class GoalCheckFailureCodes
{
    public const string UnsupportedCheckKind = "unsupported_check_kind";
    public const string CheckNotRun = "check_not_run";
    public const string NonZeroExitCode = "non_zero_exit_code";
    public const string TestCountUnknown = "test_count_unknown";
    public const string TestsFailed = "tests_failed";
    public const string InputFingerprintMissing = "input_fingerprint_missing";
    public const string CheckTimeout = "check_timeout";
    public const string CheckIdentityMismatch = "check_identity_mismatch";

    /// <summary>命令被终端准入拒绝（白名单 / 危险模式 / 宿主机安全不变量）——不得启动任何进程。</summary>
    public const string AdmissionDenied = "check_admission_denied";
    public const string EvidenceMissing = "evidence_missing";
    public const string NoTestEvidence = "no_test_evidence";
}
