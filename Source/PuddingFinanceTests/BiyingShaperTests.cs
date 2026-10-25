using System.Text.Json.Nodes;
using PuddingFinance.Biying;

namespace PuddingFinanceTests;

/// <summary>
/// 响应整形的回归。核心命题：229 KB 的资产负债表必须变成可控的一段 JSON，
/// 而且**截断必须显式可见**——静默截断会让模型以为"公司只有 5 期财报"。
/// </summary>
[TestClass]
public sealed class BiyingShaperTests
{
    private static BiyingEndpointCatalog Catalog() => BiyingEndpointCatalog.LoadEmbedded();

    [TestMethod]
    public void BalanceSheet_IsCompressedByOrdersOfMagnitude()
    {
        var raw = BuildBalanceSheet(rows: 138);

        var result = BiyingShaper.Shape(Catalog().Get("255"), raw);

        Assert.AreEqual(138, result.TotalRows);
        // 默认 MaxRows=5，但字符预算（8000）会先把行数压下去——这是有意的双层限制
        Assert.IsLessThanOrEqualTo(5, result.ReturnedRows, "不应超过默认行数上限");
        Assert.IsGreaterThanOrEqualTo(1, result.ReturnedRows, "至少要给一行");
        Assert.IsTrue(result.Truncated);
        Assert.IsLessThan(0.05, result.CompressionRatio,
            $"整形后应远小于原始（实际 {result.ShapedChars}/{result.RawChars}）");
    }

    [TestMethod]
    public void RowsAreSelectedByRecency_NotBySourceOrder()
    {
        // 实测：资产负债表是升序（1990 在前），财务指标 cwzb 是倒序。
        // 整形层不能假设源顺序，必须按日期字段自己排序。
        var ascending = BuildBalanceSheet(rows: 10, ascending: true);
        var descending = BuildBalanceSheet(rows: 10, ascending: false);

        var fromAscending = BiyingShaper.Shape(Catalog().Get("255"), ascending);
        var fromDescending = BiyingShaper.Shape(Catalog().Get("255"), descending);

        Assert.AreEqual("jzrq", fromAscending.DateKey);
        Assert.AreEqual(FirstRowDate(fromAscending), FirstRowDate(fromDescending),
            "无论源顺序如何，都应返回同一批最近期数据");
        Assert.AreEqual("2026-06-30", FirstRowDate(fromAscending), "应取最新的报告期");
    }

    [TestMethod]
    public void LegendOnlyCoversFieldsThatAppeared()
    {
        var result = BiyingShaper.Shape(Catalog().Get("255"), BuildBalanceSheet(rows: 3, fieldCount: 8));
        var payload = JsonNode.Parse(result.Json)!.AsObject();
        var legend = payload["legend"]!.AsObject();

        // 8 个真实字段 + 日期键（若不在前 8 个里）
        Assert.IsLessThan(20, legend.Count, "词典不得回传全部 112 项");
        Assert.IsTrue(legend.ContainsKey("jzrq"));
        Assert.AreEqual("截止日期", legend["jzrq"]!.GetValue<string>());
    }

    [TestMethod]
    public void UnknownFieldsAreReportedNotSilentlyDropped()
    {
        var raw = """[{"jzrq":"2026-06-30","weird_field":"x","another_unknown":1}]""";

        var result = BiyingShaper.Shape(Catalog().Get("255"), raw);

        CollectionAssert.Contains(result.UnknownFields.ToList(), "weird_field");
        CollectionAssert.Contains(result.UnknownFields.ToList(), "another_unknown");
    }

