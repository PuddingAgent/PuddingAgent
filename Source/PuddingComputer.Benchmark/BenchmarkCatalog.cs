using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuddingComputer.Benchmark;

/// <summary>
/// 基准任务清单的读写与整份校验。
/// <para>
/// 清单是**数据**而不是代码：任务会随被测应用变化，写死在源码里就没法对比不同版本的结果。
/// </para>
/// </summary>
public static class BenchmarkCatalog
{
    /// <summary>
    /// 序列化选项。
    /// <para>
    /// 用 camelCase 与 oracle 的多态判别值（<c>kind</c>）保持一致；
    /// <see cref="JsonStringEnumConverter"/> 让 risk 写成 <c>reversible</c> 而不是数字 ——
    /// 数字在 JSON 里不可读，人工审清单时会看不出风险等级。
    /// </para>
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
    };

    /// <summary>从 JSON 文本加载任务清单。</summary>
    /// <exception cref="InvalidOperationException">JSON 结构不对或反序列化为 null。</exception>
    public static IReadOnlyList<BenchmarkTask> Load(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        List<BenchmarkTask>? tasks;
        try
        {
            tasks = JsonSerializer.Deserialize<List<BenchmarkTask>>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"benchmark JSON 解析失败：{ex.Message}", ex);
        }

        return tasks ?? throw new InvalidOperationException("benchmark JSON 反序列化结果为 null");
    }

    /// <summary>把任务清单序列化为 JSON 文本。</summary>
    public static string Save(IReadOnlyList<BenchmarkTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return JsonSerializer.Serialize(tasks, Options);
    }

    /// <summary>
    /// 校验整份清单：逐条字段自检 + Id 唯一性。
    /// </summary>
    /// <returns>问题列表，空表示通过。</returns>
    public static IReadOnlyList<string> Validate(IReadOnlyList<BenchmarkTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var task in tasks)
        {
            foreach (var problem in task.Validate())
                problems.Add($"[{task.Id}] {problem}");

            // Id 是报告里对齐用的唯一键，重复会让结果无法归因。
            if (!seen.Add(task.Id))
                problems.Add($"[{task.Id}] Id 重复");
        }

        return problems;
    }
}
