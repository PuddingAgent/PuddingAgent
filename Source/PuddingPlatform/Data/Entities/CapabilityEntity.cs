using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 平台能力实体——定义 Agent 可用的工具/能力项。
/// </summary>
public class CapabilityEntity
{
    [Key]
    public int Id { get; set; }

    [MaxLength(128)]
    public string CapabilityId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? Description { get; set; }

    [MaxLength(128)]
    public string ToolName { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? ToolDescription { get; set; }

    public string? ToolParametersJson { get; set; }

    public bool RequiresShellExecution { get; set; }

    public bool RequiresFileWrite { get; set; }

    public bool RequiresNetworkAccess { get; set; }

    public bool IsEnabled { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
