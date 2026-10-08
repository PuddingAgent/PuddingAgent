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
