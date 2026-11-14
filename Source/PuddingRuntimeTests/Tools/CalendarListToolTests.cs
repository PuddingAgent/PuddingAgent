using PuddingRuntime.Services.Tools;
using System.Xml.Linq;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// calendar_list 的纯解析与参数契约：发现三跳、事件解析、凭据不外泄、时间范围归一。
/// <para>全部离线：没有网络、没有真密码。形状取自 2026-11-14 钉钉 CalDAV 的真实响应（已裁剪）。</para>
/// </summary>
[TestClass]
public sealed class CalendarListToolTests
{
    private const string FakePassword = "APPPASS-SHOULD-NOT-LEAK";

    private const string PrincipalXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <D:multistatus xmlns:D="DAV:"><D:response><D:href>/dav/principals/</D:href><D:propstat><D:prop>
        <D:current-user-principal><D:href>/dav/users/u_test</D:href></D:current-user-principal>
        <D:resourcetype><D:collection/></D:resourcetype>
        </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>
        """;

    private const string HomeXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav"><D:response><D:href>/dav/users/u_test</D:href><D:propstat><D:prop>
        <C:calendar-home-set><D:href>/dav/u_test</D:href></C:calendar-home-set>
        <D:displayname>Calendar home set for user Test</D:displayname>
        </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>
        """;

    /// <summary>真实形状：Inbox / Outbox 也是 collection，只有 primary 带 calendar。</summary>
    private const string HomeListingXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
        <D:response><D:href>/dav/u_test/</D:href><D:propstat><D:prop>
        <D:displayname>Calendar home set for user Test</D:displayname>
        <D:resourcetype><D:collection/></D:resourcetype>
        </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
        <D:response><D:href>/dav/u_test/Inbox/</D:href><D:propstat><D:prop>
        <D:displayname>Inbox</D:displayname>
        <D:resourcetype><D:collection/><C:schedule-inbox/></D:resourcetype>
        </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
        <D:response><D:href>/dav/u_test/Outbox/</D:href><D:propstat><D:prop>
        <D:displayname>Outbox</D:displayname>
        <D:resourcetype><C:schedule-outbox/><D:collection/></D:resourcetype>
        </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
        <D:response><D:href>/dav/u_test/primary/</D:href><D:propstat><D:prop>
        <D:displayname>我的日历（钉钉）</D:displayname>
        <D:resourcetype><C:calendar/><D:collection/></D:resourcetype>
        </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
        </D:multistatus>
        """;

    private const string QueryXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
        <D:response><D:href>/dav/u_test/primary/a1</D:href><D:propstat><D:prop>
        <C:calendar-data>BEGIN:VCALENDAR&#13;
        BEGIN:VEVENT&#13;
        SUMMARY:出差中&#13;
        DTSTART;TZID=Asia/Shanghai:20261108T120000&#13;
        DTEND;TZID=Asia/Shanghai:20261113T235900&#13;
        END:VEVENT&#13;
        END:VCALENDAR&#13;
        </C:calendar-data>
        </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
        <D:response><D:href>/dav/u_test/primary/a2</D:href><D:propstat><D:prop>
        <C:calendar-data>BEGIN:VCALENDAR&#13;
        BEGIN:VEVENT&#13;
        SUMMARY:SUMMARYX 不是 SUMMARY 的截断&#13;
        DTSTART:20261104T000000Z&#13;
        DTEND:20261105T235900Z&#13;
        END:VEVENT&#13;
        END:VCALENDAR&#13;
        </C:calendar-data>
        </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
        </D:multistatus>
        """;

