namespace PuddingDesktop.FoundationTests;

using PuddingDesktop.Foundation;

/// <summary>
/// 画布状态快照的契约：**形状不对就拒绝**，绝不猜。
///
/// <para>
/// 这份快照只用来纠正宿主工具栏的选中态。猜错的代价不是崩溃，而是
/// 「用户按了 B 换成笔、界面却仍然点亮着框」——一个不会报错、只会让人怀疑
/// 快捷键坏了的静默错位。所以宁可返回 false 让调用方保持现状。
/// </para>
///
/// <para>
/// 反过来说，<c>tool</c> 是唯一必需字段：将来页面加了新工具而宿主还不认识它时，
/// 快照仍然要能读到（只是不对应任何按钮），否则宿主会一直停在旧状态。
/// </para>
/// </summary>
public sealed class PanelCanvasStateTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tool\":123}")]
    [InlineData("{\"tool\":null}")]
    [InlineData("{\"tool\":\"\"}")]
    [InlineData("{\"tool\":\"   \"}")]
    public void Malformed_snapshots_are_rejected_instead_of_guessed(string? json)
    {
        Assert.False(PanelCanvasState.TryParse(json, out var state));
        Assert.Null(state);
    }

    [Fact]
    public void Snapshot_reports_the_active_tool_and_the_annotation_count()
    {
        const string Json =
            "{\"tool\":\"pen\",\"active\":true,\"count\":7,\"canUndo\":true}";

        Assert.True(PanelCanvasState.TryParse(Json, out var state));
        Assert.Equal("pen", state.Tool);
        Assert.True(state.IsActive);
        Assert.Equal(7, state.Count);
        Assert.True(state.CanUndo);
    }

    [Fact]
    public void Optional_fields_default_to_idle_when_the_page_omits_them()
    {
        // 页面只报工具名也要能用：刚打开标注、一笔没画就是这个形状。
        Assert.True(PanelCanvasState.TryParse("{\"tool\":\"box\"}", out var state));
        Assert.Equal("box", state.Tool);
        Assert.False(state.IsActive);
        Assert.Equal(0, state.Count);
        Assert.False(state.CanUndo);
    }

    [Theory]
    // 非布尔 true 不算「开着」：JS 里 active 是布尔，字符串 'true' 说明发信方不对劲。
    [InlineData("{\"tool\":\"pen\",\"active\":\"true\"}")]
    // 负数/非数字/字符串数字都不能当成笔数，否则状态栏会显示「-3 笔」。
    [InlineData("{\"tool\":\"pen\",\"count\":-1}")]
    [InlineData("{\"tool\":\"pen\",\"count\":\"3\"}")]
    [InlineData("{\"tool\":\"pen\",\"count\":3.7}")]
    [InlineData("{\"tool\":\"pen\",\"canUndo\":1}")]
    public void Wrongly_typed_optional_fields_degrade_to_idle_not_to_an_error(string json)
    {
        Assert.True(PanelCanvasState.TryParse(json, out var state));
        Assert.False(state.IsActive);
        Assert.Equal(0, state.Count);
        Assert.False(state.CanUndo);
    }

    [Fact]
    public void An_unknown_tool_name_is_still_accepted()
    {
        // 页面先加上新工具、宿主后跟上是正常节奏。拒绝会让宿主永远停在旧选中态，
        // 比「没有按钮匹配这个工具」更糟 —— 后者至少用户能看出是没接线。
        Assert.True(PanelCanvasState.TryParse("{\"tool\":\"lasso\"}", out var state));
        Assert.Equal("lasso", state.Tool);
    }
}
