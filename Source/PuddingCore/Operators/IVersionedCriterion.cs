namespace PuddingCode.Operators;

/// <summary>
/// 可版本化判据：一切阈值 / 判据都必须有稳定 id 与版本。
/// <para>
/// 为什么把它抬成<b>类型事实</b>而不是约定：判据一旦可被移动（配置覆盖、自我改进候选），
/// 「这条结论是用哪一版判据得出的」就必须能从结果本身回答；否则历史结论不可复现、不可审计，
/// 也无法回答「改进前后到底差在哪一版」。
/// </para>
/// <para>
/// 本接口只声明标识，不声明形状：三区间判据（<see cref="ThresholdPolicy"/>）与单侧验收门
/// （<see cref="AcceptanceThresholdPolicy"/>）语义不同，但都必须可版本化——共性在标识，不在判定。
/// </para>
/// </summary>
public interface IVersionedCriterion
{
    /// <summary>稳定判据 id（连同版本落库，供事后解释「为什么通过 / 为什么降级」）。</summary>
    string PolicyId { get; }

    /// <summary>判据版本。同一 id 的判据被移动过时必须递增，历史结果才能自证用的是哪一版。</summary>
    int Version { get; }
}
