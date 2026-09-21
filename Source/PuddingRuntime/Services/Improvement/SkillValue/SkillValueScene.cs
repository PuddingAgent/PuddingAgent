namespace PuddingRuntime.Services.Improvement.SkillValue;

/// <summary>
/// 技能价值打分场景的<b>唯一</b>常量来源。
/// <para>
/// 把场景键、刻度串与打分常量集中在一处，是为了让「刻度串给打分函数版本化」这条不变量
/// 可以被机械检查：<b>任何公式或常量改动都必须同时升刻度串版本</b>（<c>v1</c> → <c>v2</c>）。
/// 分数用于<b>跨技能比较</b>，悄悄改公式会让新旧分数不可比且无人察觉；把版本写进刻度串，
/// 使「不可比」变成调用方可以检测的事实。
/// </para>
/// <para>
/// <b>这里没有阈值</b>：通过 / 不通过由外置阈值策略决定（见 <see cref="PuddingCode.Operators.ThresholdPolicy"/>）。
/// <see cref="AdoptionSaturationN"/> 与 <see cref="CostCeilingBytes"/> 是<b>打分函数内部的
/// 饱和点与代价上限</b>，不是判据 —— 它们只影响排序，不决定任何「通过」。
/// </para>
/// </summary>
public static class SkillValueScene
{
    /// <summary>场景键（与阈值策略、审计、缓存同键）。</summary>
    public const string SceneKey = "skill.value";

    /// <summary>指令版本（变更指令文本或打分口径时必须递增）。</summary>
    public const int InstructionVersion = 1;

    /// <summary>指令文本（本算子问题集为空，指令仅用于复现与审计）。</summary>
    public const string InstructionText =
        "对一项技能的历史使用事实给出价值分。只输出分数，不输出通过与否。";

    /// <summary>有效打分刻度：v1 公式，量纲 0..1，越大表示越有「被用过且用得成」的证据。</summary>
    public const string ValueScale = "skill-value.v1/0..1";

    /// <summary>
    /// 无观测数据时的刻度：表示<b>无法打分</b>，而非低分。
    /// 与 <see cref="ValueScale"/> 必须是不同的串，否则调用方无法用「读刻度」的方式区分
    /// 「没有数据」与「数据表明很差」。
    /// </summary>
    public const string InsufficientDataScale = "skill-value.v1/insufficient-data";

    /// <summary>无观测数据时的稳定原因码。</summary>
    public const string InsufficientDataReasonCode = "skill_value_insufficient_data";

    /// <summary>达到该注入次数即视为「充分采用」（饱和点，<b>不是阈值</b>）。</summary>
    public const int AdoptionSaturationN = 5;

    /// <summary>平均注入正文达到该字节数即视为代价上限（影响分数，<b>不是阈值</b>）。</summary>
    public const int CostCeilingBytes = 4096;

    /// <summary>采用度权重。</summary>
    public const double AdoptionWeight = 0.5;

    /// <summary>可靠度权重。</summary>
    public const double ReliabilityWeight = 0.5;

    /// <summary>代价折扣的最大占比（costRatio = 1 时分数折半）。</summary>
    public const double MaxCostDiscount = 0.5;
}
