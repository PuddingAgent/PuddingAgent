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
        bool RequiresTestEvidence,
        string? ExpectedText = null);

    private const string BuildRef = "checks/build.md#dotnet-build";
    private const string TestRef = "checks/test.md#dotnet-test";

    /// <summary>目标级文件证据定义：只读核验（存在且非空），不对应任何 shell 命令。</summary>
    public const string FileEvidenceRef = "checks/file-evidence.md#file-exists-nonempty";

    /// <summary>
    /// G92-1 S1-c（片 2）：纯文本断言定义。只读判定，不对应任何 shell 命令；
    /// 期望文本经 <see cref="GoalCheckSpec.ExpectedText"/> 供给并按方案 A 进入 hash 载荷。
    /// </summary>
    public const string TextAssertionRef = "checks/text-assertion.md#equals";

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
            [FileEvidenceRef] = new(
                FileEvidenceRef,
                GoalVerificationSpecKinds.FileEvidence,
                "只读核验：文件存在且非空（File.Exists + 长度；不启动任何进程）",
                RequiresTestEvidence: false),
            [TextAssertionRef] = new(
                TextAssertionRef,
                GoalVerificationSpecKinds.TextAssertion,
                string.Empty,
                RequiresTestEvidence: false),
        };

    public static IReadOnlyCollection<string> RegisteredDefinitionRefs => Definitions.Keys;

    /// <summary>
    /// 版本化定义的内容 hash。定义内容（引用/kind/命令模板）变化 => hash 变化 => 旧报告失效。
    /// </summary>
    public static string ComputeDefinitionHash(GoalCheckDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var payload = $"{definition.DefinitionRef}|{definition.Kind}|{definition.CommandTemplate}";

        // G92-1 S1-c 方案 A：期望文本参与载荷，但仅当非空（IsNullOrEmpty）时追加。
        // 禁改用 IsNullOrWhiteSpace：决策 D1 默认不 Trim，纯空白期望文本是合法载荷，
        // WhiteSpace 判断会把它当空丢弃（hash 不可复现）。null/空串不追加，旧条目 payload 字节不变。
        if (!string.IsNullOrEmpty(definition.ExpectedText))
            payload += $"|{definition.ExpectedText}";

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

        // 不变量：file-evidence / text-assertion 是只读/纯文本判定，永远不能被解析成 shell 命令；
        // 它们的评估必须走 GoalCheckRunner 的只读分支（G92-1 S1-c 片 2 并入 text-assertion）。
        if (string.Equals(definition.Kind, GoalVerificationSpecKinds.FileEvidence, StringComparison.Ordinal)
            || string.Equals(definition.Kind, GoalVerificationSpecKinds.TextAssertion, StringComparison.Ordinal))
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

    /// <summary>命令目标安全校验：相对、无元字符、无逃逸，且必须是项目文件（行为与历史版本一致）。</summary>
    public static bool IsSafeTarget(string target)
        => IsSafeRelativeTarget(target)
           && Path.GetExtension(target) is ".csproj" or ".sln" or ".slnx";

    /// <summary>
    /// 文件证据目标安全校验：与 <see cref="IsSafeTarget"/> 共享同一套相对性/元字符核心检查，
    /// 但不限制扩展名（文件证据可以是任意文档），并显式拒绝通配符与以目录分隔符结尾的目标。
    /// 绝对路径、盘符、UNC、<c>..</c> 逃逸与 shell 元字符一律拒绝。
    /// <para>
    /// 另加「必须像文件名」的收紧（2026-09-20，缺陷修复）：目标末段必须以 <c>.扩展名</c>（1..16 位 ASCII
    /// 字母数字）结尾，且不得包含 CJK/中文标点。否则 objective 里引用的散文锚点（例如 <c>§9.A07）。</c>）会被
    /// 误判为文件路径，派生出永远无法满足的 <c>objective-file-evidence</c> 条件，使目标轮轮 fail-closed 判负。
    /// </para>
    /// </summary>
    public static bool IsSafeEvidenceFilePath(string target)
        => IsSafeRelativeTarget(target)
           && target.IndexOfAny(['*', '?']) < 0
           && !target.EndsWith('/')
           && !target.EndsWith('\\')
           && LooksLikeFileName(target);

    /// <summary>
    /// 散文标点：出现在候选证据目标里即说明该 token 来自叙述文本（引用锚点/括号说明），不是文件路径。
    /// 只列全角/中文标点与节符号，不列 ASCII 括号等合法文件名字符（避免误伤真实路径）。
    /// </summary>
    private static readonly char[] ProsePunctuation =
    [
        '（', '）', '【', '】', '「', '」', '『', '』', '《', '》', '〈', '〉',
        '。', '．', '，', '、', '；', '：', '！', '？', '…', '—', '§', '·',
    ];

    /// <summary>
    /// 目标是否「像文件名」：不含散文标点，且末段（最后一个分隔符之后）存在非首位 <c>.</c>，
    /// 其后的扩展名为 1..16 位 ASCII 字母数字。既放行 <c>Docs/summary.md</c>、<c>notes.txt</c>，
    /// 也拒绝 <c>§9.A07）。</c>、<c>dir/</c> 这类非路径 token。
    /// </summary>
    private static bool LooksLikeFileName(string target)
    {
        if (target.IndexOfAny(ProsePunctuation) >= 0)
            return false;

        var lastSeparator = target.LastIndexOfAny(['/', '\\']);
        var lastSegment = lastSeparator >= 0 ? target[(lastSeparator + 1)..] : target;
        var lastDot = lastSegment.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == lastSegment.Length - 1)
            return false;

        var extension = lastSegment[(lastDot + 1)..];
        if (extension.Length is < 1 or > 16)
            return false;

        foreach (var ch in extension)
        {
            var isAsciiAlphanumeric = (ch >= '0' && ch <= '9')
                || (ch >= 'a' && ch <= 'z')
                || (ch >= 'A' && ch <= 'Z');
            if (!isAsciiAlphanumeric)
                return false;
        }

        return true;
    }

    /// <summary>共享核心：拒绝 <c>..</c> 逃逸、绝对/盘符/UNC 路径与 shell 元字符（含空白）。</summary>
    private static bool IsSafeRelativeTarget(string target)
    {
        if (target.Contains("..", StringComparison.Ordinal))
            return false;
        if (Path.IsPathRooted(target))
            return false;
        if (target.IndexOfAny([' ', '\t', '"', '\'', '&', '|', ';', '<', '>', '`', '$', '\n', '\r']) >= 0)
            return false;
        return true;
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

    /// <summary>进程已终态但退出码不可得（快照缺失 / kill 失败仍存活）：fail-closed 记失败。</summary>
    public const string ExitCodeUnknown = "exit_code_unknown";

    /// <summary>命令被终端准入拒绝（白名单 / 危险模式 / 宿主机安全不变量）——不得启动任何进程。</summary>
    public const string AdmissionDenied = "check_admission_denied";
    public const string EvidenceMissing = "evidence_missing";
    public const string NoTestEvidence = "no_test_evidence";

    /// <summary>文件证据缺失或为空（只读判定：File.Exists 失败，或长度为 0）——终态 failed，不是 pending。</summary>
    public const string FileEvidenceMissing = "file_evidence_missing";

    /// <summary>文件证据存在但无法读取（IO/权限错误）——终态 failed 且携带可读原因。</summary>
    public const string FileEvidenceUnreadable = "file_evidence_unreadable";
}
