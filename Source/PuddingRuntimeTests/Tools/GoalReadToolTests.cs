using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using PuddingCode.Configuration;
using PuddingCode.Goals;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class GoalReadToolTests
{
    [TestMethod]
    public void ReadWithSizeLimit_SmallFile_ReturnsFullContent()
    {
        // Arrange: create a small temp file (< 16KB)
        var tmpFile = Path.GetTempFileName();
        try
        {
            var content = "line1\nline2\nline3\n";
            File.WriteAllText(tmpFile, content, Encoding.UTF8);

            // Act
            var (result, truncated) = GoalReadTool.ReadWithSizeLimit(tmpFile);

            // Assert
            Assert.IsFalse(truncated);
            Assert.AreEqual(content, result);
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    [TestMethod]
    public void ReadWithSizeLimit_ExceedsLimit_ReturnsTruncatedWithWarning()
    {
        // Arrange: create a file > 16KB
        var tmpFile = Path.GetTempFileName();
        try
        {
            var sb = new StringBuilder();
            // Write ~20KB of content with identifiable start lines
            sb.AppendLine("--- BEGIN OLD CONTENT ---");
            for (var i = 1; i <= 400; i++)
                sb.AppendLine($"line-{i:D4}: some padding content to fill the file beyond 16KB limit");
            sb.AppendLine("--- MARKER: this is near the end ---");
            sb.AppendLine("final-line-1");
            sb.AppendLine("final-line-2");
            var fullContent = sb.ToString();
            File.WriteAllText(tmpFile, fullContent, Encoding.UTF8);

            // Act
            var (result, truncated) = GoalReadTool.ReadWithSizeLimit(tmpFile);

            // Assert
            Assert.IsTrue(truncated);
            // Should contain the warning header
            StringAssert.Contains(result, "超过读取上限");
            StringAssert.Contains(result, "goal_update");
            // Should contain the recent content
            StringAssert.Contains(result, "final-line-1");
            StringAssert.Contains(result, "final-line-2");
            // Should NOT contain the very old content (trimmed)
            Assert.IsFalse(result.Contains("--- BEGIN OLD CONTENT ---"));
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    [TestMethod]
    public void ReadWithSizeLimit_ExceedsLimit_TruncatesAtNewlineBoundary()
    {
        // Arrange: create a file just over 16KB
        var tmpFile = Path.GetTempFileName();
        try
        {
            var sb = new StringBuilder();
            for (var i = 0; i < 200; i++)
                sb.AppendLine($"padding-{i:D4}: " + new string('x', 70));
            sb.AppendLine("MARKER-KEPT-LINE-1");
            sb.AppendLine("MARKER-KEPT-LINE-2");
            var fullContent = sb.ToString();
            File.WriteAllText(tmpFile, fullContent, Encoding.UTF8);

            // Act
            var (result, truncated) = GoalReadTool.ReadWithSizeLimit(tmpFile);

            // Assert
            Assert.IsTrue(truncated);
            // The first character after the warning should not be a mid-line fragment;
            // our implementation skips to after the first newline in the tail.
            // So MARKER-KEPT-LINE-1 and MARKER-KEPT-LINE-2 should be present
            StringAssert.Contains(result, "MARKER-KEPT-LINE-1");
            StringAssert.Contains(result, "MARKER-KEPT-LINE-2");
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    [TestMethod]
    public void ReadWithSizeLimit_ChineseUtf8_DoesNotSplitMultibyte()
    {
        // Arrange: create a file with Chinese characters near the truncation boundary
        var tmpFile = Path.GetTempFileName();
        try
        {
            var sb = new StringBuilder();
            for (var i = 0; i < 200; i++)
                sb.AppendLine($"填充行-{i:D4}: " + new string('x', 70));
            sb.AppendLine("目标：这是一行包含中文的标记行");
            sb.AppendLine("最终行-中文-测试");
            var fullContent = sb.ToString();
            File.WriteAllText(tmpFile, fullContent, Encoding.UTF8);

            // Act
            var (result, truncated) = GoalReadTool.ReadWithSizeLimit(tmpFile);

            // Assert
            Assert.IsTrue(truncated);
            // The result should be valid UTF-8 (no replacement characters from split bytes)
            StringAssert.Contains(result, "最终行-中文-测试");
            // Verify no orphaned replacement chars at start
            Assert.IsFalse(result.StartsWith("\uFFFD"));
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    [TestMethod]
    public void ReadWithSizeLimit_UnderLimit_ReturnsFullContent()
    {
        // Arrange: create a file well under ReadLimitBytes (16KB)
        var tmpFile = Path.GetTempFileName();
        try
        {
            var content = new string('a', GoalReadTool.ReadLimitBytes - 10);
            File.WriteAllText(tmpFile, content, Encoding.UTF8);

            // Act
            var (result, truncated) = GoalReadTool.ReadWithSizeLimit(tmpFile);

            // Assert
            Assert.IsFalse(truncated, "File under limit should not be truncated");
            StringAssert.Contains(result, content[..10]);
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    /// <summary>
    /// 结构化 GoalRun 必须自报“我是事实来源”，并把 phase / 轮次 / revision / blocker 一次给全；
    /// 否则模型只能靠 goal.md 里的自由文本推测状态——那正是当日误判的成因。
    /// </summary>
    [TestMethod]
    public void FormatGoalRun_DeclaresAuthorityAndCarriesState()
    {
        var snapshot = new PuddingCode.Goals.GoalSnapshot
        {
            GoalRunId = "gr-77",
            WorkspaceId = "default",
            ConversationId = "conv-1",
            AgentInstanceId = "agent-1",
            Objective = "ship it",
            ObjectiveVersion = 2,
            Phase = PuddingCode.Goals.GoalPhase.Blocked,
            BlockedCode = "budget_exhausted",
            BlockedMessage = "no rounds left",
            MaxIterations = 12,
            IterationsStarted = 5,
            IterationsSettled = 4,
            ActivationEpoch = 3,
            AggregateVersion = 7,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            UpdatedAtUtc = DateTimeOffset.UnixEpoch,
        };

        var text = GoalReadTool.FormatGoalRun(snapshot);

        StringAssert.Contains(text, "权威");
        StringAssert.Contains(text, "id=gr-77");
        StringAssert.Contains(text, "phase=Blocked");
        StringAssert.Contains(text, "轮次=5/12");
        StringAssert.Contains(text, "revision=7");
        StringAssert.Contains(text, "budget_exhausted: no rounds left");
        StringAssert.Contains(text, "不是事实来源");
    }

    [TestMethod]
    public void FormatGoalRun_WithoutBlocker_RendersDashNotEmptySlot()
    {
        var snapshot = new PuddingCode.Goals.GoalSnapshot
        {
            GoalRunId = "gr-78",
            WorkspaceId = "default",
            ConversationId = "conv-1",
            AgentInstanceId = "agent-1",
            Objective = "ship it",
            ObjectiveVersion = 1,
            Phase = PuddingCode.Goals.GoalPhase.Active,
            MaxIterations = 8,
            IterationsStarted = 0,
            IterationsSettled = 0,
            ActivationEpoch = 1,
            AggregateVersion = 1,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            UpdatedAtUtc = DateTimeOffset.UnixEpoch,
        };

        var text = GoalReadTool.FormatGoalRun(snapshot);

        StringAssert.Contains(text, "blocker=-");
        StringAssert.Contains(text, "next=-");
    }

    /// <summary>
    /// 复刻宿主真实注册形态：<c>IGoalQueryService</c> 是 Scoped，而工具由扫描器登记为 Singleton。
    /// </summary>
    /// <remarks>
    /// 2026-11-17 事故：把 Scoped 服务直接写进 Singleton 工具的构造函数。单元测试全绿（测试里是
    /// <c>new GoalReadTool(...)</c>，从不经过容器校验），宿主 <c>ValidateOnBuild</c> 却抛
    /// <c>AggregateException: Cannot consume scoped service ... from singleton</c>（exit -532462766），
    /// Guardian 等了 240 秒健康检查后判部署失败并回滚。
    /// 这条测试用与宿主相同的校验选项把该错误提前到 CI，并要求工具改为在调用内自建作用域。
    /// </remarks>
    [TestMethod]
    public void GoalReadTool_ResolvesFromHostLikeContainer_WhenGoalQueryIsScoped()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-goalread-composition", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var goalQuery = new Mock<IGoalQueryService>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PuddingDataPaths { DataRoot = root });
        // Scoped 注册正是事故的一半；另一半是工具被登记为 Singleton。
        services.AddScoped(_ => goalQuery.Object);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton(typeof(IPuddingTool), typeof(GoalReadTool)));

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var tools = provider.GetServices<IPuddingTool>().ToList();

        Assert.IsTrue(
            tools.OfType<GoalReadTool>().Any(),
            "GoalReadTool 必须能从宿主同款容器解析；解析失败即宿主启动期崩溃。");
    }
}
