using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>
/// 元素输出字段白名单（设计方案 §6.2 的 <c>fields</c>）。
/// 核心字段恒在（没有 ref 的定位结果无法继续操作），可裁剪字段严格校验、未知即拒。
/// </summary>
public sealed class ElementFieldsTests
{
    [Fact]
    public void Default_KeepsEverything()
    {
        var fields = BrowserElementFields.Default;

        Assert.False(fields.IsExplicit);
        foreach (var name in BrowserElementFields.All)
        {
            Assert.True(fields.Includes(name), $"默认集合必须包含 {name}");
        }

        // 名字也要认得（否则调用方写了个不存在的字段名却不报错）。
        Assert.False(fields.Includes("no_such_field"));
    }

    [Fact]
    public void CoreFields_SurviveAnyWhitelist()
    {
        Assert.True(BrowserElementFields.TryParse(["name"], out var fields, out var unknown));

        Assert.Null(unknown);
        Assert.True(fields.IsExplicit);

        // 核心字段恒在：ref 是继续操作的唯一把手，visible/enabled 决定能不能动。
        foreach (var name in BrowserElementFields.Core)
        {
            Assert.True(fields.Includes(name), $"核心字段 {name} 必须恒在");
        }

        Assert.True(fields.Includes("name"));
        Assert.False(fields.Includes("text"));
        Assert.False(fields.Includes("boundingBox"));
    }

    [Fact]
    public void UnknownField_IsRejectedWithItsName()
    {
        // 静默忽略会让调用方以为"字段真的没了"，因此必须明确拒绝。
        Assert.False(BrowserElementFields.TryParse(["name", "nope"], out _, out var unknown));
        Assert.Equal("nope", unknown);
    }

    [Fact]
    public void EmptyOrBlankInput_MeansNoWhitelist()
    {
        // 全空白等价于没给：不能意外变成"只剩核心字段"。
        Assert.True(BrowserElementFields.TryParse([], out var empty, out _));
        Assert.False(empty.IsExplicit);

        Assert.True(BrowserElementFields.TryParse(["  ", ""], out var blank, out _));
        Assert.False(blank.IsExplicit);
    }

    [Fact]
    public void SnapshotFields_ShareTheSameParsingSemantics()
    {
        // 设计 §151：快照也有 fields。核心 = "能不能用/还有没有"（mode/truncated/truncationReason/nextCursor），
        // 可裁剪 = 三种表示 + 三个预算事实。
        Assert.True(BrowserSnapshotFields.TryParse(["domText"], out var fields, out var unknown));
        Assert.Null(unknown);

        foreach (var name in BrowserSnapshotFields.Core)
        {
            Assert.True(fields.Includes(name), $"核心字段 {name} 必须恒在");
        }

        Assert.True(fields.Includes("domText"));
        Assert.False(fields.Includes("html"));
        Assert.False(fields.Includes("accessibilityTree"));

        // 与元素白名单同一套解析语义：未知即拒、全空白等价于没给、默认不裁剪。
        Assert.False(BrowserSnapshotFields.TryParse(["nope"], out _, out var badName));
        Assert.Equal("nope", badName);

        Assert.True(BrowserSnapshotFields.TryParse([" "], out var blank, out _));
        Assert.False(blank.IsExplicit);

        Assert.True(BrowserSnapshotFields.Default.Includes("html"));
        Assert.False(BrowserSnapshotFields.Default.Includes("no_such_field"));

        // 名字必须与输出键一致（不含下划线）。
        foreach (var name in BrowserSnapshotFields.All)
        {
            Assert.DoesNotContain('_', name);
        }

        // 三种表示都必须是**可裁剪**的：否则"同次只返回一种表示"无法表达（§151）。
        foreach (var representation in new[] { "accessibilityTree", "domText", "html" })
        {
            Assert.Contains(representation, BrowserSnapshotFields.Selectable);
        }
    }

    [Fact]
    public void OutputFieldSet_IsReusableAcrossTools()
    {
        // 通用实现在 Contracts 里只有一份：各工具只提供"核心/可裁剪"两张清单。
        var custom = new OutputFieldSet(core: ["a", "b"], selectable: ["c", "d"]);

        Assert.Equal(["a", "b", "c", "d"], custom.All);
        Assert.True(custom.Includes("a"));
        Assert.True(custom.Includes("d"));
        Assert.False(custom.Includes("e"));

        Assert.True(custom.TryParse(["c"], out var parsed, out _));
        Assert.True(parsed.Includes("a"));   // 核心恒在
        Assert.True(parsed.Includes("c"));
        Assert.False(parsed.Includes("d"));

        Assert.Throws<ArgumentNullException>(() => new OutputFieldSet(null!, ["c"]));
    }

    [Fact]
    public void NamesAreTrimmed_AndCoverTheBigFields()
    {
        Assert.True(BrowserElementFields.TryParse([" name ", "text"], out var fields, out _));
        Assert.True(fields.Includes("name"));
        Assert.True(fields.Includes("text"));

        // payload 的大头必须都能被裁掉。
        foreach (var heavy in new[] { "name", "text", "boundingBox", "value", "selectedValues" })
        {
            Assert.Contains(heavy, BrowserElementFields.Selectable);
        }

        // 线名与工具输出的 JSON 键一一对应（名字里不许出现下划线，否则调用方照抄会失败）。
        foreach (var name in BrowserElementFields.All)
        {
            Assert.DoesNotContain('_', name);
        }
    }
}
