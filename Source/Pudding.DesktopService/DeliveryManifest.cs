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
        int knownUnverifiedTotal,
        IReadOnlyList<string> requiredExternalSteps,
        int changelogFilesScanned = 0,
        int changelogFilesTotal = 0,
        DesktopAcceptance.Stage stage = DesktopAcceptance.Stage.ReadyForExternalDeploy)
    {
        BuildId = buildId;
        Commit = commit;
        Artifacts = artifacts;
        Checks = checks;
        KnownUnverified = knownUnverified;
        KnownUnverifiedTotal = knownUnverifiedTotal;
        RequiredExternalSteps = requiredExternalSteps;
        ChangelogFilesScanned = changelogFilesScanned;
        ChangelogFilesTotal = changelogFilesTotal;
        Stage = stage;
    }

    /// <summary>
    /// **从日志提取到的未验证项总数**（可能大于 <see cref="KnownUnverified"/> 的条数：
    /// 清单展示有上限）。必须显式声明：只报"20 条"而不说"共 43 条、已截断"就是报告假事实。
    /// </summary>
    public int KnownUnverifiedTotal { get; }

    /// <summary>展示的未验证项是否少于实际找到的（由总数与列表长度推出）。</summary>
    public bool KnownUnverifiedIsTruncated => KnownUnverifiedTotal > (KnownUnverified?.Count ?? 0);

    /// <summary>实际扫描的日志文件数（提取未验证项的语料规模）。</summary>
    public int ChangelogFilesScanned { get; }

    /// <summary>日志目录里的 markdown 文件总数（&gt; <see cref="ChangelogFilesScanned"/> 即语料被截断）。</summary>
    public int ChangelogFilesTotal { get; }

    /// <summary>提取未验证项时的**语料**是否被截断（与"条目被截断"是两件事，必须分开报告）。</summary>
    public bool ChangelogCorpusIsTruncated => ChangelogFilesTotal > ChangelogFilesScanned;

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

        if (manifest.ChangelogFilesScanned < 0 || manifest.ChangelogFilesTotal < manifest.ChangelogFilesScanned)
        {
            problems.Add(
                "changelogFilesTotal must be >= changelogFilesScanned (both must be non-negative)"
                + $" (scanned {manifest.ChangelogFilesScanned}, total {manifest.ChangelogFilesTotal})");
        }

        // 语料被截断 ⇒ 未验证项的"总数"也只是**已扫描部分**的数字，绝不能当成全集。
        if (manifest.ChangelogCorpusIsTruncated)
        {
            problems.Add(
                $"changelog corpus was truncated: scanned {manifest.ChangelogFilesScanned}"
                + $" of {manifest.ChangelogFilesTotal} files, so knownUnverifiedTotal is a lower bound");
        }

        // 总数是"实际找到多少条"的声明：比列出的条数还小就是自相矛盾（必有一个字段在撒谎）。
        if (manifest.KnownUnverifiedTotal < (manifest.KnownUnverified?.Count ?? 0))
        {
            problems.Add(
                "knownUnverifiedTotal must be >= the number of listed items"
                + $" (total {manifest.KnownUnverifiedTotal}, listed {manifest.KnownUnverified?.Count ?? 0})");
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

    /// <summary>
    /// 反序列化清单（外部一侧读取内部交付的 JSON）。缺字段一律失败、**不补默认值**：
    /// 一份读不全的清单不能用来判断"到底交付了什么"。
    /// </summary>
    public static bool TryDeserialize(
        string? json,
        out DeliveryManifest? manifest,
        out IReadOnlyList<string> problems)
    {
        manifest = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            problems = ["the manifest is empty"];
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            problems = [$"the manifest is not valid JSON: {exception.Message}"];
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            var collected = new List<string>();

            var buildId = ReadString(root, "buildId");
            var commit = ReadString(root, "commit");
            var artifacts = ReadStringArray(root, "artifacts");
            var knownUnverified = ReadStringArray(root, "knownUnverified");
        var knownUnverifiedTotal = ReadInt32(root, "knownUnverifiedTotal");
        var changelogFilesScanned = ReadInt32(root, "changelogFilesScanned");
        var changelogFilesTotal = ReadInt32(root, "changelogFilesTotal");
            var steps = ReadStringArray(root, "requiredExternalSteps");

            if (string.IsNullOrWhiteSpace(buildId))
            {
                collected.Add("manifest is missing buildId");
            }

            if (string.IsNullOrWhiteSpace(commit))
            {
                collected.Add("manifest is missing commit");
            }

            var checks = new List<DeliveryCheck>();
            if (!root.TryGetProperty("checks", out var checksElement) || checksElement.ValueKind != JsonValueKind.Array)
            {
                collected.Add("manifest is missing checks");
            }
            else
            {
                foreach (var element in checksElement.EnumerateArray())
                {
                    var name = ReadString(element, "name");
                    if (string.IsNullOrWhiteSpace(name)
                        || !TryReadInt(element, "passed", out var passed)
                        || !TryReadInt(element, "failed", out var failed))
                    {
                        collected.Add("manifest contains a check without name/passed/failed");
                        break;
                    }

                    checks.Add(new DeliveryCheck(name, passed, failed));
                }
            }

            if (collected.Count > 0)
            {
                problems = collected;
                return false;
            }

            manifest = new DeliveryManifest(
                buildId!,
                commit!,
                artifacts ?? [],
                checks,
                knownUnverified ?? [],
            knownUnverifiedTotal,
                steps is { Count: > 0 } ? steps : DeliveryManifest.RequiredLifecycleSteps);

            problems = [];
            return true;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>读一个整数（用于 <c>knownUnverifiedTotal</c> 这类**必需**的计数）。</summary>
    private static int ReadInt32(JsonElement element, string name)
    {
        if (!TryReadInt(element, name, out var value))
        {
            throw new InvalidOperationException(
                $"delivery manifest is missing required integer '{name}'"
                + " (state 0 explicitly when there is nothing to count)");
        }

        return value;
    }

    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static IReadOnlyList<string>? ReadStringArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .ToArray()
            : null;
}
