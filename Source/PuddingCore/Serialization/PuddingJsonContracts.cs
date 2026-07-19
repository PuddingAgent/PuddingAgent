using System.Text.Json;

namespace PuddingCode.Serialization;

/// <summary>
/// 统一 JSON 序列化契约：jsonl 文件每行不换行（单行 JSON object），json 文件使用缩进格式。
/// 所有 Producer/Consumer 必须使用这两个选项，防止序列化格式不一致。
/// </summary>
public static class PuddingJsonContracts
{
    /// <summary>缩进格式 — 用于 .json 文件</summary>
    public static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>紧凑格式 — 用于 .jsonl 文件（每行一个完整 JSON object，不换行）</summary>
    public static readonly JsonSerializerOptions JsonLines = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
