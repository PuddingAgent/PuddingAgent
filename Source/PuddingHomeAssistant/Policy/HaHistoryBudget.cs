using System.Text;
using System.Text.Json;

namespace PuddingHomeAssistant.Policy;

/// <summary>
/// 历史响应（<c>/api/history/period</c>）的字节预算裁剪。
///
/// <para>背景：该端点的返回形状是「每个 entity 一个状态数组」的数组，
/// 24 小时窗口在设备频繁上下线时可达数十万字节。宿主必须给出**有界**响应。</para>
///
/// <para>不变量（三条都由测试锁住）：</para>
/// <list type="bullet">
/// <item>裁剪结果**始终是合法 JSON**；逐字节截断字符串会产生不可解析的半个对象；</item>
/// <item>裁剪按「entity 顺序 → entity 内 state 顺序」保序，先到的先保留；</item>
/// <item>必须如实回报 <c>truncated</c> 与 <c>droppedEntities</c>，不允许静默变少。</item>
/// </list>
/// </summary>
public static class HaHistoryBudget
{
    /// <summary>单次历史响应的序列化字节上限（256 KiB）。</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>
    /// 把历史响应裁剪到 <paramref name="maxBytes"/> 字节以内。
    /// 未超预算时**原样返回**（不做任何重排或重新序列化）。
    /// </summary>
    public static HaHistoryTrim Trim(JsonElement root, int maxBytes = MaxBytes)
    {
        if (maxBytes <= 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "maxBytes must be greater than 2.");
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            // 形状意外的响应体：不做裁剪，只如实回报字节数供调用方判断。
            var raw = root.GetRawText();
            var size = Encoding.UTF8.GetByteCount(raw);
            return new HaHistoryTrim(root, size > maxBytes, 0, 0, 0, size);
        }

        var entities = root.EnumerateArray().ToList();
        var fullBytes = Encoding.UTF8.GetByteCount(root.GetRawText());
        if (fullBytes <= maxBytes)
        {
            return new HaHistoryTrim(root, false, entities.Count, entities.Count, 0, fullBytes);
        }

        var builder = new StringBuilder("[");
        var used = 0;
        var keptEntities = 0;

        foreach (var entity in entities)
        {
            var separate = keptEntities > 0 ? 1 : 0;
            var raw = entity.GetRawText();
            var need = Encoding.UTF8.GetByteCount(raw) + separate;

            if (used + need + 2 <= maxBytes)
            {
                if (separate == 1)
                {
                    builder.Append(',');
                }

                builder.Append(raw);
                used += need;
                keptEntities++;
                continue;
            }

            // 整个 entity 装不下：若它是状态数组，保留能装下的前缀（保序）。
            var allowance = maxBytes - used - 2 - separate;
            var prefix = TryTrimInner(entity, allowance);
            if (prefix is not null)
            {
                if (separate == 1)
                {
                    builder.Append(',');
                }

                builder.Append(prefix);
                var prefixBytes = Encoding.UTF8.GetByteCount(prefix);
                used += prefixBytes + separate;
                keptEntities++;
            }

            break;
        }

        builder.Append(']');
        var json = builder.ToString();

        using var doc = JsonDocument.Parse(json);
        return new HaHistoryTrim(
            doc.RootElement.Clone(),
            Truncated: true,
            EntityCount: entities.Count,
            KeptEntities: keptEntities,
            DroppedEntities: entities.Count - keptEntities,
            EstimatedBytes: Encoding.UTF8.GetByteCount(json));
    }

    /// <summary>
    /// 取状态数组的前缀，使整个前缀数组（含方括号）不超过 <paramref name="allowance"/> 字节。
    /// 一个状态都装不下时返回 <c>null</c>（调用方据此丢弃该 entity）。
    /// </summary>
    private static string? TryTrimInner(JsonElement entity, int allowance)
    {
        if (entity.ValueKind != JsonValueKind.Array || allowance <= 2)
        {
            return null;
        }

        var builder = new StringBuilder("[");
        var used = 0;
        var count = 0;

        foreach (var state in entity.EnumerateArray())
        {
            var raw = state.GetRawText();
            var need = Encoding.UTF8.GetByteCount(raw) + (count > 0 ? 1 : 0);
            if (used + need + 2 > allowance)
            {
                break;
            }

            if (count > 0)
            {
                builder.Append(',');
            }

            builder.Append(raw);
            used += need;
            count++;
        }

        if (count == 0)
        {
            return null;
        }

        builder.Append(']');
        return builder.ToString();
    }
}

/// <summary>裁剪结果。字段名即调用方契约，改名前先看 <c>HaHistoryBudgetTests</c>。</summary>
public sealed record HaHistoryTrim(
    JsonElement Data,
    bool Truncated,
    int EntityCount,
    int KeptEntities,
    int DroppedEntities,
    int EstimatedBytes);