    [TestMethod]
    public void UnknownFieldsAppearInPayload_LegendOmitsPlaceholders()
    {
        // 断言的是**模型实际看到的 JSON**，不是 C# 属性。
        // 上一版只断 result.UnknownFields，所以「unknownFields 从未写进 payload」
        // 这个缺陷一直绿着：属性有值，JSON 里没有。
        var raw = """[{"jzrq":"2026-06-30","weird_field":"x","another_unknown":1}]""";

        var result = BiyingShaper.Shape(Catalog().Get("255"), raw);
        var payload = JsonNode.Parse(result.Json)!.AsObject();

        var legend = payload["legend"]!.AsObject();
        Assert.IsTrue(legend.ContainsKey("jzrq"), "有词典的键仍要解释");
        Assert.IsFalse(legend.ContainsKey("weird_field"), "未收录键不得进 legend");
        Assert.IsFalse(result.Json.Contains("词典未收录", StringComparison.Ordinal),
            "占位符不得出现在输出里");

        Assert.AreEqual(2, payload["unknownFieldCount"]!.GetValue<int>());
        var listed = payload["unknownFields"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
        CollectionAssert.AreEquivalent(new[] { "another_unknown", "weird_field" }, listed);
    }

    [TestMethod]
    public void ManyUnknownFields_DoNotInflateShapedOutput()
    {
        // 体积回归锁。占位符方案在真实 flow.stockFlow 上把 1,988 字符撑到 8,787（4.4 倍）；
        // 改用「未收录键只列一次」后约 1.9 倍。上界取 2.5：新方案过、旧方案红。
        var row = new JsonObject { ["jzrq"] = "2026-06-30" };
        for (var i = 0; i < 140; i++)
            row[$"u{i:D3}"] = i;
        var raw = new JsonArray(row).ToJsonString();

        var result = BiyingShaper.Shape(Catalog().Get("255"), raw,
            new BiyingShapeOptions { MaxRows = 1, MaxChars = 100_000 });

        Assert.AreEqual(140, result.UnknownFields.Count);
        Assert.IsLessThan(2.5, (double)result.ShapedChars / result.RawChars,
            $"整形后不应显著大于原始（实际 {result.ShapedChars}/{result.RawChars}）");
    }

    [TestMethod]
    public void FieldProjection_KeepsOnlyRequestedKeys()
    {
        var raw = BuildBalanceSheet(rows: 3, fieldCount: 20);

        var result = BiyingShaper.Shape(Catalog().Get("255"), raw,
            new BiyingShapeOptions { Fields = ["jzrq", "f1", "f2"] });
        var row = JsonNode.Parse(result.Json)!["rows"]!.AsArray()[0]!.AsObject();

        Assert.HasCount(3, row);
        Assert.IsTrue(row.ContainsKey("jzrq"));
        Assert.IsFalse(row.ContainsKey("f3"), "未请求的字段必须被丢弃");
    }

    [TestMethod]
    public void CharBudget_ShrinksRowCountInsteadOfOverflowing()
    {
        var raw = BuildBalanceSheet(rows: 50, fieldCount: 20);

        var result = BiyingShaper.Shape(Catalog().Get("255"), raw,
            new BiyingShapeOptions { MaxRows = 50, MaxChars = 3_000 });

        Assert.IsLessThanOrEqualTo(3_000, result.ShapedChars, "整形结果必须守住字符预算");
        Assert.IsGreaterThanOrEqualTo(1, result.ReturnedRows, "预算够时应给出部分行");
        Assert.IsTrue(result.Truncated);
    }

    [TestMethod]
    public void SingleRowExceedingBudget_ReturnsActionablePayloadInsteadOfOverflowing()
    {
        // 112 字段的一行装不进 200 字符预算。硬塞会让预算形同虚设，
        // 所以应当返回“0 行 + 可行动提示”，而不是超预算的正文。
        var raw = BuildBalanceSheet(rows: 5, fieldCount: 112);

        var result = BiyingShaper.Shape(Catalog().Get("255"), raw,
            new BiyingShapeOptions { MaxRows = 5, MaxChars = 200 });

        Assert.AreEqual(0, result.ReturnedRows);
        Assert.IsLessThanOrEqualTo(200, result.ShapedChars, "回执本身也必须守预算");
        StringAssert.Contains(result.Json, "budgetExceeded");
        StringAssert.Contains(result.Json, "fields");
    }

    [TestMethod]
    public void EmptyArray_IsReportedAsZeroRows()
    {
        var result = BiyingShaper.Shape(Catalog().Get("255"), "[]");

        Assert.AreEqual(0, result.TotalRows);
        Assert.AreEqual(0, result.ReturnedRows);
        Assert.IsFalse(result.Truncated);
    }

    [TestMethod]
    public void NonJsonBody_FallsBackToRawTextWithoutThrowing()
    {
        var result = BiyingShaper.Shape(Catalog().Get("255"), "<html>502 Bad Gateway</html>");

        StringAssert.Contains(result.Json, "502");
        Assert.AreEqual(0, result.TotalRows);
    }

    [TestMethod]
    public void SingleObjectResponse_IsTreatedAsOneRow()
    {
        var raw = """{"t":"2026-10-23 15:35:12","p":11.2,"pc":-0.88}""";

        var result = BiyingShaper.Shape(Catalog().Get("54"), raw);

        Assert.AreEqual(1, result.TotalRows);
        Assert.AreEqual(1, result.ReturnedRows);
        Assert.IsFalse(result.Truncated);
    }

    private static string FirstRowDate(BiyingShapeResult result)
        => JsonNode.Parse(result.Json)!["rows"]!.AsArray()[0]!["jzrq"]!.GetValue<string>();

    /// <summary>构造一份接近真实形状的资产负债表：112 个键、按日期排序。</summary>
    private static string BuildBalanceSheet(int rows, int fieldCount = 112, bool ascending = true)
    {
        var array = new JsonArray();
        for (var i = 0; i < rows; i++)
        {
            var row = new JsonObject();
            // 每行 3 个月，最后一行落在 2026-06-30
            var date = new DateTime(2026, 6, 30).AddMonths(-3 * (rows - 1 - i)).ToString("yyyy-MM-dd");
            row["jzrq"] = i == rows - 1 ? "2026-06-30" : date;
            row["plrq"] = "2026-08-20";
            for (var f = 0; f < fieldCount - 2; f++)
                row["f" + f] = 1234.5678m;

            array.Add(row);
        }

        if (!ascending)
            array = new JsonArray(array.Reverse().Select(node => node!.DeepClone()).ToArray());

        return array.ToJsonString();
    }
}
