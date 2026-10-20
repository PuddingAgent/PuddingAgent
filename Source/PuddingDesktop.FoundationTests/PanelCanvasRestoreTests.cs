namespace PuddingDesktop.FoundationTests;

using PuddingDesktop.Foundation;

/// <summary>
/// 制成品重开时把标注画回画布。
///
/// <para>
/// 这层的失败模式很轻：灌不进去就是"没画回来"，与未实现时表现一致，不会损坏数据。
/// 所以断言只钉住**形状**：脚本真的调了 <c>load</c>、payload 是归一化值、涂鸦带轨迹。
/// 画笔的实际手感与重试是否够长属于人工验收项。
/// </para>
/// </summary>
public sealed class PanelCanvasRestoreTests
{
    [Fact]
    public void Empty_items_produce_no_script_at_all()
    {
        // 不产生空 script：免得页面里多一个什么都不做的块。
        Assert.Equal(string.Empty, PanelCanvasRestore.BuildInlineScript(Array.Empty<PanelAnnotation>()));
    }

    [Fact]
    public void Script_waits_for_the_annotation_layer_then_calls_load()
    {
        var script = PanelCanvasRestore.BuildInlineScript(new[] { PanelAnnotation.Box(10, 20, 30, 40) });

        Assert.Contains("puddingAnnotation.load", script, StringComparison.Ordinal);
        Assert.Contains("setInterval", script, StringComparison.Ordinal);
        // 超时后必须停：否则一条永不结束的定时器会一直轮询页面。
        Assert.Contains("clearInterval", script, StringComparison.Ordinal);

        // 脚本不能含 </script> 字面量，否则会截断宿主 HTML。
        Assert.DoesNotContain("</script>", script[8..^9], StringComparison.Ordinal);
    }

    [Fact]
    public void Payload_carries_normalized_coordinates_for_every_kind()
    {
        var payload = PanelCanvasRestore.BuildPayload(new[]
        {
            PanelAnnotation.Box(320, 180, 680, 520),
            PanelAnnotation.Point(518, 135),
            PanelAnnotation.Arrow(880, 900, 120, 100),
            PanelAnnotation.Text("左边的人", 300, 400),
        });

        // 按**语义**断言而不是字符串匹配：Utf8JsonWriter 默认会把非 ASCII 转成 \uXXXX
        // （对内联 script 是好事），死盯原文会把正确实现判成失败。
        using var document = System.Text.Json.JsonDocument.Parse(payload);
        var items = document.RootElement;
        Assert.Equal(4, items.GetArrayLength());

        Assert.Equal("box", items[0].GetProperty("kind").GetString());
        Assert.Equal(320, items[0].GetProperty("x1").GetInt32());
        Assert.Equal(520, items[0].GetProperty("y2").GetInt32());

        Assert.Equal("point", items[1].GetProperty("kind").GetString());
        Assert.Equal(518, items[1].GetProperty("x").GetInt32());

        // 箭头方向靠顺序：起终点不能被排序掉。
        Assert.Equal("arrow", items[2].GetProperty("kind").GetString());
        Assert.Equal(880, items[2].GetProperty("x1").GetInt32());
        Assert.Equal(120, items[2].GetProperty("x2").GetInt32());

        Assert.Equal("text", items[3].GetProperty("kind").GetString());
        Assert.Equal("左边的人", items[3].GetProperty("label").GetString());
        Assert.Equal(300, items[3].GetProperty("x").GetInt32());
    }

    [Fact]
    public void Scribble_payload_carries_the_trail_not_just_the_box()
    {
        // 只给 bbox 的话，重开后画出来的是一根盒状线，不是用户原本那笔。
        var scribble = PanelAnnotation.Scribble(new[]
        {
            new PanelInkPoint(100, 120),
            new PanelInkPoint(300, 200),
            new PanelInkPoint(700, 880),
        });

        var payload = PanelCanvasRestore.BuildPayload(new[] { scribble });

        Assert.Contains("\"kind\":\"scribble\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"points\":[{\"x\":100,\"y\":120}", payload, StringComparison.Ordinal);
        Assert.Contains("{\"x\":700,\"y\":880}]", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_scribble_without_a_trail_falls_back_to_its_box()
    {
        var legacy = PanelAnnotation.ScribbleFromPixels(80, 40, 400, 200, 800, 400);

        var payload = PanelCanvasRestore.BuildPayload(new[] { legacy });

        Assert.Contains("\"kind\":\"box\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"x1\":100", payload, StringComparison.Ordinal);
    }
}
