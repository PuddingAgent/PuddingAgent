using System.Text.RegularExpressions;

namespace PuddingCode.SubAgents;

/// <summary>
/// 子代理回包是否符合派发时声明的 <c>output_contract</c>。
/// <para>
/// 只做**观测**，不参与判定成功/失败：任务书允许覆盖默认格式（实测两个模型都会服从
/// “只回一行”这类指令），因此“没按五段式回”不等于执行失败。标注出来的意义是让父代理
/// 知道「这份回包能不能按结构解析」，而不是替它下终态结论。
/// </para>
/// </summary>
/// <param name="Applicable">合同为空或无有效键时为 false，此时不做任何判断。</param>
/// <param name="Satisfied">所有键都以「标签形式」出现在回包里。</param>
/// <param name="MissingKeys">未按标签出现的键，保持合同声明顺序。</param>
public sealed record SubAgentOutputContractCheck(
    bool Applicable,
    bool Satisfied,
    IReadOnlyList<string> MissingKeys);

/// <summary>
/// 判定回包是否含齐 <c>output_contract</c> 声明的键。
/// </summary>
/// <remarks>
/// 判定采用「标签位置」而非「文中出现」：键必须位于行首（允许前置空白、列表符号、
/// 序号、引用符或 JSON 引号）。这样 <c>SUMMARY: ...</c>、<c>- SUMMARY: ...</c>、
/// <c>**SUMMARY**</c>、<c>"SUMMARY": ...</c> 都算命中，而
/// “我忘了写 RISKS” 这类顺带提及不算——否则一份承认缺失的回包会被判成齐全。
/// </remarks>
public static class SubAgentOutputContractInspector
{
    /// <summary>spawn_sub_agent 在调用方未声明合同时使用的默认键集。</summary>
    public const string DefaultContract = "SUMMARY, CHANGES, EVIDENCE, RISKS, BLOCKERS";

    public static SubAgentOutputContractCheck Inspect(string? reply, string? contract)
    {
        var keys = ParseKeys(contract);
        if (keys.Count == 0)
        {
            return new SubAgentOutputContractCheck(false, true, Array.Empty<string>());
        }

        var text = reply ?? string.Empty;
        var missing = new List<string>(keys.Count);
        foreach (var key in keys)
        {
            if (!ContainsLabel(text, key))
            {
                missing.Add(key);
            }
        }

        return new SubAgentOutputContractCheck(true, missing.Count == 0, missing);
    }

    /// <summary>
    /// 拆解逗号分隔的键集。空项与大小写重复项被丢弃，保持声明顺序。
    /// </summary>
    public static IReadOnlyList<string> ParseKeys(string? contract)
    {
        if (string.IsNullOrWhiteSpace(contract))
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new List<string>();
        foreach (var raw in contract.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = raw.Trim();
            if (key.Length == 0 || !seen.Add(key))
            {
                continue;
            }

            keys.Add(key);
        }

        return keys;
    }

    private static bool ContainsLabel(string text, string key)
    {
        if (text.Length == 0)
        {
            return false;
        }

        // JSON 回包里的换行是字面量 \n，先还原成真实换行，否则 "message":"SUMMARY:…\n\nRISKS:…" 之类的多行内容看不到行首。
        var normalized = text.Replace("\\n", "\n").Replace("\\r", "\r");

        // 标签边界用「枚举」而不是「字符类」：
        //   行首 / 引号 / 冒号+空白 / 列表或引用符+空白 / 数字序号
        // 这样才能同时覆盖 “2. RISKS:”、JSON 里的 "message": "SUMMARY: …”、
        // 以及 "- **SUMMARY**"，而 “the RISKS section” 这种顺带提及仍不命中。
        var pattern = "(?:^|[\"']|:\\s|[-*>#]\\s|\\d+[.)]\\s)[ \\t]*\\**\\s*"
            + Regex.Escape(key)
            + "\\b";
        return Regex.IsMatch(
            normalized,
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    }
}
