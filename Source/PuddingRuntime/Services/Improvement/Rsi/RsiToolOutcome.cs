namespace PuddingRuntime.Services.Improvement.Rsi;

/// <summary>
/// RSI 工具调用结局的三态判定（规格 §1.2 冻结规则，实现不得另发明）。
/// </summary>
/// <remarks>
/// <b>Unknown 必须为 0（枚举缺省值）</b>：把 Unknown 放在 0，意味着任何未初始化、
/// 反序列化失败或字段缺失的结局都落在「未知」而不是「成功」——
/// 在类型层面杜绝「exitCode 缺失时把成功当默认值」这一最危险的偷懒
/// （规格 §1.2 / §3-5：不得用 0 / false / 空串冒充缺失，Unknown 是一等公民）。
/// </remarks>
public enum RsiToolOutcome
{
    /// <summary>结局不可判定：payload 缺失/无法解析，或 exitCode 与 error 均缺失（如工具本身无 exitCode 语义）。不得当作成功或失败。</summary>
    Unknown = 0,

    /// <summary>成功：exitCode == 0 且 error 为空。</summary>
    Completed = 1,

    /// <summary>失败：exitCode 非 0 或 error 非空。</summary>
    Failed = 2,
}
