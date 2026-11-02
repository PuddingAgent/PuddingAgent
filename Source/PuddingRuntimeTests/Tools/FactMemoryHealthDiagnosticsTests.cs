using PuddingRuntime.Services.Tools;
using PuddingMemoryEngine.Services;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// `agent_diagnostics` 的 `fact_memory_health` 用这条判定决定「要不要报警」。
/// <para>
/// 存在的理由：`MemoryFacts` 长期 0 行是翻库时偶然发现的 —— 服务注册了、测试全绿、没人调用。
/// 静默失效必须能被主动查出来，而**只有「开关开着却没写入」才算异常**：
/// Disabled 是预期状态，把它报成故障会让告警本身失去意义。
/// </para>
/// </summary>
[TestClass]
public sealed class FactMemoryHealthDiagnosticsTests
{
    [TestMethod]
    public void Disabled_is_not_an_anomaly()
    {
        Assert.IsFalse(AgentDiagnosticsTool.IsFactMemoryAnomaly(FactMemoryWriteHealth.Disabled),
            "开关没开是预期状态，不该报警");
    }

    [TestMethod]
    public void Healthy_is_not_an_anomaly()
    {
        Assert.IsFalse(AgentDiagnosticsTool.IsFactMemoryAnomaly(FactMemoryWriteHealth.Healthy));
    }

    [TestMethod]
    public void Enabled_but_never_written_is_an_anomaly()
    {
        Assert.IsTrue(AgentDiagnosticsTool.IsFactMemoryAnomaly(FactMemoryWriteHealth.NeverWritten),
            "开关开着却 0 行 —— 这正是当初那个静默失效的形态");
    }

    [TestMethod]
    public void Stalled_is_an_anomaly()
    {
        Assert.IsTrue(AgentDiagnosticsTool.IsFactMemoryAnomaly(FactMemoryWriteHealth.Stalled),
            "写过但长期没新写入，同样需要报警");
    }
}
