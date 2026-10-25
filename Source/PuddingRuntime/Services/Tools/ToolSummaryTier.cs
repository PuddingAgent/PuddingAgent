using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// L1「摘要级」工具定义：冷门工具不再发整份 schema，只发「它做什么（首句）+ 参数名 + 必填」。
///
/// 背景（2026-10-25 实测）：工具定义占 prompt 13.2%（单请求 p50 19,546 token），
/// 但正常调用里工具块的缺失只占 prompt 的 0.8%——它基本是全命中的固定前缀。
/// 因此分级的目标不是"每轮省钱"，而是：
/// ① 让工具集变化时被重读的那一块变小（一次变化≈一块，实测 15.2k token）；
/// ② 让同一份字节预算容纳更多工具，减少"发现不到就得搜"的往返。
///
/// 设计约束（见 Docs/11_design/工具三级披露与动态加载-2026-10-25.md §4）：
/// 任一级别变化都会改写 ToolSpecHash，等于让该请求的工具块按未命中计费。
/// 所以本类只做**纯函数压缩**，不决定"谁该被压缩"——选择权留给调用方，
/// 由它保证级别只升不降、不在轮次之间抖动。
///
/// 压缩必须保住两件事：这个工具**做什么**（第一句），以及**参数叫什么、哪些必填**。
/// 丢掉的只有逐参说明、示例和嵌套 schema 细节——那些正是最占字节的部分。
/// </summary>
internal static class ToolSummaryTier
{
    /// <summary>L1 描述上限。首句优先，超长才硬截断。</summary>
    internal const int DefaultDescriptionChars = 160;

    /// <summary>把一条完整工具定义压成 L1 形态。纯函数，输入不变则输出不变。</summary>
    internal static LlmToolDefinition Apply(LlmToolDefinition tool, int descriptionChars = DefaultDescriptionChars)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var limit = descriptionChars > 0 ? descriptionChars : DefaultDescriptionChars;

        // 参数只留「名字 + 类型」：模型靠这两项就能试调；逐参说明是字节大户。
        var properties = tool.Parameters.Properties
            .Select(parameter => new ToolParameter(parameter.Name, parameter.Type, string.Empty))
            .ToList();

        return tool with
        {
            Description = SummarizeDescription(tool.Description, limit),
            // RawJsonSchema 必须丢掉：它会把完整 schema 原样带回去，等于没压缩。
            Parameters = new ToolParameterSchema(properties, tool.Parameters.Required, RawJsonSchema: null),
        };
    }

    /// <summary>保留首句（最长 limit 字符）。空白输入返回空串，不返回 null——调用方无需判空。</summary>
    internal static string SummarizeDescription(string? description, int limit = DefaultDescriptionChars)
    {
        if (string.IsNullOrWhiteSpace(description))
            return string.Empty;

        var cap = limit > 0 ? limit : DefaultDescriptionChars;
        var text = description.Trim();
        var sentenceEnd = FirstSentenceEnd(text);
        if (sentenceEnd > 0)
            text = text[..sentenceEnd].TrimEnd();
        if (text.Length > cap)
            text = text[..cap].TrimEnd();
        return text;
    }

    /// <summary>
    /// 首句结束位置（不含终止符）。找不到返回 -1。
    /// 中文句号/换行/感叹/问号直接算；英文句点只在后面是空白或已到结尾时才算，
    /// 避免把 `e.g.` `v1.2` 这类切成半句。
    /// </summary>
    private static int FirstSentenceEnd(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '。':
                case '\n':
                case '！':
                case '？':
                case '!':
                case '?':
                    return i;
                case '.':
                    // 英文句点只在「后面是空白或已到结尾」且「前面不是单字母缩写（e.g. / i.e. / U.S.）」时
                    // 才算句末；否则会把 `e.g. /usage` 切成半句。
                    if ((i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])) && !EndsWithSingleLetterToken(text, i))
                        return i;
                    break;
            }
        }

        return -1;
    }

    /// <summary>取出句点前一个词是否为单字母（缩写形态：e.g. / i.e. / U.S.）。</summary>
    private static bool EndsWithSingleLetterToken(string text, int dotIndex)
    {
        var end = dotIndex;
        var start = end;
        while (start > 0 && char.IsLetter(text[start - 1]))
            start--;
        return end - start == 1;
    }
}