    private const string EmptyQueryXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <D:multistatus xmlns:D="DAV:"/>
        """;

    [TestMethod]
    public void Discovery_ParsesPrincipal_Home_AndPicksTheCalendarNotInbox()
    {
        Assert.AreEqual("/dav/users/u_test", CalDavXml.CurrentUserPrincipal(XDocument.Parse(PrincipalXml)));
        Assert.AreEqual("/dav/u_test", CalDavXml.CalendarHomeSet(XDocument.Parse(HomeXml)));

        var listing = XDocument.Parse(HomeListingXml);
        var href = CalDavXml.FirstCalendarHref(listing);
        Assert.AreEqual("/dav/u_test/primary/", href, "Inbox/Outbox 也是 collection，但不是日历；选错会导致 REPORT 永远空。");
        Assert.AreEqual("我的日历（钉钉）", CalDavXml.CalendarDisplayName(listing, href!));
    }

    [TestMethod]
    public void Query_ParsesEventsWithTzidAndPlainUtc()
    {
        var events = CalDavXml.ParseCalendarQuery(XDocument.Parse(QueryXml));

        Assert.HasCount(2, events);
        Assert.AreEqual("出差中", events[0].Summary);
        Assert.AreEqual("20261108T120000", events[0].Start);
        Assert.AreEqual("20261113T235900", events[0].End);
        Assert.AreEqual("Asia/Shanghai", events[0].TimeZone);

        // 无 TZID 时不得把时区编出来。
        Assert.AreEqual("unknown", events[1].TimeZone);
        Assert.AreEqual("20261104T000000Z", events[1].Start);
    }

    /// <summary>空 multistatus 是「这个区间没有日程」，不是错误。</summary>
    [TestMethod]
    public void Query_EmptyMultistatus_IsEmptyListNotFailure()
    {
        var events = CalDavXml.ParseCalendarQuery(XDocument.Parse(EmptyQueryXml));
        Assert.IsEmpty(events);
    }

    [TestMethod]
    public void Endpoint_ResolvesPassword_OrEnvironmentRef_AndNeverLeaksIt()
    {
        var inline = $$"""
            {"server":"https://calendar.example.test/","user":"u_test","password":"{{FakePassword}}"}
            """;
        Assert.IsTrue(CalendarListTool.TryResolveEndpoint(inline, out var endpoint, out var error), error);
        Assert.AreEqual("https://calendar.example.test", endpoint.Server, "尾部斜杠要归一，否则拼接会出 //。");
        Assert.AreEqual(FakePassword, endpoint.Password);

        var refName = "PUDDING_TEST_CALDAV_PASSWORD";
        Environment.SetEnvironmentVariable(refName, FakePassword);
        try
        {
            var byRef = $$"""
                {"server":"https://calendar.example.test","user":"u_test","passwordRef":"{{refName}}"}
                """;
            Assert.IsTrue(CalendarListTool.TryResolveEndpoint(byRef, out var viaRef, out var refError), refError);
            Assert.AreEqual(FakePassword, viaRef.Password);
        }
        finally
        {
            Environment.SetEnvironmentVariable(refName, null);
        }
    }

    [TestMethod]
    public void Endpoint_MissingCredentials_FailsClosedWithoutEchoingSecrets()
    {
        Assert.IsFalse(CalendarListTool.TryResolveEndpoint(
            """{"server":"https://x.test","user":"u"}""", out _, out var noSecret));
        StringAssert.Contains(noSecret, "password");

        Assert.IsFalse(CalendarListTool.TryResolveEndpoint(
            """{"server":"https://x.test"}""", out _, out var noUser));
        StringAssert.Contains(noUser, "user");

        Assert.IsFalse(CalendarListTool.TryResolveEndpoint(
            """{"user":"u","password":"P"}""", out _, out var noServer));
        StringAssert.Contains(noServer, "server");

        // 指向不存在的环境变量：必须明确失败，且错误里不能把变量名当成密码打印。
        var missingRef = """
            {"server":"https://x.test","user":"u","passwordRef":"PUDDING_ABSENT_VAR"}
            """;
        Assert.IsFalse(CalendarListTool.TryResolveEndpoint(missingRef, out _, out var missing));
        StringAssert.Contains(missing, "PUDDING_ABSENT_VAR");
    }

    [TestMethod]
    public void Range_DefaultsToSevenDays_AndConvertsToUtc()
    {
        Assert.IsTrue(CalendarListTool.TryResolveRange(
            new CalendarListArgs(), out var from, out var to, out var error), error);

        Assert.AreEqual('Z', from[^1], "CalDAV 的 time-range 要 UTC。");
        Assert.AreEqual('Z', to[^1]);
        Assert.IsTrue(string.CompareOrdinal(from, to) < 0);
    }

    [TestMethod]
    public void Range_RejectsInvalidOrInvertedInput()
    {
        Assert.IsFalse(CalendarListTool.TryResolveRange(
            new CalendarListArgs { From = "not-a-date" }, out _, out _, out var badFrom));
        StringAssert.Contains(badFrom, "from");

        Assert.IsFalse(CalendarListTool.TryResolveRange(
            new CalendarListArgs { From = "2026-11-20", To = "2026-11-01" }, out _, out _, out var inverted));
        StringAssert.Contains(inverted, "结束早于开始");
    }

    /// <summary>工具必须能被 [Tool] 特性发现，否则「实现了但永远不加载」。</summary>
    [TestMethod]
    public void Tool_Is_Discoverable_By_Attribute()
    {
        var attribute = typeof(CalendarListTool)
            .GetCustomAttributes(typeof(PuddingCode.Tools.ToolAttribute), inherit: false);

        Assert.HasCount(1, attribute, "calendar_list 需要 [Tool] 特性才会被 AddPuddingToolsFromAssembly 注册。");
    }
}
