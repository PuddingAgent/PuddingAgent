using Microsoft.Extensions.Logging;

namespace PuddingPlatform.Services;

/// <summary>
/// 配置数据种子迁移服务 — A方案已废弃（JSON 文件为唯一配置来源）。
/// LLM Provider/Model 和 GlobalAgentTemplate 不再从 JSON 种子到 DB。
/// 保留空实现以兼容 Program.cs 调用链。
/// </summary>
public sealed class DataSeedService
{
    private readonly ILogger<DataSeedService> _logger;

    public DataSeedService(ILogger<DataSeedService> logger)
    {
        _logger = logger;
    }

    public Task SeedAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Data seed skipped (A方案: JSON files are the single source of truth)");
        return Task.CompletedTask;
    }
}
