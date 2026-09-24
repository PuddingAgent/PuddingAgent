namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 一组 scope 标识的**规范集合**（去重 + 确定性排序 + 取值相等）。
/// <para>
/// 存在理由：ADR-089 §2.4 要求"多 scope 命中作为<b>元数据/置信度加权</b>"，
/// 因此命中上要挂一组 scope。用裸数组做 record 字段会让取值相等退化成引用相等
/// （`string[]` 的默认比较器是引用比较）—— 于是"两处独立构造的相同命中不相等"
/// 这种隐性陷阱就会出现。本类型把集合压成一个规范字符串并只比较它，
/// 从而"规范化"在构造期完成、无法绕过。
/// </para>
/// </summary>
public sealed record ScopeIdSet
{
    private const char Separator = '\u001f';

    private readonly string _key;

    private ScopeIdSet(string key, int count)
    {
        _key = key;
        Count = count;
    }

    /// <summary>空集合。</summary>
    public static ScopeIdSet Empty { get; } = new(string.Empty, 0);

    /// <summary>元素个数（去重后）。</summary>
    public int Count { get; }

    /// <summary>是否为多 scope 佐证（&gt; 1）。</summary>
    public bool IsCorroborated => Count > 1;

    /// <summary>确定性排序（Ordinal）后的 scope 标识。</summary>
    public IReadOnlyList<string> Ids => _key.Length == 0 ? [] : _key.Split(Separator);

    /// <summary>构造集合：空白项被拒；控制字符被拒（它们是内部分隔符）。</summary>
    public static ScopeIdSet Of(IEnumerable<string>? ids)
    {
        if (ids is null)
            return Empty;

        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in ids)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new ArgumentException("scope 标识不得为空。", nameof(ids));

            var id = raw.Trim();
            if (id.Any(char.IsControl))
                throw new ArgumentException($"scope 标识不得包含控制字符：'{id}'。", nameof(ids));

            set.Add(id);
        }

        return set.Count == 0 ? Empty : new ScopeIdSet(string.Join(Separator, set), set.Count);
    }

    /// <summary>并集（去重合并时用）。</summary>
    public ScopeIdSet Union(ScopeIdSet other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Count == 0)
            return other;

        if (other.Count == 0)
            return this;

        return Of(Ids.Concat(other.Ids));
    }

    /// <inheritdoc />
    public override string ToString() => _key.Replace(Separator, ',');
}
