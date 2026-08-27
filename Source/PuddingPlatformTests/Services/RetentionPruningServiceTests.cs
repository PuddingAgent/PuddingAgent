using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Services;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingPlatformTests.Services;

/// <summary>
/// RetentionPruningService（ADR-076 §5.2）调度行为专项测试。
///
/// 自 c3deb2d 起，服务仅负责"何时清理"（策略读取、闸门、抖动调度）；
/// 数据库删除统一委托 StorageMaintenanceCoordinator（sealed、Channel 队列消费模型，
/// 无法在单元测试内轻量驱动）。因此：
///  - 本文件覆盖策略闸门与 fail-closed 合成逻辑；
///  - 三表裁剪/归档先于删除等行为归属 Coordinator/Executor 层测试（当前缺口，另行登记）。
///
/// 哨兵约定：以下用例均处于 RunOnceAsync 不触达 coordinator 的安全分支，
/// 以 null! 占位注入——若未来分支改动意外触达 coordinator 将直接 NRE 报红，
/// 提醒测试随架构同步演进。
/// </summary>
[TestClass]
public sealed class RetentionPruningServiceTests
{
    private const string TidPlaceholder = "__TID__";

    /// <summary>策略合成用 system.json 模板：__TID__ 由真实 TargetId 运行时替换。</summary>
    private const string SyntheticPolicyJsonTemplate =
        "{\"storageManagement\":{\"automaticCleanup\":{\"enabled\":true,\"targets\":{\"__TID__\":{\"retentionDays\":0},\"ghost-target-x\":{\"enabled\":true}}}}}";

    private static (PuddingDataPaths Paths, string RootDir) MakePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-retention-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (PuddingDataPaths.FromRoot(root), root);
    }

    private static string WriteSystemJson(PuddingDataPaths paths, string json)
    {
        var configPath = paths.SystemConfigFile("system.json");
        var dir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(configPath, json);
        return configPath;
    }

    private static RetentionPruningService CreateService(
        PuddingDataPaths paths,
        CapturingLogger logger)
        => new(
            new StorageRetentionPolicyService(paths, NullLogger<StorageRetentionPolicyService>.Instance),
            /* ADR-076 后仅在自动清理启用时触达；闸门用例以 null 哨兵占位 */
            null!,
            logger);

    private static async Task<EffectiveStoragePolicy> LoadPolicyAsync(PuddingDataPaths paths)
    {
        var policyService = new StorageRetentionPolicyService(
            paths, NullLogger<StorageRetentionPolicyService>.Instance);
        return await policyService.GetEffectivePolicyAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Disabled_By_Policy_Short_Circuits_Skip_Sweep()
    {
        var (paths, _) = MakePaths();
        WriteSystemJson(paths, "{\"storageManagement\":{\"automaticCleanup\":{\"enabled\":false}}}");

        var logger = new CapturingLogger();
        var service = CreateService(paths, logger);
        await service.RunOnceAsync();

        Assert.IsTrue(logger.ContainsMessage("disabled by policy"),
            "策略禁用时 RunOnceAsync 应短路并记录 skip sweep 日志");
    }

    [TestMethod]
    public async Task Corrupted_System_Json_Fails_Closed_As_Disabled()
    {
        var (paths, _) = MakePaths();
        // 文件存在但无法解析 → 读取异常分支必须 fail closed（绝不按默认开启清理）
        WriteSystemJson(paths, "{ this is not valid json ");

        var logger = new CapturingLogger();
        var service = CreateService(paths, logger);
        await service.RunOnceAsync();

        Assert.IsTrue(logger.ContainsMessage("disabled by policy"),
            "system.json 损坏时应 fail closed 短路清理");

        var policy = await LoadPolicyAsync(paths);
        Assert.IsFalse(policy.AutomaticCleanupEnabled, "fail closed 场景下 AutomaticCleanupEnabled 必须为 false");
        Assert.IsTrue(policy.Warnings.Any(w => w.Contains("fail closed")),
            "应包含 fail closed 警告文案");
    }

    [TestMethod]
    public async Task Policy_Synthesis_Warns_Unknown_Target_And_Suspends_Illegal_Retention()
    {
        var (paths, _) = MakePaths();
        WriteSystemJson(paths, "{}"); // 无 storageManagement 节 → 目标清单来自代码目录定义

        var baseline = await LoadPolicyAsync(paths);
        var eligible = baseline.Targets.FirstOrDefault(t => t.AutomaticCleanupAllowed);
        if (eligible is null)
            Assert.Inconclusive("StorageDataClassCatalog 未定义任何允许自动清理的目标，跳过合成断言");
        var realTargetId = eligible!.TargetId;

        // retentionDays=0 属非法值（0 不代表立即删除）→ 目标挂起；ghost 目标 → 未知警告。
        var json = SyntheticPolicyJsonTemplate.Replace(TidPlaceholder, realTargetId, StringComparison.Ordinal);
        WriteSystemJson(paths, json);

        var policy = await LoadPolicyAsync(paths);

        var overridden = policy.Targets.Single(t => t.TargetId == realTargetId);
        Assert.IsTrue(overridden.Suspended, "retentionDays=0 属非法值，目标必须挂起");
        Assert.IsFalse(overridden.Enabled, "挂起目标同时禁用");
        Assert.IsTrue(policy.Warnings.Any(w => w.Contains(realTargetId) && w.Contains("暂停")),
            "非法保留期应产生挂起警告");
        Assert.IsTrue(policy.Warnings.Any(w => w.Contains("未知策略目标 ghost-target-x")),
            "未知目标应被忽略并警告");
    }

    /// <summary>轻量内存日志捕获器：用于断言关键控制流日志。</summary>
    public sealed class CapturingLogger : ILogger<RetentionPruningService>
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = [];

        public bool ContainsMessage(string fragment)
        {
            lock (_gate)
                return _messages.Any(m => m.Contains(fragment, StringComparison.Ordinal));
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
                _messages.Add(formatter(state, exception));
        }
    }
}
