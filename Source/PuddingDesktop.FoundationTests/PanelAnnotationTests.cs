namespace PuddingDesktop.FoundationTests;

using PuddingDesktop.Foundation;

/// <summary>
/// 画布标注 → Seedream 坐标的换算契约。
///
/// 这里唯一"能算错却看起来正常"的地方是**换算与舍入**：坐标错了不会抛异常，
/// 只会生成一张"改错地方"的图，用户第一反应是模型不听话。所以：
/// ① 换算公式要与官方文档一致（round(px/extent*1000)）；
/// ② 越界要拒绝而不是夹取（夹取会把"算错了"伪装成"合法坐标"）；
/// ③ 半份标注整体拒绝（半份比没有更容易被误当成完整意图）。
/// </summary>
public sealed class PanelAnnotationTests
{
    // ── Normalize ───────────────────────────────────────────────

    [Fact]
    public void Converts_pixels_to_the_documented_zero_to_999_range()
    {
        // 官方：x = round(x_px / width * 1000)
        Assert.Equal(0, PanelAnnotation.Normalize(0, 800));
        Assert.Equal(500, PanelAnnotation.Normalize(400, 800));
        Assert.Equal(999, PanelAnnotation.Normalize(800, 800));
    }

    [Fact]
    public void Rounds_half_away_from_zero_so_boundaries_do_not_flicker()
    {
        // 默认银行家舍入会把 0.5 抹成 0，同一次框选在边界上会来回抖 1 像素，
        // 而用户完全看不出原因。
        Assert.Equal(1, PanelAnnotation.Normalize(0.5, 1000));
        Assert.Equal(2, PanelAnnotation.Normalize(1.5, 1000));
        Assert.Equal(1, PanelAnnotation.Normalize(0.6, 1000));
    }

    [Fact]
    public void Clamps_pixels_outside_the_image_instead_of_going_out_of_range()
    {
        // 用户在图片边缘拖到外面一点点是常态，落到 0 或 999 仍然是有意义的框。
        Assert.Equal(0, PanelAnnotation.Normalize(-20, 800));
        Assert.Equal(999, PanelAnnotation.Normalize(900, 800));
    }

    [Fact]
    public void Rejects_a_non_positive_extent()
    {
        // 图片还没加载完时宽高是 0；继续算会得到 Infinity 或 NaN，最后变成一个乱七八糟的坐标。
        Assert.Throws<ArgumentOutOfRangeException>(() => PanelAnnotation.Normalize(10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PanelAnnotation.Normalize(10, -5));
    }

    [Fact]
    public void Rejects_a_non_finite_pixel()
    {
        Assert.Throws<ArgumentException>(() => PanelAnnotation.Normalize(double.NaN, 800));
        Assert.Throws<ArgumentException>(() => PanelAnnotation.Normalize(double.PositiveInfinity, 800));
    }

    // ── FromPixels ──────────────────────────────────────────────

    [Fact]
    public void Normalizes_a_drag_in_any_direction()
    {
        var forward = PanelAnnotation.FromPixels(80, 40, 400, 200, 800, 400);
        var backward = PanelAnnotation.FromPixels(400, 200, 80, 40, 800, 400);

        // 用户从右下往左上拖也要得到同一个框，否则一半的框选方向会失效。
        Assert.Equal(forward, backward);
        Assert.Equal(PanelAnnotationKind.Box, forward.Kind);
        Assert.Equal("100 100", $"{forward.X1} {forward.Y1}");
        Assert.Equal("500 500", $"{forward.X2} {forward.Y2}");
    }

    [Fact]
    public void Builds_a_point_annotation_from_pixels()
    {
        var point = PanelAnnotation.PointFromPixels(518, 135, 1000, 1000);

        Assert.Equal(PanelAnnotationKind.Point, point.Kind);
        Assert.Equal("<point>518 135</point>", point.ToPromptFragment());
    }

    // ── ToPromptFragment ───────────────────────────────────────

    [Fact]
    public void Emits_the_exact_marker_syntax_the_model_expects()
    {
        Assert.Equal(
            "<bbox>179 283 796 986</bbox>",
            PanelAnnotation.Box(179, 283, 796, 986).ToPromptFragment());

        Assert.Equal("<point>518 135</point>", PanelAnnotation.Point(518, 135).ToPromptFragment());
    }

    [Fact]
    public void Factory_rejects_out_of_range_and_inverted_boxes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PanelAnnotation.Box(-1, 0, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => PanelAnnotation.Box(0, 0, 1000, 10));
        Assert.Throws<ArgumentException>(() => PanelAnnotation.Box(50, 0, 10, 10));
    }

