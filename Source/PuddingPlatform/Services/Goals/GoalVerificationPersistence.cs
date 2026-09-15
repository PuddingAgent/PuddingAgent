using System.Text.Json;
using PuddingCode.Goals;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// ADR-092 §5.1/§6.2：验收合同与真实检查记录的持久读写适配。
/// <para>
/// 读取一律 fail-closed：JSON 非法、报告缺失、记录未 finished 都返回"没有可用证据"，
/// 绝不把缺失伪造成 passed；调用方据此走 acceptance_contract_missing / check_not_run
/// 的有界修复或等待，而不是判定完成。
/// </para>
/// </summary>
public static class GoalVerificationPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string BuildContractId(string goalRunId, int activationEpoch, int objectiveVersion)
        => $"gc-{goalRunId}-{activationEpoch}-{objectiveVersion}";

    public static string BuildCheckRecordId(
        string goalRunId,
        int activationEpoch,
        int iterationNo,
        string checkId)
        => $"gchk-{goalRunId}-{activationEpoch}-{iterationNo}-{checkId}";

    /// <summary>去重键的内层身份：同一 (scope, criterionRevision, definitionHash, inputFingerprint) 只执行一次。
    /// 注意：持久层不得直接使用它 —— 工作项去重必须再叠加 (goalRunId, activationEpoch) 作用域，见 BuildScopedDedupKey。</summary>
    public static string BuildDedupKey(
        string scope,
        int criterionRevision,
        string? definitionHash,
        string? inputFingerprint)
        => $"{scope}|{criterionRevision}|{definitionHash ?? "-"}|{inputFingerprint ?? "-"}";

    /// <summary>
    /// 检查工作项的持久去重键：在 <see cref="BuildDedupKey"/> 之上再叠加 (goalRunId, activationEpoch) 作用域。
    /// ADR-092 §6.2：epoch 变更（Pause/Resume 等）之后必须重新真实执行 —— 旧 epoch 的报告是旧进程的证据，
    /// 既不能当作当前 epoch 的证据，也不能因为"命中去重键"而让当前 epoch 连工作项都没有（fail-livelock）。
    /// </summary>
    public static string BuildScopedDedupKey(
        string goalRunId,
        int activationEpoch,
        string scope,
        int criterionRevision,
        string? definitionHash,
        string? inputFingerprint)
        => $"{goalRunId}|{activationEpoch}|{BuildDedupKey(scope, criterionRevision, definitionHash, inputFingerprint)}";

    public static string SerializeCriteria(IEnumerable<GoalCriterion> criteria)
        => JsonSerializer.Serialize(criteria, JsonOptions);

    public static string SerializeChecks(IEnumerable<GoalCheckSpec> checks)
        => JsonSerializer.Serialize(checks, JsonOptions);

    public static string SerializeReport(GoalCheckReport report)
        => JsonSerializer.Serialize(report, JsonOptions);

    /// <summary>合同缺失或 JSON 非法一律返回空集合（=> 空合同，走有界修复，不得 vacuous pass）。</summary>
    public static IReadOnlyList<GoalCriterion> ReadCriteria(string? json)
        => DeserializeList<GoalCriterion>(json);

    public static IReadOnlyList<GoalCheckSpec> ReadChecks(string? json)
        => DeserializeList<GoalCheckSpec>(json);

    /// <summary>
    /// 只有 status == finished 且带 ReportJson 的记录才构成可信检查报告；
    /// pending/leased/租约过期以及报告缺失一律跳过（由证据策略判定为未运行）。
    /// </summary>
    public static IReadOnlyList<GoalCheckReport> ReadReports(
        IEnumerable<GoalCheckRecordEntity>? records)
    {
        if (records is null)
            return [];

        var reports = new List<GoalCheckReport>();
        foreach (var record in records)
        {
            if (!string.Equals(
                    record.Status,
                    GoalCheckRecordStatuses.Finished,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(record.ReportJson))
                continue;

            try
            {
                var report = JsonSerializer.Deserialize<GoalCheckReport>(record.ReportJson, JsonOptions);
                if (report is not null)
                    reports.Add(report);
            }
            catch (JsonException)
            {
                // fail-closed：非法报告视为没有报告，绝不当成通过。
            }
        }

        return reports;
    }

    private static IReadOnlyList<T> DeserializeList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
