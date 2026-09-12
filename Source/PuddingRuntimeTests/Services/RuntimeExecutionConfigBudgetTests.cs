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
