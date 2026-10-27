using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// N00（卡 6495cee85e104d9ebdaaff7c65f73ccc）：运行时执行配置的子代理预算归一化回归。
/// S3 要求移除 Math.Max(600, cfg) 强制抬升——显式配置即权威（仅保留 ≥1 健全性下限），
/// 未配置时回落到长程默认 600 正常轮 / 2400 工具调用 / 24h 硬时限。
/// </summary>
[TestClass]
public sealed class RuntimeExecutionConfigBudgetTests
{
    [TestMethod]
    [DataRow(0, 300)]
    [DataRow(-1, 300)]
    [DataRow(42, 42)]
    [DataRow(int.MaxValue, 86400)]
    public void CompactionIdleDeadlineIsConfigurableAndValidForTimers(int configured, int expected)
    {
        var root = CreateRoot();
        try
        {
            WriteConfig(root, System.Text.Json.JsonSerializer.Serialize(new
            { contextCompaction = new { preparationIdleTimeoutSeconds = configured } }));
            Assert.AreEqual(expected, CreateService(root).GetOptions().ContextCompaction.PreparationIdleTimeoutSeconds);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void Normalize_ExplicitConfigBudgets_AreRespectedWithoutElevation()
    {
        var root = CreateRoot();
        try
        {
            WriteConfig(root, """
            {
              "subAgents": {
                "maxRounds": 400,
                "maxToolCallsTotal": 1800,
                "maxTimeoutSeconds": 7200,
                "budgetGraceRounds": 15
              }
            }
            """);

            var subAgents = CreateService(root).GetOptions().SubAgents;

            // N00/S3：显式配置（含低于 600 的小预算）必须原样生效，不再被抬到 600/2400/24h。
            Assert.AreEqual(400, subAgents.MaxRounds);
            Assert.AreEqual(1800, subAgents.MaxToolCallsTotal);
            Assert.AreEqual(7200, subAgents.MaxTimeoutSeconds);
            Assert.AreEqual(15, subAgents.BudgetGraceRounds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Normalize_UnsetBudgets_FallBackToLongRunDefaults()
    {
        var root = CreateRoot();
        try
        {
            var subAgents = CreateService(root).GetOptions().SubAgents;

            // N00/S2：未配置时落单一权威长程默认，而不是旧的 200/400/3600。
            Assert.AreEqual(600, subAgents.MaxRounds);
            Assert.AreEqual(2400, subAgents.MaxToolCallsTotal);
            Assert.AreEqual(24 * 60 * 60, subAgents.MaxTimeoutSeconds);
            Assert.AreEqual(20, subAgents.BudgetGraceRounds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ToolExposure_DefaultsToBoundedWorkingSet_AndSeedsSection()
    {
        var root = CreateRoot();
        try
        {
            var toolExposure = CreateService(root).GetOptions().ToolExposure;

            Assert.IsTrue(toolExposure.Enabled, "工具曝光预算默认启用（长会话固定前缀治理）");
            Assert.AreEqual(48, toolExposure.MaxVisibleTools);
            Assert.AreEqual(40 * 1024, toolExposure.MaxVisibleToolBytes);
            var json = File.ReadAllText(Path.Combine(root, "config", "runtime.execution.json"));
            StringAssert.Contains(json, "\"toolExposure\"", "默认节应随首次读取种入配置文件");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ToolExposure_NegativeLimits_AreClampedToZero()
    {
        var root = CreateRoot();
        try
        {
            WriteConfig(root, """
            {
              "toolExposure": {
                "maxVisibleTools": -5,
                "maxVisibleToolBytes": -1
              }
            }
            """);

            var toolExposure = CreateService(root).GetOptions().ToolExposure;

            Assert.AreEqual(0, toolExposure.MaxVisibleTools);
            Assert.AreEqual(0, toolExposure.MaxVisibleToolBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-exec-config-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        return root;
    }

    private static void WriteConfig(string root, string json) =>
        File.WriteAllText(Path.Combine(root, "config", "runtime.execution.json"), json);

    private static RuntimeExecutionConfigService CreateService(string root) =>
        new(PuddingDataPaths.FromRoot(root), NullLogger<RuntimeExecutionConfigService>.Instance);
}
