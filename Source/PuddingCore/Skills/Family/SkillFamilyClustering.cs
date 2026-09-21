namespace PuddingCode.Skills.Family;

/// <summary>
/// 家族划分的输入：**只有**「id + 名称」两件事。
/// <para>
/// <b>为什么刻意不含 keywords / tags</b>：keywords/tags 里含大量共享工具名（如 <c>file_read</c>），
/// 算进去会让**所有**技能都"相似" ⇒ 家族划分退化成"一个大家族"的假象。
/// 这一取舍在只读盘点报告里已被实测验证过，此处沿用同一口径。
/// </para>
/// </summary>
public sealed record SkillFamilySubject
{
    /// <summary>技能 id。</summary>
    public required string SkillId { get; init; }

    /// <summary>技能名称（可为空字符串：无名技能只是"无可比 token"）。</summary>
    public required string Name { get; init; }

    /// <summary>
    /// 校验式工厂：<see cref="SkillId"/> 是家族成员身份，**不得**为空
    /// （空 id 会在既有集合里与其它空 id 静默合并成同一个"成员"）。
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="skillId"/> 为空白时抛出。</exception>
    public static SkillFamilySubject Create(string skillId, string? name)
    {
        if (string.IsNullOrWhiteSpace(skillId))
        {
            throw new ArgumentException("技能 id 不得为空白。", nameof(skillId));
        }

        return new SkillFamilySubject { SkillId = skillId, Name = name ?? string.Empty };
    }
}

/// <summary>
/// 一个技能家族（簇）。
/// <para>
/// ⚠️ <b>相等性按内容比较，不按引用</b>：<see cref="Members"/> 声明为 <see cref="IReadOnlyList{T}"/>，
/// 而 <c>record</c> 自动生成的相等性对集合字段用的是**引用相等** —— 那会让两个内容完全相同的簇
/// 被判为不等，进而使"两次划分结果一致"这类断言**静默失效**。故此处显式覆写。
/// </para>
/// </summary>
public sealed record SkillFamilyCluster
{
    /// <summary>
    /// 家族键 = 簇内**按序数序最小**的成员 id。
    /// <para>
    /// 为什么用"最小成员"而不是并查集的根：根取决于合并顺序（顺序不同根就不同），
    /// 而家族上限的裁决必须**与输入顺序无关**。取最小成员是与顺序无关的确定性函数。
    /// </para>
    /// </summary>
    public required string FamilyKey { get; init; }

    /// <summary>成员 id，**按序数序升序**（确定性输出，调用方无需再排序）。</summary>
    public required IReadOnlyList<string> Members { get; init; }

    /// <summary>成员数。单成员簇表示「该技能与任何其他技能都不构成家族」（孤立技能），不是"未处理"。</summary>
    public int Size => Members.Count;

    /// <inheritdoc />
    public bool Equals(SkillFamilyCluster? other)
        => other is not null
            && string.Equals(FamilyKey, other.FamilyKey, StringComparison.Ordinal)
            && Members.SequenceEqual(other.Members, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(FamilyKey, StringComparer.Ordinal);
        foreach (var member in Members)
        {
            hash.Add(member, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// 家族聚类（**纯函数**，零 IO、零 LLM、零写盘）：按名称 token 的 Jaccard 相似度做并查集传递闭包。
/// <para>
/// <b>为什么是并查集而不是"相似度分组"</b>：Jaccard **不满足传递性**（A~B、B~C 但 A 与 C 可能毫不相似）。
/// 家族上限的语义是"同一件事上的重复建设规模"，链条式相似正是要合并的对象，
/// 故采用传递闭包；本选择由用例显式钉住（A~B~C 三链 ⇒ 一个家族）。
/// </para>
/// <para>
/// <b>确定性（本类型的核心约束）</b>：结果只取决于**集合内容**，与输入顺序无关 ——
/// 输入先规范化排序、家族键取最小成员、成员与簇均按序数序输出。
/// 否则"上限强制生效"会变成"取决于索引文件里技能恰好排在第几行"。
/// </para>
/// </summary>
public static class SkillFamilyClusterer
{
    /// <summary>
    /// 划分家族。返回**全部**簇（含单成员簇），按 <see cref="SkillFamilyCluster.FamilyKey"/> 序数序升序。
    /// <para>
    /// ⛔ 不得把单成员簇从结果里过滤掉：那会让"孤立技能有多少"这一事实消失，
    /// 读者会以为"所有技能都归了族"。孤立技能的存在本身就是 G4 结论的一部分。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">策略配置非法时抛出（入口即校验，不依赖调用方先调 <c>Create</c>）。</exception>
    public static IReadOnlyList<SkillFamilyCluster> Cluster(
        IReadOnlyList<SkillFamilySubject> subjects,
        SkillFamilyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(policy);
        policy.EnsureValid();

        // ① 规范化：排序 ⇒ 后续"取首个"与"去重"都是确定性的；去重按 id 忽略大小写，
        //    保证同一技能的不同大小写写法不会变成两个成员。
        var ordered = subjects
            .OrderBy(subject => subject.SkillId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(subject => subject.SkillId, StringComparer.Ordinal)
            .ThenBy(subject => subject.Name, StringComparer.Ordinal)
            .ToArray();

        var ids = new List<string>(ordered.Length);
        var tokensById = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var subject in ordered)
        {
            if (tokensById.ContainsKey(subject.SkillId))
            {
                continue;
            }

            ids.Add(subject.SkillId);
            tokensById[subject.SkillId] = SkillNameTokenization.Tokenize(subject.Name, policy);
        }

        if (ids.Count == 0)
        {
            return [];
        }

        // ② 并查集：相似度达到阈值即合并（传递闭包）。
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            parent[id] = id;
        }

        for (var i = 0; i < ids.Count; i++)
        {
            for (var j = i + 1; j < ids.Count; j++)
            {
                var similarity = SkillNameTokenization.Jaccard(tokensById[ids[i]], tokensById[ids[j]]);
                if (similarity >= policy.NameTokenJaccardThreshold)
                {
                    Union(parent, ids[i], ids[j]);
                }
            }
        }

        // ③ 归簇：根只用于分组，**不**用作家族键（根依赖合并顺序）。
        var byRoot = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var root = Find(parent, id);
            if (!byRoot.TryGetValue(root, out var members))
            {
                members = [];
                byRoot[root] = members;
            }

            members.Add(id);
        }

        var clusters = new List<SkillFamilyCluster>(byRoot.Count);
        foreach (var members in byRoot.Values)
        {
            var sorted = members.OrderBy(member => member, StringComparer.Ordinal).ToArray();
            clusters.Add(new SkillFamilyCluster
            {
                FamilyKey = sorted[0],
                Members = sorted,
            });
        }

        clusters.Sort((left, right) => string.CompareOrdinal(left.FamilyKey, right.FamilyKey));
        return clusters;
    }

    private static string Find(Dictionary<string, string> parent, string id)
    {
        var root = id;
        while (!string.Equals(parent[root], root, StringComparison.OrdinalIgnoreCase))
        {
            root = parent[root];
        }

        // 路径压缩：家族规模与技能数同阶，但压缩能让"超大簇"不退化。
        while (!string.Equals(parent[id], root, StringComparison.OrdinalIgnoreCase))
        {
            var next = parent[id];
            parent[id] = root;
            id = next;
        }

        return root;
    }

    private static void Union(Dictionary<string, string> parent, string left, string right)
    {
        var leftRoot = Find(parent, left);
        var rightRoot = Find(parent, right);
        if (string.Equals(leftRoot, rightRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        parent[leftRoot] = rightRoot;
    }
}
