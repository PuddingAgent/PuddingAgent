namespace PuddingCode.Operators;

/// <summary>
/// 置信度种类：区分「模型自报」与「已校准」，避免下游把自报值当成概率使用。
/// </summary>
/// <remarks>
/// 序列化兼容：<see cref="Unknown"/> 必须保持数值 0（字段缺省反序列化值）；
/// 新增成员只允许追加在枚举末尾，不得插入或重排。
/// </remarks>
public enum ConfidenceKind
{
    /// <summary>未提供置信度，或来源不明。</summary>
    Unknown = 0,

    /// <summary>模型自报置信度（<b>未经校准</b>，不得直接当概率与阈值比较）。</summary>
    ModelSelfReported = 1,

    /// <summary>已校准置信度（可与阈值直接比较）。</summary>
    Calibrated = 2,
}

/// <summary>
/// 处置分档：把「不确定就升级」变成类型可见的档位，而不是散落在调用方的 if。
/// </summary>
public enum JudgementTier
{
    /// <summary>未知 / 未分档。</summary>
    Unknown = 0,

    /// <summary>可自动处置。</summary>
    Auto = 1,

    /// <summary>需要一次确认。</summary>
    Confirm = 2,

    /// <summary>必须人工处置。</summary>
    Human = 3,
}

/// <summary>
/// 判断器三值结论。
/// <para>
/// <see cref="Abstain"/> 是<b>一等结果</b>（「确定地不知道」），禁止被任何投影折叠为
/// <see cref="Yes"/> 或 <see cref="No"/>；<see cref="Unknown"/> 表示未产生有效判断，
/// 调用方必须按降级契约处理。
/// </para>
/// </summary>
public enum JudgeOutcome
{
    /// <summary>降级 / 未产生有效判断（不得当作放行）。</summary>
    Unknown = 0,

    /// <summary>判定为「是」。</summary>
    Yes = 1,

    /// <summary>判定为「否」。</summary>
    No = 2,

    /// <summary>弃权：确定地无法判定（不是降级，也不是「否」）。</summary>
    Abstain = 3,
}
