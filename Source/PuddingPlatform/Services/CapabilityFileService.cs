using System.Text.Json;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// 能力/工具注册文件式管理服务 — 存储在 data/config/capabilities.json。
/// 唯一事实来源：capabilities.json 文件。
/// </summary>
public sealed class CapabilityFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly PuddingDataPaths _paths;
    private readonly ILogger<CapabilityFileService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public CapabilityFileService(PuddingDataPaths paths, ILogger<CapabilityFileService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    private string ConfigPath => _paths.SystemConfigFile("capabilities.json");

    /// <summary>获取所有能力/工具注册。</summary>
    public async Task<List<CapabilityDto>> ListAsync(CancellationToken ct = default)
    {
        var config = await LoadAsync(ct);
        return config.Select((c, idx) => new CapabilityDto(
            Id: idx + 1,
            CapabilityId: c.CapabilityId ?? "",
            Name: c.Name ?? "",
            Description: c.Description,
            ToolName: c.ToolName ?? "",
            ToolDescription: c.ToolDescription,
            ToolParametersJson: c.ToolParametersJson,
            RequiresShellExecution: c.RequiresShellExecution,
            RequiresFileWrite: c.RequiresFileWrite,
            RequiresNetworkAccess: c.RequiresNetworkAccess,
            IsEnabled: c.IsEnabled,
            SortOrder: c.SortOrder,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow
        )).ToList();
    }

    /// <summary>加载 capabilities 配置。</summary>
    public async Task<List<CapabilityConfig>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ConfigPath))
            return [];

        var config = await AtomicFileWriter.ReadJsonAsync<List<CapabilityConfig>>(ConfigPath, JsonOptions, ct);
        return config ?? [];
    }

    /// <summary>保存 capabilities 配置。</summary>
    public async Task SaveAsync(List<CapabilityConfig> capabilities, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await AtomicFileWriter.WriteJsonAsync(ConfigPath, capabilities, JsonOptions, ct);
            _logger.LogInformation("Capabilities config saved to {Path}", ConfigPath);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

/// <summary>能力/工具注册配置模型。</summary>
public sealed record CapabilityConfig
{
    public string? CapabilityId { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? ToolName { get; init; }
    public string? ToolDescription { get; init; }
    public string? ToolParametersJson { get; init; }
    public bool RequiresShellExecution { get; init; }
    public bool RequiresFileWrite { get; init; }
    public bool RequiresNetworkAccess { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int SortOrder { get; init; }
}