    // ── DescribeForComposer ────────────────────────────────────

    [Fact]
    public void Describes_each_annotation_on_its_own_line()
    {
        var text = PanelAnnotation.DescribeForComposer(new[]
        {
            PanelAnnotation.Box(179, 283, 796, 986),
            PanelAnnotation.Point(518, 135),
        });

        // 每行一个：用户是这条链路的最终编辑者，删掉不想改的那行最省事。
        Assert.Equal("图一 <bbox>179 283 796 986</bbox>\n图一 <point>518 135</point>", text);
    }

    [Fact]
    public void Describes_nothing_as_empty_string()
    {
        Assert.Equal(string.Empty, PanelAnnotation.DescribeForComposer(Array.Empty<PanelAnnotation>()));
    }

    // ── TryParse ───────────────────────────────────────────────

    [Fact]
    public void Parses_a_mixed_payload()
    {
        const string json = """
            [{"kind":"box","x1":10,"y1":20,"x2":30,"y2":40},
             {"kind":"point","x":500,"y":600}]
            """;

        Assert.True(PanelAnnotation.TryParse(json, out var items, out var error), error);

        Assert.Equal(2, items.Count);
        Assert.Equal(PanelAnnotationKind.Box, items[0].Kind);
        Assert.Equal(PanelAnnotationKind.Point, items[1].Kind);
    }

    [Fact]
    public void Accepts_an_empty_array_as_no_annotations()
    {
        Assert.True(PanelAnnotation.TryParse("[]", out var items, out _));

        Assert.Empty(items);
    }

