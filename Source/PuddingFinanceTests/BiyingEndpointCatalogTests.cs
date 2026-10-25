using PuddingFinance.Biying;

namespace PuddingFinanceTests;

/// <summary>
/// 端点注册表与 URL 拼装的回归。用例的取样全部来自**实测返回**，
/// 不是照抄文档示例——文档里的 <c>?licence=</c> 写法是错的（实测返回
/// <c>{"code":404,"message":"Licence证书不存在","licence":"&lt;代码&gt;"}</c>）。
/// </summary>
[TestClass]
public sealed class BiyingEndpointCatalogTests
{
    private const string Licence = "08AF0000-0000-0000-0000-000000000000";

    private static BiyingEndpointCatalog Catalog() => BiyingEndpointCatalog.LoadEmbedded();

    [TestMethod]
    public void EmbeddedCatalog_LoadsAllEndpoints()
    {
        var catalog = Catalog();

        Assert.AreEqual(200, catalog.Count, "注册表应含 200 个端点");
        StringAssert.StartsWith(catalog.Schema, "pudding.biying.endpoints.v2");
    }

    [TestMethod]
    public void EveryEndpoint_HasFieldDictionary()
    {
        // 与 extract_endpoints.py --check 的硬门禁同义：
        // 没有词典，模型面对 jzrq/plrq 这类拼音键只能猜，猜错比不给更糟。
        var missing = Catalog().All.Where(endpoint => endpoint.Fields.Count == 0).Select(endpoint => endpoint.Id).ToList();

        Assert.IsEmpty(missing, $"以下端点缺少字段词典：{string.Join(",", missing)}");
    }

    [TestMethod]
    public void BalanceSheet_CarriesHundredsOfFields()
    {
        var endpoint = Catalog().Get("255");

        Assert.AreEqual("资产负债表", endpoint.Name);
        // 签名是 (lowerBound, value)：先下界后实际值，写反了会把断言意义颠倒
        Assert.IsGreaterThanOrEqualTo(100, endpoint.Fields.Count, $"资产负债表字段数应过百，实际 {endpoint.Fields.Count}");
        Assert.IsTrue(endpoint.Fields.Any(field => field.Key == "jzrq" && field.Description.Contains("截止日期")));
    }

    [TestMethod]
    public void BuildUrl_PlacesCodeThenLicenceAsPathSegments()
    {
        var catalog = Catalog();

        var quote = BiyingUrlBuilder.BuildUrl(catalog.Get("54"), Licence, ["000001"]);

        Assert.AreEqual($"https://api.biyingapi.com/hsrl/ssjy/000001/{Licence}", quote);
    }

    [TestMethod]
    public void BuildUrl_EndpointWithoutCode_HasLicenceAsLastSegment()
    {
        var catalog = Catalog();

        var tree = BiyingUrlBuilder.BuildUrl(catalog.Get("4"), Licence);

        Assert.AreEqual($"https://api.biyingapi.com/hszg/list/{Licence}", tree);
    }

    [TestMethod]
    public void BuildUrl_ProEndpoint_FillsBothPathArguments()
    {
        // 313 模板：hsstock/pro/latest/{代码.市场}/1/{除权方式}/{licence}
        var catalog = Catalog();

        var url = BiyingUrlBuilder.BuildUrl(catalog.Get("313"), Licence, ["000001.SZ", "n"]);

        Assert.AreEqual($"https://vip.biyingapi.com/hsstock/pro/latest/000001.SZ/1/n/{Licence}", url);
    }

    [TestMethod]
    public void BuildUrl_TooFewPathArguments_ThrowsActionableMessage()
    {
        var catalog = Catalog();

        var error = Assert.ThrowsExactly<BiyingEndpointException>(
            () => BiyingUrlBuilder.BuildUrl(catalog.Get("313"), Licence, ["000001.SZ"]));

        StringAssert.Contains(error.Message, "需要 2 个路径参数");
        StringAssert.Contains(error.Message, "313");
    }

    [TestMethod]
    public void BuildUrl_TooManyPathArguments_ThrowsInsteadOfSilentlyDropping()
    {
        var catalog = Catalog();

        var error = Assert.ThrowsExactly<BiyingEndpointException>(
            () => BiyingUrlBuilder.BuildUrl(catalog.Get("54"), Licence, ["000001", "extra"]));

        StringAssert.Contains(error.Message, "只接受 1 个路径参数");
    }

    [TestMethod]
    public void BuildUrl_UnknownQueryKey_ThrowsWithAllowedKeys()
    {
        var catalog = Catalog();

        var error = Assert.ThrowsExactly<BiyingEndpointException>(
            () => BiyingUrlBuilder.BuildUrl(catalog.Get("54"), Licence, ["000001"],
                new Dictionary<string, string> { ["nonsense"] = "1" }));

        StringAssert.Contains(error.Message, "不支持查询参数");
    }

    [TestMethod]
    public void BuildUrl_DeclaredQueryKey_IsAppended()
    {
        // 314 模板声明了 st/et/lt 三个查询键
        var catalog = Catalog();

        var url = BiyingUrlBuilder.BuildUrl(catalog.Get("314"), Licence, ["000001.SZ", "n"],
            new Dictionary<string, string> { ["lt"] = "5" });

        StringAssert.EndsWith(url, "?lt=5");
    }

    [TestMethod]
    public void Get_UnknownEndpoint_SuggestsCandidates()
    {
        var catalog = Catalog();

        var error = Assert.ThrowsExactly<BiyingEndpointException>(() => catalog.Get("hsrl/不存在"));

        StringAssert.Contains(error.Message, "不在注册表中");
        StringAssert.Contains(error.Message, "最接近的候选");
    }

    [TestMethod]
    public void Search_FindsEndpointsByChineseName()
    {
        var hits = Catalog().Search("资产负债表", 5);

        Assert.IsGreaterThanOrEqualTo(1, hits.Count);
        Assert.AreEqual("255", hits[0].Id);
    }

    [TestMethod]
    public void TryGet_ResolvesByPathAsWellAsId()
    {
        Assert.IsTrue(Catalog().TryGet("hsrl/ssjy", out var byPath));
        Assert.AreEqual("54", byPath.Id);
        Assert.IsTrue(Catalog().TryGet("54", out var byId));
        Assert.AreEqual(byPath.Id, byId.Id);
    }

    [TestMethod]
    public void Redact_RemovesLicenceFromLoggedUrl()
    {
        var url = BiyingUrlBuilder.BuildUrl(Catalog().Get("54"), Licence, ["000001"]);

        var redacted = BiyingUrlBuilder.Redact(url, Licence);

        Assert.DoesNotContain(Licence, redacted, "日志 URL 不得包含证书");
        StringAssert.Contains(redacted, "<licence>");
    }

    [TestMethod]
    public void Segments_ClassifyLicenceByContentNotPosition()
    {
        var catalog = Catalog();
        var endpoint = catalog.Get("54");

        Assert.AreEqual(BiyingSegmentKind.Licence, endpoint.Segments[^1].Kind);
        Assert.AreEqual(1, endpoint.Segments.Count(segment => segment.Kind == BiyingSegmentKind.Licence));
        Assert.AreEqual(1, endpoint.PlaceholderCount);
    }
}
