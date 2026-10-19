using System.Text;
using System.Text.Json;
using PuddingHomeAssistant.Policy;

namespace PuddingHomeAssistantTests;

/// <summary>
/// <see cref="HaHistoryBudget"/> 的三条不变量：裁剪后仍是合法 JSON、保序、如实回报丢弃。
/// 变异取红：把 <c>Trim</c> 里的 <c>fullBytes &lt;= maxBytes</c> 改成 <c>true</c>，
/// <see cref="OverBudgetResultStaysParseable"/> 与 <see cref="TrimmingKeepsOrderAndReportsDroppedEntities"/> 立即转红。
/// </summary>
[TestClass]
public sealed class HaHistoryBudgetTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string State(string entityId, string state, string lastChanged) =>
        "{\"entity_id\":\"" + entityId +
        "\",\"state\":\"" + state +
        "\",\"last_changed\":\"" + lastChanged +
        "\",\"attributes\":{\"friendly_name\":\"主卧室灯光\"}}";

    private static string History(params (string Entity, int Count)[] entities)
    {
        var parts = new List<string>(entities.Length);
        foreach (var (entity, count) in entities)
        {
            var states = Enumerable.Range(0, count)
                .Select(i => State(entity, i % 2 == 0 ? "on" : "off", $"2026-10-18T10:{i:00}:00+00:00"));
            parts.Add("[" + string.Join(',', states) + "]");
        }

        return "[" + string.Join(',', parts) + "]";
    }

    [TestMethod]
    public void UnderBudgetIsReturnedVerbatim()
    {
        var json = History(("light.a", 3), ("light.b", 2));
        var root = Parse(json);

        var result = HaHistoryBudget.Trim(root, 4096);

        Assert.IsFalse(result.Truncated);
        Assert.AreEqual(2, result.EntityCount);
        Assert.AreEqual(2, result.KeptEntities);
        Assert.AreEqual(0, result.DroppedEntities);
        Assert.AreEqual(json, result.Data.GetRawText());
    }

    [TestMethod]
    public void OverBudgetResultStaysParseable()
    {
        var json = History(("light.a", 40), ("light.b", 40), ("light.c", 40));
        var fullBytes = Encoding.UTF8.GetByteCount(json);
        var budget = fullBytes / 2;
        var root = Parse(json);

        var result = HaHistoryBudget.Trim(root, budget);

        // 关键断言：逐字符截断会产生无法解析的半个对象，这里必须能重新解析。
        using var doc = JsonDocument.Parse(result.Data.GetRawText());
        Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.IsTrue(result.Truncated);
        Assert.IsTrue(result.EstimatedBytes <= budget, $"估算 {result.EstimatedBytes} 应 <= {budget}");
    }

    [TestMethod]
    public void TrimmingKeepsOrderAndReportsDroppedEntities()
    {
        var json = History(("light.a", 40), ("light.b", 40), ("light.c", 40), ("light.d", 40));
        var root = Parse(json);

        var result = HaHistoryBudget.Trim(root, Encoding.UTF8.GetByteCount(json) / 3);

        Assert.IsTrue(result.Truncated);
        Assert.AreEqual(4, result.EntityCount);
        Assert.IsTrue(result.KeptEntities >= 1 && result.KeptEntities < 4,
            $"KeptEntities={result.KeptEntities} 应处于 [1,4)");
        Assert.AreEqual(result.EntityCount - result.KeptEntities, result.DroppedEntities);

        // 保序：保留的 entity 必须是原顺序的前缀。
        var keptIds = result.Data.EnumerateArray()
            .Select(e => e.EnumerateArray().First().GetProperty("entity_id").GetString())
            .ToList();
        CollectionAssert.AreEqual(
            new[] { "light.a", "light.b", "light.c", "light.d" }.Take(keptIds.Count).ToList(),
            keptIds);
    }

    [TestMethod]
    public void FirstEntityTooLargeIsTrimmedToStatePrefix()
    {
        var json = History(("light.a", 60));
        var root = Parse(json);
        var budget = Encoding.UTF8.GetByteCount(json) / 3;

        var result = HaHistoryBudget.Trim(root, budget);

        Assert.IsTrue(result.Truncated);
        Assert.AreEqual(1, result.EntityCount);
        Assert.AreEqual(1, result.KeptEntities);
        Assert.AreEqual(0, result.DroppedEntities);

        var states = result.Data[0];
        Assert.IsTrue(states.GetArrayLength() >= 1 && states.GetArrayLength() < 60,
            $"状态条数 {states.GetArrayLength()} 应处于 [1,60)");
    }

    [TestMethod]
    public void NonArrayRootIsReportedWithoutMutation()
    {
        const string json = "{\"error\":\"unexpected\"}";
        var root = Parse(json);

        var result = HaHistoryBudget.Trim(root, 4096);

        Assert.IsFalse(result.Truncated);
        Assert.AreEqual(json, result.Data.GetRawText());
        Assert.AreEqual(0, result.EntityCount);
    }

    [TestMethod]
    public void TinyBudgetStillProducesValidJson()
    {
        var root = Parse(History(("light.a", 5)));

        var result = HaHistoryBudget.Trim(root, 3);

        using var doc = JsonDocument.Parse(result.Data.GetRawText());
        Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.IsTrue(result.Truncated);
        Assert.AreEqual(0, result.KeptEntities);
        Assert.AreEqual(1, result.DroppedEntities);
    }

    [TestMethod]
    public void BudgetMustBeUsable()
    {
        var root = Parse("[]");

        var threw = false;
        try
        {
            HaHistoryBudget.Trim(root, 2);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "maxBytes<=2 必须被拒绝");
    }
}
