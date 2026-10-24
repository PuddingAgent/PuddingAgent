using System.Text.Json;

namespace PuddingCode.Observability;

/// <summary>
/// 从出站报文里读出思考强度。
///
/// 为什么需要它：捕获日志目前只记字节数、指纹和 token，读不出"报文里到底有没有
/// reasoning 段"。于是「重启后怎么算通过」这个验收标准只能靠单元测试间接推断，
/// 线上没有任何直接证据——这正是我在设计文档里写过、却仍然犯的一次仪表不足。
///
/// 只在 PUDDING_CAPTURE_REQUEST 打开时被调用，所以整份报文的解析成本不计入常态。
/// 解析失败一律返回 null：诊断代码不允许因为报文形状异常而让请求失败。
/// </summary>
public static class LlmBodyReasoningProbe
{
    /// <summary>报文里 reasoning.effort 的值；没有该段时返回 null。</summary>
    public static string? TryReadReasoningEffort(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!document.RootElement.TryGetProperty("reasoning", out var reasoning)
                || reasoning.ValueKind != JsonValueKind.Object)
                return null;
            if (!reasoning.TryGetProperty("effort", out var effort)
                || effort.ValueKind != JsonValueKind.String)
                return null;
            return effort.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
