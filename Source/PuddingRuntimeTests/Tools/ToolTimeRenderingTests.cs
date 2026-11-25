using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 工具输出里的时间渲染：<b>本地时区 + 必须带日期</b>。
/// <para>
/// <b>缺陷现场（2026-11-25 同一轮内对照出来的）：</b>同一个子会话 <c>sub-31461569</c>，
/// <c>pool_action="list"</c> 显示 <c>2026-11-25 13:36</c>，而 <c>query_sub_agents</c>
/// 显示 <c>创建: 05:36:13</c> —— 差 8 小时，且后者没有日期。两个工具给出两个答案时，
/// 用户没有任何办法判断哪个对。
/// </para>
/// <para>
/// 这组用例钉的是「口径一致」，不是「格式好看」：把转换或日期改回去必须变红。
/// </para>
/// </summary>
[TestClass]
public sealed class ToolTimeRenderingTests
{
    // 现场真实值：frontend-ui 的 last_used_at 是 2026-11-21T04:26:17Z（本机本地时间 12:26）。
    private static readonly DateTimeOffset UtcSample =
        new(2026, 11, 21, 4, 26, 17, TimeSpan.Zero);

    private static readonly Func<DateTimeOffset, DateTimeOffset> Plus8 =
        static t => t.ToOffset(TimeSpan.FromHours(8));

    [TestMethod]
    public void RenderMinute_UtcValue_RendersLocalDateAndClock()
    {
        var rendered = ToolTimeRendering.RenderMinute(UtcSample, Plus8);

        Assert.AreEqual("2026-11-21 12:26", rendered);
        // 旧缺陷形态：只有时分秒，而且那是 UTC 时刻。
        Assert.IsFalse(rendered.Contains("04:26", StringComparison.Ordinal), $"仍输出 UTC 时刻：{rendered}");
    }

    [TestMethod]
    public void RenderMinute_CrossDayUtc_RendersNextLocalDate()
    {
        // 这条钉的是「日期必须跟着转换走」：UTC 是 25 日，本地已经是 26 日。
        // 若先取 UTC 日期再拼本地时刻，就会渲染成 2026-11-25 01:30 —— 错一天，且看不出错。
        var utc = new DateTimeOffset(2026, 11, 25, 17, 30, 0, TimeSpan.Zero);

        Assert.AreEqual("2026-11-26 01:30", ToolTimeRendering.RenderMinute(utc, Plus8));
    }

    [TestMethod]
    public void RenderSecond_KeepsSecondPrecision_WhileMinuteTruncates()
    {
        // 两种精度共享同一套转换与格式不变量，只差精度。查单个条目时秒有意义。
        Assert.AreEqual("2026-11-25 12:26:17", ToolTimeRendering.RenderSecond(UtcSample.AddDays(4), Plus8));
        Assert.AreEqual("2026-11-25 12:26", ToolTimeRendering.RenderMinute(UtcSample.AddDays(4), Plus8));
    }

    [TestMethod]
    public void Render_NullOrDefault_RendersDash_ButValidValueStillRenders()
    {
        // CompletedAt 为空是常态（还在跑），它不能渲染成 0001-01-01。
        Assert.AreEqual("-", ToolTimeRendering.RenderMinute(null, Plus8));
        Assert.AreEqual("-", ToolTimeRendering.RenderSecond(null, Plus8));
        Assert.AreEqual("-", ToolTimeRendering.RenderMinute(default, Plus8));

        // 负向不能靠「恒返回 -」通过：有效值必须仍被渲染。
        Assert.AreEqual("2026-11-21 12:26", ToolTimeRendering.RenderMinute(UtcSample, Plus8));
    }

    [TestMethod]
    public void Render_AlreadyLocalOffset_IsNotShiftedTwice()
    {
        // 旧行可能本来就带 +08:00（历史数据混写）。ToLocalTime 按绝对时刻归一，
        // 墙钟不该再被挪一次 —— 否则修了 UTC 假象，引入「本地值被当 UTC 再 +8」的新错。
        var local = new DateTimeOffset(2026, 11, 25, 9, 7, 5, TimeSpan.FromHours(8));

        Assert.AreEqual("2026-11-25 09:07", ToolTimeRendering.RenderMinute(local, Plus8));
    }

