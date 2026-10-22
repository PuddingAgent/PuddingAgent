namespace PuddingDesktop.Foundation;

using System.Text;
using System.Text.Json;

/// <summary>
/// 把已存的标注还原到画布上：生成一段自带重试的内联脚本。
///
/// <para>
/// 为什么是**内联脚本**而不是宿主 <c>ExecuteScriptAsync</c>：标注层由壳在包装 HTML 时注入，
/// 注入顺序与页面脚本谁先执行并没有保证。宿主侧执行还要先按 panelId 反查 tab，
/// 多一层配对就多一处"看起来执行了、其实没找到画布"的失败面。
/// 内联脚本自己等到 <c>puddingAnnotation</c> 出现再灌，顺序无关。
/// </para>
///
/// <para>
/// 坐标一律传**归一化 0–1000**（与 <see cref="PanelAnnotation.Normalize"/> 的除数一致），
/// 像素换算交给页面按当前显示尺寸做 —— 与其它路径共用同一套换算。
/// </para>
/// </summary>
public static class PanelCanvasRestore
{
    /// <summary>最多等多久（毫秒）。到点就放弃，不无限轮询。</summary>
    public const int TimeoutMs = 5000;

    /// <summary>轮询间隔（毫秒）。</summary>
    public const int PollIntervalMs = 100;

    /// <summary>
    /// 生成内联 <c>&lt;script&gt;</c> 块；没有标注时返回空串（调用方直接不追加）。
    /// </summary>
    public static string BuildInlineScript(IReadOnlyList<PanelAnnotation> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return string.Empty;
        }

        var payload = BuildPayload(items);
        var attempts = Math.Max(1, TimeoutMs / PollIntervalMs);

        // 注意：脚本里不要出现 </script> 字面量，否则会提前截断 HTML。
        return "<script>(function(){var n=0,t=setInterval(function(){"
            + "if(window.puddingAnnotation&&puddingAnnotation.load){"
            + "clearInterval(t);puddingAnnotation.load(" + payload + ");}"
            + "else if(++n>" + attempts + "){clearInterval(t);}},"
            + PollIntervalMs + ");})();</script>";
    }

    /// <summary>
    /// 标注数组 → JSON。手写而非反射序列化：与制成品 JSON 一致，避免裁剪/AOT 下的元数据依赖。
    ///
    /// <para>
    /// 这是「标注 → JSON」的**唯一实现**：页面 <c>load()</c> 用它，制成品文件的
    /// <c>annotations</c> 也直接嵌入它的输出（<see cref="PanelCraftStore"/>）。
    /// 两边各自手写一份字段名，就会出现「存得下、读不回」的静默分叉。
    /// </para>
    /// </summary>
    public static string BuildPayload(IReadOnlyList<PanelAnnotation> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var item in items)
            {
                writer.WriteStartObject();

                if (item.IsScribble && item.HasInkTrail)
                {
                    // 涂鸦只传轨迹：页面按 points 重画，bbox 由页面自己算。
                    writer.WriteString("kind", "scribble");
                    writer.WriteStartArray("points");
                    foreach (var point in item.Points!)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("x", point.X);
                        writer.WriteNumber("y", point.Y);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }
                else if (item.IsScribble)
                {
                    // 没轨迹的旧涂鸦：仍按框还原，总比什么都不画强。
                    writer.WriteString("kind", "box");
                    WriteCorners(writer, item);
                }
                else
                {
                    writer.WriteString("kind", item.Kind switch
                    {
                        PanelAnnotationKind.Arrow => "arrow",
                        PanelAnnotationKind.Text => "text",
                        PanelAnnotationKind.Point => "point",
                        _ => "box",
                    });

                    if (item.Kind == PanelAnnotationKind.Point)
                    {
                        writer.WriteNumber("x", item.X1);
                        writer.WriteNumber("y", item.Y1);
                    }
                    else if (item.Kind == PanelAnnotationKind.Text)
                    {
                        writer.WriteNumber("x", item.X1);
                        writer.WriteNumber("y", item.Y1);
                        writer.WriteString("label", item.Label);
                    }
                    else
                    {
                        WriteCorners(writer, item);
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteCorners(Utf8JsonWriter writer, PanelAnnotation item)
    {
        writer.WriteNumber("x1", item.X1);
        writer.WriteNumber("y1", item.Y1);
        writer.WriteNumber("x2", item.X2);
        writer.WriteNumber("y2", item.Y2);
    }
}
