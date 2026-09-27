using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-13 runtime node slice: status/heartbeat host derivation, and the reason-is-audit-only rule.</summary>
public sealed class RuntimeNodeContractTests
{
    private static RuntimeNode Node(string status = "Online", bool frozen = false, int sessions = 0,
        string endpoint = "http://localhost:5100") =>
        new("node-1", endpoint, status, DateTimeOffset.UtcNow, sessions, true, "DemoDesktopApp", frozen, []);

    [Fact]
    public void SummaryCountsEveryStateTheCardAsKSFor()
    {
        var nodes = new[]
        {
            Node("Online", sessions: 2),
            Node("Online", frozen: true, sessions: 1),
            Node("Degraded"),
            Node("Offline"),
        };
        var summary = RuntimeNodeSummary.Of(nodes);
        Assert.Equal(4, summary.Total);
        Assert.Equal(2, summary.Online);
        Assert.Equal(1, summary.Degraded);
        Assert.Equal(1, summary.Offline);
        Assert.Equal(3, summary.ActiveSessions);
        Assert.Equal(1, summary.Frozen);
        Assert.Equal(4, summary.Embedded);
        Assert.Contains("在线 2", summary.HeadlineText, StringComparison.Ordinal);
        Assert.Contains("已冻结 1", summary.ExtraText, StringComparison.Ordinal);
        Assert.Equal(0, RuntimeNodeSummary.Empty.Total);
    }

    [Fact]
    public void StatusVocabularyIsTranslatedAndUnknownValuesPassThrough()
    {
        Assert.Equal(["Online", "Degraded", "Offline"], RuntimeNodeText.Statuses);
        Assert.Equal("在线", RuntimeNodeText.DescribeStatus("online"));
        Assert.Equal("降级", RuntimeNodeText.DescribeStatus("Degraded"));
        Assert.Equal("离线", RuntimeNodeText.DescribeStatus("Offline"));
        Assert.Equal("状态未知", RuntimeNodeText.DescribeStatus(null));
        Assert.Equal("Maintenance", RuntimeNodeText.DescribeStatus("Maintenance"));
    }

    [Fact]
    public void HostIsDerivedFromTheEndpointRatherThanInvented()
    {
        // Core 的节点模型只有 Endpoint，没有 host/IP 字段。
        Assert.Equal("localhost:5100", RuntimeNodeText.HostOf("http://localhost:5100"));
        Assert.Equal("10.0.0.7:8080", RuntimeNodeText.HostOf("https://10.0.0.7:8080/api"));
        Assert.Equal("未上报端点", RuntimeNodeText.HostOf(""));
        // 解析不了就原样显示，不猜地址。
        Assert.Equal("not a uri", RuntimeNodeText.HostOf("not a uri"));
        Assert.Equal("localhost:5100", Node().HostText);
    }

    [Fact]
    public void HeartbeatAgeIsRelativeAndAFutureClockIsCalledOut()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("30 秒前心跳", RuntimeNodeText.DescribeHeartbeat(now.AddSeconds(-30), now));
        Assert.Equal("5 分钟前心跳", RuntimeNodeText.DescribeHeartbeat(now.AddMinutes(-5), now));
        Assert.Equal("3 小时前心跳", RuntimeNodeText.DescribeHeartbeat(now.AddHours(-3), now));
        Assert.Equal("2 天前心跳", RuntimeNodeText.DescribeHeartbeat(now.AddDays(-2), now));
        // 时钟不同步时不假装「刚刚」。
        Assert.Contains("未来", RuntimeNodeText.DescribeHeartbeat(now.AddMinutes(5), now), StringComparison.Ordinal);

        Assert.True(RuntimeNodeText.IsStale(now.AddMinutes(-10), now, TimeSpan.FromMinutes(5)));
        Assert.False(RuntimeNodeText.IsStale(now.AddMinutes(-1), now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void FreezeRequestsRequireANodeAndAReason()
    {
        Assert.Empty(RuntimeNodeText.Validate("node-1", "maintenance"));
        Assert.Contains("选择节点", RuntimeNodeText.Validate(" ", "r").Single(), StringComparison.Ordinal);
        // 原因会写进审计 Detail；空原因会让这条轨迹失去意义。
        Assert.Contains("必须填写原因", RuntimeNodeText.Validate("node-1", "  ").Single(), StringComparison.Ordinal);

        Assert.Contains("审计记录", RuntimeNodeText.ReasonNotice, StringComparison.Ordinal);
        Assert.Contains("没有原因字段", RuntimeNodeText.ReasonNotice, StringComparison.Ordinal);
        Assert.Contains("没有单独的 host/IP", RuntimeNodeText.HostNotice, StringComparison.Ordinal);
        Assert.Contains("拒绝所有原生能力调用", RuntimeNodeText.FreezeEffectNotice, StringComparison.Ordinal);
        Assert.Contains("只读查询", RuntimeNodeText.DescribeCapabilityCategory("QueryState"), StringComparison.Ordinal);
        Assert.Equal("Custom（自定义）", RuntimeNodeText.DescribeCapabilityCategory("Custom"));
    }

    [Fact]
    public void NodeAndCapabilityDisplaysUseCoreFieldsOnly()
    {
        var node = Node(status: "Degraded", frozen: true) with
        {
            Capabilities = [new RuntimeNodeCapability("demo.query", "Query", "d", "QueryState", true)]
        };
        Assert.Equal("降级", node.StatusText);
        Assert.Equal("嵌入（桌面宿主）", node.ModeText);
        Assert.False(node.IsOnline);
        Assert.True((node with { Status = "Online" }).IsOnline);
        Assert.Contains("需审批", node.Capabilities[0].Display, StringComparison.Ordinal);
        Assert.Contains("只读查询", node.Capabilities[0].CategoryText, StringComparison.Ordinal);
        Assert.False((node with { EmbeddedMode = false }).ModeText.StartsWith("嵌入", StringComparison.Ordinal));
    }
}