    [TestMethod]
    public void Render_Format_IsCultureInvariant()
    {
        var thai = CultureInfo.GetCultureInfo("th-TH");
        // 正向对照：先证明这个区域真的会让 `yyyy` 变成佛历年份，否则下面的断言是空转。
        // 仓库若开了 InvariantGlobalization，th-TH 会退化成公历 —— 那时本用例无从取证。
        if (new DateTime(2026, 11, 21).ToString("yyyy", thai) == "2026")
            Assert.Inconclusive("当前运行时不支持 th-TH 的佛历日历，本用例无法取证");

        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = thai;
            Assert.AreEqual("2026-11-21 12:26", ToolTimeRendering.RenderMinute(UtcSample, Plus8));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [TestMethod]
    public async Task QuerySubAgents_List_RendersLocalAbsoluteTime()
    {
        // 接线：池 list 已改好，而 query_sub_agents 仍是 HH:mm:ss —— 同一个 run 两个答案。
        // 这条钉的是调用点，把 list 行改回 `{sa.SpawnedAt:HH:mm:ss}` 必须变红。
        var output = await RunQueryAsync("list");

        StringAssert.Contains(output, LocalAbsolute(UtcSample, "yyyy-MM-dd HH:mm"),
            $"list 必须给本地绝对时间：{output}");
        Assert.IsFalse(output.Contains("04:26:17", StringComparison.Ordinal),
            $"仍输出 UTC 时刻：{output}");
        // 只给时分秒时「四天前」与「今天凌晨」不可区分，所以日期必须在。
        Assert.IsTrue(Regex.IsMatch(output, @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}"), $"没有日期：{output}");
    }

    [TestMethod]
    public async Task QuerySubAgents_Status_RendersSecondPrecisionLocal()
    {
        var output = await RunQueryAsync("status", subAgentId: "sess-parent-sub-31461569");

        StringAssert.Contains(output, LocalAbsolute(UtcSample, "yyyy-MM-dd HH:mm:ss"),
            $"status 必须给本地绝对时间（含秒）：{output}");
        Assert.IsFalse(output.Contains("04:26:17", StringComparison.Ordinal),
            $"仍输出 UTC 时刻：{output}");
    }

    // ===== 辅助 =====

    /// <summary>
    /// 期望值按<b>平台原语</b> <see cref="DateTimeOffset.ToLocalTime"/> 计算，不复刻被测代码的格式串选择。
    /// 机器时区为 UTC 时「转本地」无从取证，明确判为无可取证而不是静默通过。
    /// </summary>
    private static string LocalAbsolute(DateTimeOffset utc, string format)
    {
        if (TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.Zero)
            Assert.Inconclusive("机器时区是 UTC，转换与不转换在产物上不可区分");

        return utc.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);
    }

    private static async Task<string> RunQueryAsync(string action, string? subAgentId = null)
    {
        var status = new SubAgentStatus
        {
            RunId = "run-1",
            ParentSessionId = "sess-parent",
            SubSessionId = "sess-parent-sub-31461569",
            Status = "completed",
            TemplateId = "workspace-task-agent",
            TaskSummary = "probe",
            SpawnedAt = UtcSample,
            CompletedAt = UtcSample.AddSeconds(4),
        };

        var manager = new Mock<ISubAgentManager>(MockBehavior.Strict);
        manager.Setup(m => m.GetSubAgentsAsync(
                It.IsAny<string>(), It.IsAny<SubAgentQueryFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SubAgentStatus> { status });
        manager.Setup(m => m.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var tool = new QuerySubAgentsTool(manager.Object, NullLogger<QuerySubAgentsTool>.Instance);
        var args = subAgentId is null
            ? $"{{\"action\":\"{action}\"}}"
            : $"{{\"action\":\"{action}\",\"subAgentId\":\"{subAgentId}\"}}";

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-time-rendering",
            ArgumentsJson = args,
            Context = new ToolExecutionContext
            {
                AgentInstanceId = "agent_parent",
                WorkspaceId = "workspace_1",
                SessionId = "sess-parent",
            },
        });

        Assert.IsTrue(result.Success, $"工具调用失败：{result.Error}");
        return result.Output ?? string.Empty;
    }
}
