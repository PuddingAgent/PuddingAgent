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
    public void Scribble_trail_is_decimated_but_keeps_both_ends()
    {
        // 抽稀必须发生在归一化之后：先抽稀再换算，抽稀时丢掉的极值点会把外接框变小，
        // 而外接框是模型唯一看到的坐标。
        var pixels = Enumerable.Range(0, 501).Select(i => ((double)i, (double)i)).ToArray();

        var scribble = PanelAnnotation.ScribbleFromPixelTrail(pixels, 500, 500);

        Assert.Equal(PanelAnnotation.MaxInkPoints, scribble.Points!.Count);
        Assert.Equal(new PanelInkPoint(0, 0), scribble.Points[0]);
        Assert.Equal(new PanelInkPoint(999, 999), scribble.Points[^1]);
    }

    [Fact]
    public void Scribble_without_a_trail_still_produces_the_same_bbox()
    {
        // 老调用方（只知道外接框）与轨迹版本的 prompt 必须一致，
        // 否则同一笔涂鸦会因为走了哪条路径而给模型不同的坐标。
        var fromBox = PanelAnnotation.ScribbleFromPixels(80, 40, 400, 200, 800, 400);
        var fromTrail = PanelAnnotation.ScribbleFromPixelTrail(
            new[] { (80d, 40d), (240d, 120d), (400d, 200d) }, 800, 400);

        Assert.Equal(fromBox.ToPromptFragment(), fromTrail.ToPromptFragment());
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

    [Fact]
    public void Canvas_payload_keeps_display_and_natural_size_apart()
    {
        // 显示尺寸是坐标换算的分母，自然尺寸只用于记录文件多大。
        // 混用会让标注整体偏移（拿原始像素去除以显示坐标）。
        const string json = """
            {"width":1123,"height":1123,"naturalWidth":2048,"naturalHeight":2048,
             "items":[{"kind":"point","x":10,"y":20}]}
            """;

        Assert.True(PanelAnnotation.TryParseCanvas(json, out var payload, out var error), error);

        Assert.Equal(1123, payload.Width);
        Assert.Equal(2048, payload.NaturalWidth);
        Assert.Equal(2048, payload.NaturalHeight);
    }

    [Fact]
    public void Canvas_payload_treats_natural_size_as_optional()
    {
        // 旧画布或手工构造的载荷没有这两个字段，不能因此整份被拒。
        const string json = """
            {"width":800,"height":800,"items":[]}
            """;

        Assert.True(PanelAnnotation.TryParseCanvas(json, out var payload, out var error), error);

        Assert.Equal(0, payload.NaturalWidth);
        Assert.Equal(0, payload.NaturalHeight);
    }

    [Theory]
    [InlineData("\"naturalWidth\":-5")]
    [InlineData("\"naturalWidth\":0")]
    [InlineData("\"naturalWidth\":\"2048\"")]
    [InlineData("\"naturalWidth\":null")]
    public void Canvas_payload_degrades_a_bad_natural_size_to_zero(string fragment)
    {
        // 辅助字段非法就退化成 0，让调用方回退到显示尺寸；
        // 为一个「记录用」字段拒绝整份载荷，等于让用户白画一遍标注。
        var json = $$"""{"width":800,"height":800,{{fragment}},"items":[]}""";

        Assert.True(PanelAnnotation.TryParseCanvas(json, out var payload, out var error), error);

        Assert.Equal(0, payload.NaturalWidth);
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

    // ── 新图元：箭头 / 文字 / 涂鸦 ───────────────────────────────

    [Fact]
    public void Arrow_becomes_two_points_because_the_protocol_has_no_line_marker()
    {
        // 官方交互编辑指南只定义 <point> 和 <bbox> 两种标记，没有「线」。
        var arrow = PanelAnnotation.Arrow(100, 200, 800, 600);

        Assert.Equal(
            "箭头 <point>100 200</point> → <point>800 600</point>",
            arrow.ToPromptFragment());
    }

    [Fact]
    public void Arrow_preserves_the_draw_direction()
    {
        // 反向画的箭头不能被「整理」成正向：方向就是用户想说的那件事。
        var arrow = PanelAnnotation.Arrow(800, 600, 100, 200);

        Assert.Equal(
            "箭头 <point>800 600</point> → <point>100 200</point>",
            arrow.ToPromptFragment());
    }

    [Fact]
    public void Text_carries_the_typed_words_next_to_an_anchor()
    {
        var text = PanelAnnotation.Text("把这里换成蓝色", 400, 300);

        Assert.Equal("文字「把这里换成蓝色」 <point>400 300</point>", text.ToPromptFragment());
    }

    [Fact]
    public void Blank_text_is_rejected_rather_than_silently_downgraded()
    {
        // 没有字的文字标注没有任何信息量；静默当成点选会让用户以为写进去了。
        Assert.Throws<ArgumentException>(() => PanelAnnotation.Text("   ", 1, 2));
    }

    [Fact]
    public void Scribble_is_a_labelled_box_not_a_new_marker()
    {
        // 涂鸦的语义是「这一片」，与框选同构；轨迹本身对模型没有意义。
        var scribble = PanelAnnotation.Scribble(10, 20, 300, 400);

        Assert.Equal(PanelAnnotationKind.Box, scribble.Kind);
        Assert.Equal("涂鸦 <bbox>10 20 300 400</bbox>", scribble.ToPromptFragment());
    }

    [Fact]
    public void Text_label_drops_control_characters_and_is_capped()
    {
        // 标签会被拼进 prompt：控制字符能伪装出换行/覆盖效果，必须去掉。
        var withControl = PanelAnnotation.Text("上\n下\t左", 1, 2);
        Assert.Equal("文字「上下左」 <point>1 2</point>", withControl.ToPromptFragment());

        var longLabel = new string('长', PanelAnnotation.MaxLabelLength + 50);
        var capped = PanelAnnotation.Text(longLabel, 1, 2);
        Assert.Equal(PanelAnnotation.MaxLabelLength, capped.Label!.Length);
    }

    [Fact]
    public void Canvas_payload_parses_arrow_and_text_kinds()
    {
        const string json = """
            {"width":800,"height":800,"items":[
              {"kind":"arrow","x1":0,"y1":0,"x2":400,"y2":400},
              {"kind":"text","label":"换成蓝色","x":400,"y":400}]}
            """;

        Assert.True(PanelAnnotation.TryParseCanvas(json, out var payload, out var error), error);

        Assert.Equal(2, payload.Items.Count);
        Assert.Equal(PanelAnnotationKind.Arrow, payload.Items[0].Kind);
        // 起点 (0,0) 归一化仍是 0；终点 (400,400) 在 800px 图上是 500。
        Assert.Equal(500, payload.Items[0].X2);
        Assert.Equal(PanelAnnotationKind.Text, payload.Items[1].Kind);
        Assert.Equal("换成蓝色", payload.Items[1].Label);
    }

    [Fact]
    public void Canvas_payload_rejects_a_text_annotation_without_words()
    {
        const string json = """
            {"width":800,"height":800,"items":[{"kind":"text","x":1,"y":2}]}
            """;

        Assert.False(PanelAnnotation.TryParseCanvas(json, out _, out var error));
        Assert.Contains("label", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scribble_uses_the_bounding_box_of_its_stroke()
    {
        // 轨迹点只用来算外接框；模型不需要知道画了几笔。
        const string json = """
            {"width":800,"height":800,"items":[
              {"kind":"scribble","points":[{"x":100,"y":200},{"x":300,"y":100},{"x":200,"y":400}]}]}
            """;

        Assert.True(PanelAnnotation.TryParseCanvas(json, out var payload, out var error), error);
        var item = payload.Items[0];

        Assert.Equal(PanelAnnotationKind.Box, item.Kind);
        Assert.Equal(PanelAnnotation.ScribbleLabel, item.Label);
        // 包围盒 x 100..300 在 800px 上为 125..375；y 100..400 为 125..500。
        Assert.Equal("涂鸦 <bbox>125 125 375 500</bbox>", item.ToPromptFragment());
    }

    [Fact]
    public void Scribble_without_points_is_rejected()
    {
        const string json = """
            {"width":800,"height":800,"items":[{"kind":"scribble","points":[]}]}
            """;

        Assert.False(PanelAnnotation.TryParseCanvas(json, out _, out var error));
        Assert.Contains("at least one point", error!, StringComparison.OrdinalIgnoreCase);
    }
}
