using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pudding.DesktopService;

/// <summary>
/// **交付清单**（两段式验收的交付物）：内部 Agent 交给外部控制器的**机器可读**事实。
///
/// 它解决的问题是"口头交接"：没有清单时，外部控制器无法知道
/// ① 交付的是**哪个构建/提交**；② 内部跑了哪些检查、有没有失败；
/// ③ **哪些结论内部证明不了**（真机/CDP/生命周期）；④ 外部必须做哪些步骤才能给出终态结论。
///
/// 因此 <see cref="KnownUnverified"/> 是**必需字段**（可以为空列表 = "确实没有未验证项"，
/// 但**不能为 null** = "没想过这件事"）。这条区分是刻意的，也是最容易被跳过的一步。
/// </summary>
public sealed record DeliveryManifest
{
    /// <summary>生命周期终态必须由外部控制器执行的四个步骤（缺少任何一项的清单不合格）。</summary>
    public static readonly IReadOnlyList<string> RequiredLifecycleSteps =
        ["startup", "restart", "crash-recovery", "exit-reclamation"];

    public DeliveryManifest(
        string buildId,
        string commit,
        IReadOnlyList<string> artifacts,
        IReadOnlyList<DeliveryCheck> checks,
        IReadOnlyList<string>? knownUnverified,
        IReadOnlyList<string> requiredExternalSteps,
        DesktopAcceptance.Stage stage = DesktopAcceptance.Stage.ReadyForExternalDeploy)
    {
        BuildId = buildId;
        Commit = commit;
        Artifacts = artifacts;
        Checks = checks;
        KnownUnverified = knownUnverified;
        RequiredExternalSteps = requiredExternalSteps;
        Stage = stage;
    }

    /// <summary>构建标识（产物可追溯；空值由校验拒绝）。</summary>
    public string BuildId { get; }

    /// <summary>提交（短哈希即可；空值由校验拒绝）。</summary>
    public string Commit { get; }

    /// <summary>交付产物（相对发布目录或绝对路径均可）。</summary>
    public IReadOnlyList<string> Artifacts { get; }

    /// <summary>内部跑过的检查及结果（**失败数必须为 0** 才算可交付）。</summary>
    public IReadOnlyList<DeliveryCheck> Checks { get; }

    /// <summary>
    /// **内部证明不了的结论**（真机/CDP/生命周期/环境相关）。
    /// <c>null</c> = 没写（不合格）；空列表 = 确实没有（合格）。
    /// </summary>
    public IReadOnlyList<string>? KnownUnverified { get; }

    /// <summary>外部控制器必须执行的步骤（必须是 <see cref="RequiredLifecycleSteps"/> 的超集）。</summary>
    public IReadOnlyList<string> RequiredExternalSteps { get; }

    /// <summary>本清单对应的阶段（清单只能声明 <c>ready-for-external-deploy</c>）。</summary>
    [JsonIgnore]
    public DesktopAcceptance.Stage Stage { get; }

    /// <summary>
    /// 序列化出来的阶段名（**用交付用语**，例如 <c>ready-for-external-deploy</c>）：
    /// 外部控制器读的是这套词，不该去猜枚举名。
    /// </summary>
    [JsonPropertyName("stage")]
    public string StageName => DesktopAcceptance.NameOf(Stage);
}

/// <summary>一条检查的结果。</summary>
public sealed record DeliveryCheck(string Name, int Passed, int Failed);

/// <summary>
/// 交付清单的**校验**：不合格就不该被交出去（这里逐条给出具体原因，而不是让人猜）。
/// </summary>
public static class DeliveryManifestValidator
{
    /// <summary>校验；返回所有问题（空 = 合格）。</summary>
    public static IReadOnlyList<string> Validate(DeliveryManifest? manifest)
    {
        if (manifest is null)
        {
            return ["a delivery manifest is required"];
        }

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(manifest.BuildId))
        {
            problems.Add("buildId is required (the external controller must know which build it is deploying)");
        }

        if (string.IsNullOrWhiteSpace(manifest.Commit))
        {
            problems.Add("commit is required (a build nobody can trace back is not deliverable)");
        }

        if (manifest.Artifacts is not { Count: > 0 })
        {
            problems.Add("at least one artifact is required");
        }
        else if (manifest.Artifacts.Any(string.IsNullOrWhiteSpace))
        {
            problems.Add("artifact paths must not be blank");
        }

        if (manifest.Checks is not { Count: > 0 })
        {
            problems.Add("at least one recorded check is required");
        }
        else
        {
            if (manifest.Checks.Any(check => string.IsNullOrWhiteSpace(check.Name)))
            {
                problems.Add("check names must not be blank");
            }

            var failed = manifest.Checks.Sum(check => Math.Max(0, check.Failed));
            if (failed > 0)
            {
                // 有失败项就不是"可交付"，无论别的检查多绿。
                problems.Add($"a deliverable manifest cannot contain failing checks (failed: {failed})");
            }

            if (manifest.Checks.Any(check => check.Passed < 0 || check.Failed < 0))
            {
                problems.Add("check counts must not be negative");
            }

            if (manifest.Checks.All(check => check.Passed == 0 && check.Failed == 0))
            {
                problems.Add("at least one check must have actually run (all counts are zero)");
            }
        }

        if (manifest.KnownUnverified is null)
        {
            // 刻意区分 null 与空列表：空 = "确实没有"，null = "没想过"。
            problems.Add("knownUnverified must be stated explicitly (use an empty list when there is none)");
        }
        else if (manifest.KnownUnverified.Any(string.IsNullOrWhiteSpace))
        {
            problems.Add("knownUnverified entries must not be blank");
        }

        if (manifest.RequiredExternalSteps is not { Count: > 0 })
        {
            problems.Add("requiredExternalSteps is required");
        }
        else
        {
            var missing = DeliveryManifest.RequiredLifecycleSteps
                .Where(step => !manifest.RequiredExternalSteps.Contains(step, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            if (missing.Length > 0)
            {
                // 生命周期终态只能由外部判定 ⇒ 清单必须把这几步明确交给外部。
                problems.Add(
                    "requiredExternalSteps must cover the lifecycle verdict steps: " + string.Join(", ", missing));
            }
        }

        if (manifest.Stage != DesktopAcceptance.Stage.ReadyForExternalDeploy)
        {
            problems.Add(
                "an internally produced manifest can only declare stage 'ready-for-external-deploy'"
                + $" (got '{DesktopAcceptance.NameOf(manifest.Stage)}')");
        }

        return problems;
    }

    /// <summary>
    /// 序列化成稳定的 JSON（外部控制器直接读）。
    /// 用宽松转义：清单里的中文（未验证项说明）应当**人可读**，而不是一堆 \uXXXX。
    /// </summary>
    public static string Serialize(DeliveryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, Options);
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
