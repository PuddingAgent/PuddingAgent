using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Observability;
using Serilog.Events;

namespace PuddingCoreTests.Observability;

/// <summary>
/// 日志基础设施的地基（本轮新增）：**运行时可切换级别** 与 **每行显式 tag**。
///
/// 这两件事此前缺失：级别只在启动时读环境变量且只支持两档（Debug/Information），
/// 行内没有 tag（多个组件日志拼起来看时分不清来源）。测试钉住的是"解析与推导"这两个纯函数，
/// 而不去断言 Serilog 内部行为。
/// </summary>
[TestClass]
public sealed class LoggingFoundationTests
{
    [DataTestMethod]
    [DataRow("verbose", LogEventLevel.Verbose)]
    [DataRow("trace", LogEventLevel.Verbose)]
    [DataRow("Debug", LogEventLevel.Debug)]
    [DataRow("dbg", LogEventLevel.Debug)]
    [DataRow("Information", LogEventLevel.Information)]
    [DataRow("info", LogEventLevel.Information)]
    [DataRow("Warning", LogEventLevel.Warning)]
    [DataRow("warn", LogEventLevel.Warning)]
    [DataRow("Error", LogEventLevel.Error)]
    [DataRow("err", LogEventLevel.Error)]
    [DataRow("Fatal", LogEventLevel.Fatal)]
    [DataRow("critical", LogEventLevel.Fatal)]
    public void TryParse_AcceptsAllSixSerilogLevelsAndCommonAliases(string value, LogEventLevel expected)
    {
        Assert.IsTrue(PuddingLogLevelSwitch.TryParse(value, out var level), $"'{value}' 应被识别");
        Assert.AreEqual(expected, level);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("nonsense")]
    [DataRow("Information-ish")]
    public void TryParse_RejectsUnknownWithoutGuessing(string? value)
    {
        // 认不出来就返回 false，调用方**保留当前级别**；绝不能悄悄把级别放宽到 Verbose。
        Assert.IsFalse(PuddingLogLevelSwitch.TryParse(value, out var level));
        Assert.AreEqual(LogEventLevel.Information, level, "失败时的兜底级别必须是 Information（不放大日志量）");
    }

    [TestMethod]
    public void ResolveStartupLevel_PrefersConfigurationOverEnvironment()
    {
        var before = Environment.GetEnvironmentVariable(PuddingLogLevelSwitch.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(PuddingLogLevelSwitch.EnvironmentVariableName, "Debug");

            // 配置优先：环境变量是兜底，不得覆盖显式配置。
            Assert.AreEqual(LogEventLevel.Warning, PuddingLogLevelSwitch.ResolveStartupLevel("Warning"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PuddingLogLevelSwitch.EnvironmentVariableName, before);
        }
    }

    [TestMethod]
    public void ResolveStartupLevel_FallsBackToEnvironmentThenInformation()
    {
        var before = Environment.GetEnvironmentVariable(PuddingLogLevelSwitch.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(PuddingLogLevelSwitch.EnvironmentVariableName, "Debug");
            Assert.AreEqual(LogEventLevel.Debug, PuddingLogLevelSwitch.ResolveStartupLevel(configured: null));
            Assert.AreEqual(
                LogEventLevel.Debug,
                PuddingLogLevelSwitch.ResolveStartupLevel(configured: "not-a-level"),
                "无法识别的配置值必须继续走兜底，而不是静默变回 Information");

            Environment.SetEnvironmentVariable(PuddingLogLevelSwitch.EnvironmentVariableName, null);
            Assert.AreEqual(LogEventLevel.Information, PuddingLogLevelSwitch.ResolveStartupLevel(configured: null));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PuddingLogLevelSwitch.EnvironmentVariableName, before);
        }
    }

    [TestMethod]
    public void DefaultInstance_StartsAtInformation()
    {
        // 默认不放大日志量：发布后默认 Information（Verbose/Debug 必须显式开启）。
        Assert.AreEqual(LogEventLevel.Information, PuddingLogLevelSwitch.Instance.MinimumLevel);
    }

    [DataTestMethod]
    [DataRow("AgentExecution", null, "AgentExecution")]
    [DataRow(null, "PuddingRuntime.Services.HeartbeatService", "HeartbeatService")]
    [DataRow(null, "HeartbeatService", "HeartbeatService")]
    [DataRow(null, null, "core")]
    [DataRow("   ", "   ", "core")]
    [DataRow("Connector", "PuddingHost.Services.ConnectorHost", "Connector")]
    public void DeriveTag_PrefersComponentThenSourceContextShortNameThenCore(
        string? component, string? sourceContext, string expected)
    {
        Assert.AreEqual(expected, LogTagEnricher.DeriveTag(component, sourceContext));
    }

    [TestMethod]
    public void EnricherAddsTagEvenWhenNothingElseIsPresent()
    {
        // 行内 tag 是"可追踪"的最低保证：即使宿主自身日志也必须带 tag。
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            MessageTemplate.Empty,
            properties: []);

        new LogTagEnricher().Enrich(logEvent, new ScalarPropertyFactory());

        Assert.IsTrue(logEvent.Properties.ContainsKey(LogTagEnricher.PropertyName));
        Assert.AreEqual(
            LogTagEnricher.FallbackTag,
            ((ScalarValue)logEvent.Properties[LogTagEnricher.PropertyName]).Value);
    }

    /// <summary>最小属性工厂：日志基础设施不需要结构化对象，全部按标量写入。</summary>
    private sealed class ScalarPropertyFactory : Serilog.Core.ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }
}
