using System.Net;
using System.Text;
using PuddingFinance.Collection;

namespace PuddingFinanceTests;

/// <summary>
/// 巨潮公告解析与表单编码的回归。
///
/// JSON 形状取自 2026-10-26 的真实回执（POST 200，totalAnnouncement=265,744），
/// 只裁剪了与断言无关的字段；<c>classifiedAnnouncements: null</c> 这种真实存在的空值必须能扛住。
/// </summary>
[TestClass]
public sealed class CninfoAnnouncementTests
{
    private const string RealisticPayload = """
        {"classifiedAnnouncements":null,"totalSecurities":0,"totalAnnouncement":265744,
         "totalRecordNum":265744,"announcements":[
          {"id":null,"secCode":"688585","secName":"上纬新材","orgId":"9900042956",
           "announcementId":"1225626961","announcementTitle":"股票交易异常波动公告",
           "announcementTime":1735689600000,"adjunctUrl":"finalpage/2026-10-27/1225626961.PDF",
           "adjunctSize":114,"adjunctType":"PDF","storageTime":null}]}
        """;

    [TestMethod]
    public void Parse_RealShapedPayload_ReadsAnnouncementAndPdfUrl()
    {
        var page = CninfoAnnouncementSource.Parse(
            RealisticPayload,
            new CninfoAnnouncementQuery(SearchKey: "688585"));

        Assert.AreEqual(265744, page.Total);
        Assert.HasCount(1, page.Items);

        var item = page.Items[0];
        Assert.AreEqual("688585", item.Code);
        Assert.AreEqual("上纬新材", item.Name);
        Assert.AreEqual("1225626961", item.AnnouncementId);
        Assert.AreEqual("股票交易异常波动公告", item.Title);
        Assert.AreEqual("http://static.cninfo.com.cn/finalpage/2026-10-27/1225626961.PDF", item.PdfUrl);
        Assert.AreEqual(114, item.SizeKb);
    }

    [TestMethod]
    public void Parse_Timestamp_IsInterpretedAsChinaStandardTime()
    {
        var page = CninfoAnnouncementSource.Parse(
            RealisticPayload,
            new CninfoAnnouncementQuery());

        var publishedAt = page.Items[0].PublishedAt;
        Assert.IsNotNull(publishedAt);

        // 1735689600000 = 2025-01-01T00:00:00Z，东八区即当日 08:00；
        // 断言时区而不是"就地重算"，避免用被测代码验证被测代码。
        Assert.AreEqual(TimeSpan.FromHours(8), publishedAt.Value.Offset);
        Assert.AreEqual(new DateTime(2025, 1, 1), publishedAt.Value.Date);
    }

    [TestMethod]
    public void Parse_NullAnnouncements_YieldsEmptyPage()
    {
        var page = CninfoAnnouncementSource.Parse(
            """{"totalRecordNum":0,"announcements":null}""",
            new CninfoAnnouncementQuery());

        Assert.AreEqual(0, page.Total);
        Assert.IsEmpty(page.Items);
    }

    [TestMethod]
    public void Parse_NonJsonBody_ThrowsParseException()
    {
        var error = Assert.ThrowsExactly<CninfoParseException>(
            () => CninfoAnnouncementSource.Parse("<html>blocked</html>", new CninfoAnnouncementQuery()));

        StringAssert.Contains(error.Message, "不是 JSON");
    }

    [TestMethod]
    public void BuildForm_PutsPagingAndSearchKeyInPlace()
    {
        var form = CninfoAnnouncementSource.BuildForm(new CninfoAnnouncementQuery(
            SearchKey: "601398",
            PageNumber: 2,
            PageSize: 50));

        StringAssert.Contains(form, "pageNum=2");
        StringAssert.Contains(form, "pageSize=50");
        StringAssert.Contains(form, "searchkey=601398");
        StringAssert.Contains(form, "isHLtitle=false");
        StringAssert.Contains(form, "column=szse");
    }

    [TestMethod]
    public void BuildForm_EscapesChineseSearchKey()
    {
        var form = CninfoAnnouncementSource.BuildForm(
            new CninfoAnnouncementQuery(SearchKey: "年报"));

        // 中文必须百分号编码：未编码的表单会被服务端当成乱码键值。
        StringAssert.Contains(form, "searchkey=%E5%B9%B4%E6%8A%A5");
    }

    [TestMethod]
    public void BuildForm_PageSizeOutOfRange_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CninfoAnnouncementSource.BuildForm(new CninfoAnnouncementQuery(PageSize: 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CninfoAnnouncementSource.BuildForm(new CninfoAnnouncementQuery(PageSize: 101)));
    }

    [TestMethod]
    public async Task QueryAsync_PostsFormAndParsesResponse()
    {
        var handler = new RecordingHandler(RealisticPayload);
        using var http = new HttpClient(handler);
        var source = new CninfoAnnouncementSource(new OfficialSourceFetcher(http));

        var page = await source.QueryAsync(new CninfoAnnouncementQuery(SearchKey: "688585"));

        Assert.AreEqual(1, handler.CallCount);
        Assert.AreEqual("POST", handler.Method);
        Assert.AreEqual("application/x-www-form-urlencoded", handler.ContentType);
        StringAssert.Contains(handler.Body!, "searchkey=688585");
        Assert.HasCount(1, page.Items);
    }

    [TestMethod]
    public void Parse_SearchKeyQuery_WarnsThatTotalIsFullTextScope()
    {
        var page = CninfoAnnouncementSource.Parse(
            RealisticPayload,
            new CninfoAnnouncementQuery(SearchKey: "601398"));

        Assert.IsNotNull(page.Caveat, "全文检索口径的 Total 不能不带提醒地返回");
        StringAssert.Contains(page.Caveat!, "不等于该公司的公告数");
    }

    [TestMethod]
    public void Parse_StockQueryWithZeroTotal_WarnsAboutPossibleBadOrgId()
    {
        // 实测：传错 orgId 时服务端回 200 + total=0，不报错。
        // 这种“静默空结果”必须带上口径提醒，否则会被误读成“该公司没有公告”。
        var page = CninfoAnnouncementSource.Parse(
            """{"totalRecordNum":0,"announcements":[]}""",
            new CninfoAnnouncementQuery(Stock: "601398,not-a-real-orgid"));

        Assert.IsNotNull(page.Caveat);
        StringAssert.Contains(page.Caveat!, "orgId");
    }

    [TestMethod]
    public void Parse_StockQueryWithResults_HasNoCaveat()
    {
        var page = CninfoAnnouncementSource.Parse(
            RealisticPayload,
            new CninfoAnnouncementQuery(Stock: "688585,gssh0600585"));

        Assert.IsNull(page.Caveat, "口径正确的回执不该拖一条提醒");
    }

    [TestMethod]
    public void BuildForm_StockWithoutOrgId_Throws()
    {
        var error = Assert.ThrowsExactly<ArgumentException>(
            () => CninfoAnnouncementSource.BuildForm(new CninfoAnnouncementQuery(Stock: "601398")));

        StringAssert.Contains(error.Message, "orgId");
    }

    private sealed class RecordingHandler(string payload) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public string? Method { get; private set; }

        public string? Body { get; private set; }

        public string? ContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Method = request.Method.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            ContentType = request.Content?.Headers.ContentType?.MediaType;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }
}
