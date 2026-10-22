namespace PuddingDesktop.Foundation;

using System.Text.Json;

/// <summary>
/// 画布标注层的状态快照：当前工具、是否开着、几笔标记、能否撤销。
///
/// <para>
/// 为什么由宿主**读**而不是页面**推**：面板内容由 Agent 生成，能主动向宿主推消息的
/// 通道越少越好（见 <c>PanelCanvasDocument</c>）。但快捷键是在页面里执行的
/// （XAML 的键盘加速键收不到 WebView2 内部的按键），用户按 B 换笔、按 Esc 退出标注时，
/// 宿主那边的工具栏并不知道。宿主隔一会儿读一次这个快照，就能把按钮的选中态纠正回来
/// —— 不需要任何消息桥，也不需要页面配合。
/// </para>
///
/// <para>
/// 解析只认形状：这是宿主**自己**注入的脚本的返回值，不是外部输入，但仍按
/// fail-closed 处理 —— 形状不对就返回 false，调用方保持现状（宁可不刷新，
/// 也不要按一个编出来的状态去点亮按钮）。
/// </para>
/// </summary>
public sealed record PanelCanvasState(string Tool, bool IsActive, int Count, bool CanUndo)
{
    /// <summary>
    /// 解析 <c>puddingAnnotation.state()</c> 经 JSON 往返后的文本。
    ///
    /// <para>
    /// <c>tool</c> 必须是**非空字符串**，其余字段缺省为假/零：未知的工具名不是错误
    /// （将来加工具时宿主可能还没认它），只要状态栏不被点亮就行。
    /// </para>
    /// </summary>
    public static bool TryParse(string? json, out PanelCanvasState state)
    {
        state = null!;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // tool 是唯一必需的字段：没有它就无从判断「哪个按钮该亮」。
            if (!root.TryGetProperty("tool", out var toolElement)
                || toolElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var tool = toolElement.GetString();
            if (string.IsNullOrWhiteSpace(tool))
            {
                return false;
            }

            var active = root.TryGetProperty("active", out var activeElement)
                && activeElement.ValueKind == JsonValueKind.True;

            var count = root.TryGetProperty("count", out var countElement)
                && countElement.ValueKind == JsonValueKind.Number
                && countElement.TryGetInt32(out var parsedCount)
                && parsedCount > 0
                    ? parsedCount
                    : 0;

            var canUndo = root.TryGetProperty("canUndo", out var undoElement)
                && undoElement.ValueKind == JsonValueKind.True;

            state = new PanelCanvasState(tool, active, count, canUndo);
            return true;
        }
    }
}
