using System.Text.Json;
using PuddingHomeAssistant.Contracts;

namespace PuddingHomeAssistant.Policy;

/// <summary>
/// 输出预算与投影。实测实例有 2,217 个实体，原始 `/api/states` 载荷达 MB 级，
/// 且相机实体的 attributes 含 base64 缩略图（单条可达数百 KB）。
///
/// <para>不变量：</para>
/// <list type="bullet">
/// <item>先按 domain/entityId 过滤，再投影字段，最后按 limit 截断；</item>
/// <item>截断**必须**回报 <c>total</c> / <c>returned</c> / <c>truncated</c>，禁止静默变少；</item>
/// <item>序列化字节数超过 <see cref="MaxBytes"/> 时按条回退，直到落入预算。</item>
/// </list>
/// </summary>
public static class HaOutputBudget
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;

    /// <summary>单次响应的序列化字节上限（256 KiB）。</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>把调用方请求的 limit 收窄到 [1, MaxLimit]；缺省/非法取 <see cref="DefaultLimit"/>。</summary>
    public static int NormalizeLimit(int? requested)
    {
        if (requested is null or <= 0)
        {
            return DefaultLimit;
        }

        return Math.Min(requested.Value, MaxLimit);
    }

    /// <summary>
    /// 过滤 + 投影 + 截断。`fields` 为空时返回精简视图（不带 attributes），
    /// 因为 attributes 既占体积又可能含敏感信息（token、地理位置、相机帧）。
    /// </summary>
    public static HaStatePage Project(
        IEnumerable<HaState> states,
        int limit,
        IReadOnlyCollection<string>? fields = null,
        IReadOnlyCollection<string>? domains = null,
        string? entityId = null)
    {
        ArgumentNullException.ThrowIfNull(states);

        var effectiveLimit = NormalizeLimit(limit);
        var all = states.ToList();

        IEnumerable<HaState> filtered = all;
        if (!string.IsNullOrWhiteSpace(entityId))
        {
            filtered = filtered.Where(s => string.Equals(s.EntityId, entityId, StringComparison.Ordinal));
        }
        else if (domains is { Count: > 0 })
        {
            var set = new HashSet<string>(domains, StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(s => set.Contains(s.Domain));
        }

        var matched = filtered.ToList();
        var take = Math.Min(effectiveLimit, matched.Count);

        var includeAttributes = fields is { Count: > 0 } &&
                                fields.Any(f => string.Equals(f, "attributes", StringComparison.OrdinalIgnoreCase));

        var views = new List<HaStateView>(take);
        foreach (var s in matched.Take(take))
        {
            views.Add(new HaStateView(
                s.EntityId,
                s.State,
                s.LastChanged,
                s.LastUpdated,
                includeAttributes ? s.Attributes : null,
                ResolveName(s.Attributes)));
        }

        // 字节预算：超限时按条回退（保留顺序），直到落入预算。
        while (views.Count > 0)
        {
            var bytes = Measure(views);
            if (bytes <= MaxBytes)
            {
                return new HaStatePage(views, matched.Count, views.Count, views.Count < matched.Count, bytes);
            }

            var keep = Math.Max(1, views.Count / 2);
            if (keep == views.Count)
            {
                // 单条就超预算：只保留第一条并把 attributes 剥掉
                views = new List<HaStateView>
                {
                    views[0] with { Attributes = null },
                };
                var singleBytes = Measure(views);
                return new HaStatePage(views, matched.Count, views.Count, true, singleBytes);
            }

            views = views.Take(keep).ToList();
        }

        return new HaStatePage([], matched.Count, 0, matched.Count > 0, 0);
    }

    /// <summary>
    /// 从 attributes 中取出 <c>friendly_name</c>。它**总是**随投影返回，
    /// 与 <c>include_attributes</c> 无关：没有它，调用方看到的只是一个不可读的实体 ID。
    /// </summary>
    public static string? ResolveName(IReadOnlyDictionary<string, JsonElement>? attributes)
    {
        if (attributes is null ||
            !attributes.TryGetValue("friendly_name", out var name) ||
            name.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = name.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>序列化后的 UTF-8 字节数（用于预算判定）。</summary>
    public static int Measure(IReadOnlyList<HaStateView> views)
    {
        if (views.Count == 0)
        {
            return 0;
        }

        return JsonSerializer.SerializeToUtf8Bytes(views).Length;
    }
}
