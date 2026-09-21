namespace PuddingCode.Skills.Family;

/// <summary>
/// 技能名称 → token 的**唯一**分词口径，以及 token 集合的 Jaccard 相似度。
/// <para>
/// <b>为什么单独成类型</b>：家族划分（本切片的家族键）、关键词归属裁决、以及准入侧的关键词碰撞检测
/// 都必须用**同一套**"名称/关键词怎么切、怎么归一"的规则；否则同一条技能在两处会得到不同结论，
/// 而这类漂移**不会有任何断言失败**（本会话反复出现的同一族缺陷）。故此处把口径抬成类型事实。
/// </para>
/// <para>
/// ⚠️ <b>本口径是保守下界</b>：名称分词只能发现「名字就像同一件事」的重复，
/// 发现不了措辞完全不同的同类（那需要 embedding，属**另一个**切片）。
/// </para>
/// </summary>
public static class SkillNameTokenization
{
    /// <summary>
    /// 标准分隔符集合 —— 与现存两处实现**同源**：
    /// <list type="bullet">
    /// <item><c>SkillEnforcerService.CollectKeywords</c> 的名称分词（生产注入侧）；</item>
    /// <item>只读盘点脚本的名字分词（报告侧）。</item>
    /// </list>
    /// 两者在实测中完全一致：空格 / <c>|</c> / <c>,</c> / <c>/</c> / <c>：</c> / <c>、</c>。
    /// <para>
    /// ⛔ 改动本集合会**同时**改变注入侧命中面与家族划分 ⇒ 属口径变更，必须递增策略版本并留人工裁决记录。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<char> StandardSeparators = [' ', '|', ',', '/', '：', '、'];

    /// <summary>
    /// 把名称切成 token 集合：按策略分隔符切分 → <c>Trim()</c> → <c>ToLowerInvariant()</c> →
    /// 丢弃长度小于 <see cref="SkillFamilyPolicy.MinTokenLength"/> 的项 → 去重。
    /// <para>
    /// 归一化的两个选择都是**被冻结的语义**：小写化让"ABC"与"abc"是同一个 token；
    /// 用不变文化（<c>Invariant</c>）而非当前文化，保证同一份数据在任何机器上得到同一集合
    /// （家族键必须可复现，否则家族上限不可复现）。
    /// </para>
    /// </summary>
    /// <param name="name">技能名称；<c>null</c> / 空白 ⇒ 空集合（不是异常：无名技能只是"无可比 token"）。</param>
    /// <param name="policy">提供分隔符与长度下限的策略对象。</param>
    public static IReadOnlySet<string> Tokenize(string? name, SkillFamilyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(name))
        {
            return tokens;
        }

        foreach (var part in name.Split(policy.NameSeparators.ToArray()))
        {
            var trimmed = part.Trim().ToLowerInvariant();
            if (trimmed.Length >= policy.MinTokenLength)
            {
                tokens.Add(trimmed);
            }
        }

        return tokens;
    }

    /// <summary>
    /// 两个 token 集合的 Jaccard 相似度 = 交集 / 并集。
    /// <para>
    /// ⚠️ <b>任一侧为空即返回 <c>0</c></b>（含两侧同为空的情形）。这是**刻意的**定义：
    /// 数学上"两个空集的相似度"无定义，而工程上必须给一个确定值。
    /// 取值 <c>0</c> 的含义是「<b>可比信息为零 ⇒ 视为不构成家族</b>」——
    /// 若取 <c>1</c>，则所有"解析不出 token"的技能会**互相**同族，制造一个纯噪声的大家族。
    /// </para>
    /// </summary>
    public static double Jaccard(IReadOnlySet<string>? left, IReadOnlySet<string>? right)
    {
        if (left is null || right is null || left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var intersection = 0;
        foreach (var token in left)
        {
            if (right.Contains(token))
            {
                intersection++;
            }
        }

        var union = left.Count + right.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }
}
