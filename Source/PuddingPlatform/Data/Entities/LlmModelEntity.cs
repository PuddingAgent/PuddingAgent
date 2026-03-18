using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// LLM 模型（属于某个 Provider）。
/// 记录模型的费率、上下文长度、能力标签等关键元数据。
/// </summary>
public class LlmModelEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>所属 Provider 外键</summary>
    public int ProviderId { get; set; }

    /// <summary>模型标识（与 API 调用时传入的 model 字段一致，如 gpt-4o-mini）</summary>
    [Required, MaxLength(128)]
    public string ModelId { get; set; } = string.Empty;

    /// <summary>模型显示名称</summary>
    [Required, MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>描述信息（能力、适用场景等）</summary>
    [MaxLength(2048)]
    public string? Description { get; set; }

    /// <summary>最大上下文长度（tokens）</summary>
    public int MaxContextTokens { get; set; } = 8192;

    /// <summary>输入价格（每百万 tokens，USD）</summary>
    public decimal InputPricePer1MTokens { get; set; }

    /// <summary>输出价格（每百万 tokens，USD）</summary>
    public decimal OutputPricePer1MTokens { get; set; }

    /// <summary>
    /// 能力标签（JSON 数组存储），如 ["vision","function-calling","json-mode"]
    /// </summary>
    [MaxLength(1024)]
    public string CapabilityTagsJson { get; set; } = "[]";

    /// <summary>是否已弃用</summary>
    public bool IsDeprecated { get; set; } = false;

    /// <summary>是否默认推荐模型</summary>
    public bool IsDefault { get; set; } = false;

    /// <summary>排序权重（数值越小越靠前）</summary>
    public int SortOrder { get; set; } = 100;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航属性
    public LlmProviderEntity Provider { get; set; } = null!;
}
