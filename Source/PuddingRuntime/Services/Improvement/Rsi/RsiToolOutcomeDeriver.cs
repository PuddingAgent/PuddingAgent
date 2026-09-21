using System.Text.Json;

namespace PuddingRuntime.Services.Improvement.Rsi;

/// <summary>结局推导结果：结局 + 从 payload 解出的字段（缺失字段保持 null，不得用 0 / 空串冒充）。</summary>
public sealed record RsiToolOutcomeResult
{
    /// <summary>三态结局。</summary>
    public required RsiToolOutcome Outcome { get; init; }

    /// <summary>退出码；payload 未提供时为 null（不是 0）。</summary>
    public int? ExitCode { get; init; }

    /// <summary>错误文本；无错误为 null。</summary>
    public string? Error { get; init; }

    /// <summary>工具名（payload.name）。</summary>
    public string? ToolName { get; init; }

    /// <summary>工具输出（payload.output）。</summary>
    public string? Output { get; init; }

    /// <summary>工具调用 Id（payload.toolCallId）。</summary>
    public string? ToolCallId { get; init; }
}

/// <summary>
/// 从 <c>tool.call.completed</c> 的 payload 推导工具调用结局的<b>纯函数</b>（规格 S3 §1.2 冻结规则）。
/// <para>
/// 判定规则（不得另发明）：
/// <list type="bullet">
/// <item><b>Failed</b> ⇔ exitCode 非 0 <b>或</b> error 非空；</item>
/// <item><b>Completed</b> ⇔ exitCode == 0 <b>且</b> error 为空；</item>
/// <item><b>Unknown</b> ⇔ 两者都缺失（例如工具本身没有 exitCode 语义）。</item>
/// </list>
/// </para>
/// <para>
/// 三条纪律：
/// ① Unknown 是一等公民——payload 缺失/非法/两者皆缺时返回 Unknown，
///    <b>不得抛异常</b>，<b>不得</b>把「成功」当默认值；
/// ② 字段契约与折叠层逐字一致（ConversationTranscriptFold.cs:409-419 与 :502-527）：
///    exitCode 认 camelCase 并含 exit_code 下划线回退，接受 JSON 数字或数字字符串；
///    「error 为空」与 ADR-064 <c>IsSuccessful</c> 的 <c>IsNullOrWhiteSpace</c> 语义一致；
/// ③ 纯静态、无副作用、无 IO，不依赖 DB / ILogger / DI。
/// </para>
/// </summary>
public static class RsiToolOutcomeDeriver
{
    /// <summary>推导结局。任何输入都不抛异常；无法解析时返回 Unknown（不得当 Completed）。</summary>
    public static RsiToolOutcomeResult Derive(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return Unknown();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return Unknown();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Unknown();

            var error = GetString(root, "error");
            var exitCode = GetInt(root, "exitCode") ?? GetInt(root, "exit_code");

            RsiToolOutcome outcome;
            if (exitCode is { } code && code != 0)
                outcome = RsiToolOutcome.Failed;        // exitCode 非 0（含负数）
            else if (!string.IsNullOrWhiteSpace(error))
                outcome = RsiToolOutcome.Failed;        // error 非空：优先于 exitCode == 0
            else if (exitCode is not null)
                outcome = RsiToolOutcome.Completed;     // exitCode == 0 且 error 为空
            else
                outcome = RsiToolOutcome.Unknown;       // 两者都缺失 ⇒ 不得默认成功

            return new RsiToolOutcomeResult
            {
                Outcome = outcome,
                ExitCode = exitCode,
                Error = error,
                ToolName = GetString(root, "name"),
                Output = GetString(root, "output"),
                ToolCallId = GetString(root, "toolCallId"),
            };
        }
    }

    private static RsiToolOutcomeResult Unknown() => new() { Outcome = RsiToolOutcome.Unknown };

    // ── 防御性 JSON 读取：与 ConversationTranscriptFold.GetString / GetInt（:502-527）同一契约，绝不抛异常 ──

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            return parsed;
        return null;
    }
}
