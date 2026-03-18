using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// LLM 服务提供商（全局资源池）。
/// 每个 Provider 可配置多个模型；支持多种协议，当前阶段以 OpenAI 协议为主。
/// </summary>
public class LlmProviderEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>唯一标识符（slug），如 "openai", "deepseek", "custom-1"</summary>
    [Required, MaxLength(64)]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>显示名称</summary>
    [Required, MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>接口协议类型，当前支持 "openai"，保留其他协议扩展。</summary>
    [Required, MaxLength(32)]
    public string Protocol { get; set; } = "openai";

    /// <summary>API 基础地址（需含 /v1，如 https://api.openai.com/v1）</summary>
    [Required, MaxLength(512)]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API Key（明文存储，生产环境应走 secrets/vault）</summary>
    [MaxLength(512)]
    public string? ApiKey { get; set; }

    /// <summary>描述/备注</summary>
    [MaxLength(1024)]
    public string? Description { get; set; }

    /// <summary>是否启用</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>最后更新时间</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航属性
    public ICollection<LlmModelEntity> Models { get; set; } = [];
    public LlmProviderQuotaEntity? Quota { get; set; }
}
