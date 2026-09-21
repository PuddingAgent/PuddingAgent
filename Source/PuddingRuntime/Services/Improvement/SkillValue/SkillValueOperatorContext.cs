using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PuddingCode.Operators;

namespace PuddingRuntime.Services.Improvement.SkillValue;

/// <summary>
/// 技能价值打分的场景输入：只承载<b>真实存在</b>的使用事实。
/// <para>
/// 刻意<b>不</b>包含的字段（以及为什么）：
/// <list type="bullet">
/// <item><b>回合结局</b>：遥测只记录命中 / 注入 / 读失败，当前没有任何 turn outcome 事实。
/// 它的来源是 canonical 对话事件，而既有数据访问层只暴露成功命令（见
/// <c>Docs/Features/S3-轨迹源-侦察结论-2026-09-21.md</c>）—— 因此它进入打分输入的时点是 S3，不是本片。</item>
/// <item><b>重叠度</b>：关键词共享度是 G5「定词」的产物，尚未产出。</item>
/// </list>
/// 这些字段<b>不得</b>以 0 / false / 空集合「预先占位」：任何公式里一个默认 0 都会静默变成
/// 「最差情况」并被当作真值使用。不放进类型 ⇒ 编译期就不可用，这是唯一可靠的防误用手段。
/// </para>
/// </summary>
public sealed record SkillValueOperatorContext : IOperatorContext
{
    /// <summary>被评分的技能 id。</summary>
    public required string SkillId { get; init; }

    /// <summary>观测窗口内 <c>Outcome=Injected</c> 的记录数。</summary>
    public required int InjectionCount { get; init; }

    /// <summary>观测窗口内 <c>Outcome=ReadFailed</c> 的记录数。</summary>
    public required int ReadFailureCount { get; init; }

    /// <summary>命中关键词去重数（仅诊断用，不参与分数）。</summary>
    public int DistinctKeywordCount { get; init; }

    /// <summary>去重后的活跃 agent 数（仅诊断用，不参与分数）。</summary>
    public int DistinctAgentCount { get; init; }

    /// <summary>注入记录的内容字节数之和。</summary>
    public long InjectedBytesTotal { get; init; }

    /// <summary>观测窗口起点（UTC）；无数据为 null。</summary>
    public DateTimeOffset? WindowStartUtc { get; init; }

    /// <summary>观测窗口终点（UTC）；无数据为 null。</summary>
    public DateTimeOffset? WindowEndUtc { get; init; }

    /// <summary>观测次数（注入 + 读失败）。0 表示<b>无数据</b>，不等于「没被使用」。</summary>
    public int ObservationCount => InjectionCount + ReadFailureCount;

    /// <inheritdoc />
    public string SceneKey => SkillValueScene.SceneKey;

    /// <summary>
    /// 确定性输入指纹（用于缓存键、审计去重与复现）。
    /// 只用事实字段构造，时间一律按 UTC 的往返格式（<c>O</c>）参与，避免本机时区或区域设置
    /// 影响指纹 —— 指纹漂移会让缓存与审计去重静默失效。
    /// </summary>
    public string InputDigest
    {
        get
        {
            var material = string.Join(
                '|',
                SkillId,
                InjectionCount.ToString(CultureInfo.InvariantCulture),
                ReadFailureCount.ToString(CultureInfo.InvariantCulture),
                DistinctKeywordCount.ToString(CultureInfo.InvariantCulture),
                DistinctAgentCount.ToString(CultureInfo.InvariantCulture),
                InjectedBytesTotal.ToString(CultureInfo.InvariantCulture),
                WindowStartUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                WindowEndUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
            return "svc-" + Convert.ToHexString(hash)[..32].ToLowerInvariant();
        }
    }
}
