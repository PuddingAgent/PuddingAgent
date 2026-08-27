using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// P0-6：工具定义 canonical 哈希。授权成功时对当时的 <see cref="ToolDescriptor"/> 计算
/// 规范哈希并随授权记录持久化，暴露/授权评估时可检测定义漂移。
/// 规范化语义对齐 <see cref="CompositionSnapshot.ComputeToolSpecHash"/>（camelCase、无缩进、
/// 字节稳定的规范 JSON → SHA-256 小写 hex）；序列化字段 = ToolId + Name + Description +
/// Parameters（Properties 按 Name 排序投影，Required 保留原序，RawJsonSchema 取原文）。
/// 只存哈希与版本号，绝不持久化 schema 全文（ADR-074 隐私约束）。
/// </summary>
public static class ToolDefinitionHash
{
    /// <summary>规范化 JSON 序列化选项：camelCase、无缩进、字节稳定。</summary>
    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    /// <summary>
    /// 计算工具定义的规范哈希（SHA-256，小写 hex）。同一工具定义无论构造顺序如何
    /// （Properties 顺序差异）都得到相同哈希；任何模型可见字段（含 Description、参数
    /// 类型/描述/Required/RawJsonSchema）变化都会改变哈希。
    /// </summary>
    public static string Compute(ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var canonical = new
        {
            descriptor.ToolId,
            descriptor.Name,
            descriptor.Description,
            Parameters = new
            {
                Properties = descriptor.Parameters.Properties
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new
                    {
                        p.Name,
                        p.Type,
                        p.Description,
                    }).ToArray(),
                Required = descriptor.Parameters.Required.ToArray(),
                RawJsonSchema = descriptor.Parameters.RawJsonSchema?.GetRawText(),
            },
        };

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical, CanonicalJson)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