    [Fact]
    public void Rejects_a_non_array_payload()
    {
        Assert.False(PanelAnnotation.TryParse("""{"kind":"box"}""", out _, out var error));

        Assert.Contains("array", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_more_items_than_the_cap()
    {
        var items = string.Join(',', Enumerable.Repeat("""{"kind":"point","x":1,"y":1}""", PanelAnnotation.MaxItems + 1));

        Assert.False(PanelAnnotation.TryParse($"[{items}]", out _, out var error));

        Assert.Contains("at most", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_an_unknown_kind()
    {
        Assert.False(PanelAnnotation.TryParse("""[{"kind":"arrow","x":1,"y":1}]""", out _, out var error));

        Assert.Contains("arrow", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_coordinate_outside_the_range_instead_of_clamping_it()
    {
        // 夹取会让「算错了」变成「看起来合法的坐标」，用户永远查不出问题在哪。
        Assert.False(PanelAnnotation.TryParse(
            """[{"kind":"point","x":1200,"y":1}]""", out _, out var error));

        Assert.Contains("0..999", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_non_integer_coordinate()
    {
        Assert.False(PanelAnnotation.TryParse(
            """[{"kind":"point","x":1.5,"y":1}]""", out _, out _));
    }

    [Fact]
    public void Rejects_an_inverted_box()
    {
        Assert.False(PanelAnnotation.TryParse(
            """[{"kind":"box","x1":50,"y1":0,"x2":10,"y2":10}]""", out _, out var error));

        Assert.Contains("ordered", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_the_whole_payload_when_one_item_is_bad()
    {
        // 半份标注比没有标注更危险：它看起来像一份完整的编辑意图。
        const string json = """
            [{"kind":"point","x":1,"y":1},{"kind":"nope","x":2,"y":2}]
            """;

        Assert.False(PanelAnnotation.TryParse(json, out var items, out _));

        Assert.Empty(items);
    }

    [Fact]
    public void Rejects_null_and_empty_payloads()
    {
        Assert.False(PanelAnnotation.TryParse(null, out _, out _));
        Assert.False(PanelAnnotation.TryParse("   ", out _, out _));
    }

    [Fact]
    public void Rejects_invalid_json_without_throwing()
    {
        Assert.False(PanelAnnotation.TryParse("[{not json", out _, out var error));

        Assert.Contains("JSON", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── TryParsePixels（页面只报像素，归一化在这边做）───────────

    [Fact]
    public void Normalizes_pixel_annotations_reported_by_the_page()
    {
        // 页面只报“鼠标在图片中的哪里”，换算唯一一份实现就在 C#：
        // 两份实现（JS + C#）必然漂移，而漂移后的坐标只会生成一张“改错地方”的图。
        const string json = """
            {"width":800,"height":400,
             "items":[{"kind":"box","x1":80,"y1":40,"x2":400,"y2":200},
                      {"kind":"point","x":400,"y":100}]}
            """;

        Assert.True(PanelAnnotation.TryParsePixels(json, out var items, out var error), error);

        Assert.Equal(2, items.Count);
        Assert.Equal("<bbox>100 100 500 500</bbox>", items[0].ToPromptFragment());
        Assert.Equal("<point>500 250</point>", items[1].ToPromptFragment());
    }

    [Fact]
    public void Clamps_pixels_dragged_outside_the_image()
    {
        // 拖到图片外面一点点是常态，夹到边上仍然是有意义的框。
        const string json = """
            {"width":800,"height":400,"items":[{"kind":"box","x1":-30,"y1":-10,"x2":900,"y2":500}]}
            """;

        Assert.True(PanelAnnotation.TryParsePixels(json, out var items, out _));

        Assert.Equal("<bbox>0 0 999 999</bbox>", items[0].ToPromptFragment());
    }

    [Fact]
    public void Rejects_a_zero_sized_image_because_the_canvas_may_not_have_loaded()
    {
        const string json = """
            {"width":0,"height":400,"items":[{"kind":"point","x":1,"y":1}]}
            """;

        Assert.False(PanelAnnotation.TryParsePixels(json, out _, out var error));

        Assert.Contains("width", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_a_pixel_payload_without_items()
    {
        Assert.False(PanelAnnotation.TryParsePixels(
            """{"width":800,"height":400}""", out _, out var error));

        Assert.Contains("items", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── 整份载荷：除了标注，还要知道改的是哪张图 ───────────────

    [Fact]
    public void Canvas_payload_carries_the_source_image_uri()
    {
        const string json = """
            {"src":"pudding-image:///D:/Data/a.jpg","width":800,"height":800,
             "items":[{"kind":"point","x":10,"y":20}]}
            """;

        Assert.True(PanelAnnotation.TryParseCanvas(json, out var payload, out var error), error);

        Assert.Equal("pudding-image:///D:/Data/a.jpg", payload.SourceUri);
        Assert.Single(payload.Items);
        Assert.Equal(800, payload.Width);
    }

    [Fact]
    public void Canvas_payload_allows_a_missing_source()
    {
        // 页面里没有图（纯 HTML 面板）时 src 不存在，不属于错误。
        const string json = """
            {"width":800,"height":800,"items":[{"kind":"point","x":10,"y":20}]}
            """;

        Assert.True(PanelAnnotation.TryParseCanvas(json, out var payload, out _));

        Assert.Null(payload.SourceUri);
    }

    // ── DescribeForComposer 带来源 ───────────────────────────────

    [Fact]
    public void Describe_prefixes_the_verified_source_path()
    {
        var text = PanelAnnotation.DescribeForComposer(
            new[] { PanelAnnotation.Box(100, 100, 400, 400) },
            "pudding-image:///D:/Data/shots/dog.jpg",
            new[] { @"D:\Data" });

        // 带上路径后，“改哪张图”不再依赖对话上下文。
        Assert.Equal("【编辑 D:\\Data\\shots\\dog.jpg】\n图一 <bbox>100 100 400 400</bbox>", text);
    }

    [Fact]
    public void Describe_omits_an_unverifiable_source_instead_of_trusting_the_page()
    {
        // 页面由 Agent 生成；把 src 原样塞进输入框，等于让页面能往用户
        // 即将发送的消息里写任意内容。白名单外的、data: 的、乱写的都不进文本。
        var items = new[] { PanelAnnotation.Point(1, 2) };

        Assert.Equal("图一 <point>1 2</point>",
            PanelAnnotation.DescribeForComposer(items, "pudding-image:///C:/Windows/x.jpg", new[] { @"D:\Data" }));

        Assert.Equal("图一 <point>1 2</point>",
            PanelAnnotation.DescribeForComposer(items, "data:image/png;base64,AAAA", new[] { @"D:\Data" }));

        Assert.Equal("图一 <point>1 2</point>",
            PanelAnnotation.DescribeForComposer(items, null, new[] { @"D:\Data" }));
    }
}
