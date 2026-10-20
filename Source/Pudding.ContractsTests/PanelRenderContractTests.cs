using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>
/// 面板渲染契约的形状约束。
///
/// 为什么需要这一组：本轮最初只写了「请求参数 + 属性 + 工厂」，**漏了构造器里的赋值**，
/// 结果 <c>ForPanelRender(...)</c> 造出来的 union 里 PanelRender 恒为 null。
/// 它编译通过、既有 219 条契约测试全绿（没人碰过那个属性），
/// 直到 DesktopService 分发测试报出 "payload is missing" 才暴露。
/// 因此这里专门钉住「工厂 → 属性」这一段，让同类漏写立刻红。
/// </summary>
public sealed class PanelRenderContractTests
{
    private const string Html = "<p>hello</p>";

    [Fact]
    public void ForPanelRender_PutsThePayloadOnTheUnion()
    {
        var request = DesktopCapabilityRequest.ForPanelRender(
            new DesktopPanelRenderRequest("main", Html, title: "示例"));

        Assert.NotNull(request.PanelRender);
        Assert.Equal("main", request.PanelRender!.PanelId);
        Assert.Equal(Html, request.PanelRender.Content);
        Assert.Equal("示例", request.PanelRender.Title);

        // 唯一载荷不变量：其它成员必须为 null（否则分发会按顺序命中错误分支）。
        Assert.Null(request.DocumentPreview);
        Assert.Null(request.Navigate);
        Assert.Null(request.Notification);
        Assert.False(request.ShellStatus);
        Assert.False(request.Contexts);
    }

    [Fact]
    public void FromPanelRender_PutsThePayloadOnTheResponse()
    {
        var response = DesktopCapabilityResponse.FromPanelRender(
            new DesktopPanelRenderResult("main", DesktopPanelRenderStatus.Replaced));

        Assert.NotNull(response.PanelRender);
        Assert.Equal("main", response.PanelRender!.PanelId);
        Assert.Equal(DesktopPanelRenderStatus.Replaced, response.PanelRender.Status);
        Assert.Null(response.Error);
        Assert.Null(response.DocumentPreview);
    }

    [Fact]
    public void Response_DistinguishesPanelRenderFromFailure()
    {
        // 构造器是私有的，「两个载荷同时给」在语言层面已经不可达；
        // 真正会漂的是 ToString/分发依赖的分支识别，以及「失败时不得带载荷」。
        var ok = DesktopCapabilityResponse.FromPanelRender(
            new DesktopPanelRenderResult("main", DesktopPanelRenderStatus.Rendered));
        Assert.StartsWith("panel_render(", ok.ToString());

        var failed = DesktopCapabilityResponse.Failure(DesktopCapabilityError.InvalidRequest("x"));
        Assert.Null(failed.PanelRender);
        Assert.StartsWith("failure(", failed.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PanelId_MustBeNonEmpty(string panelId)
    {
        Assert.Throws<ArgumentException>(() => new DesktopPanelRenderRequest(panelId, Html));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("中文")]
    public void PanelId_RejectsCharactersThatWouldBreakTabIdentity(string panelId)
    {
        Assert.Throws<ArgumentException>(() => new DesktopPanelRenderRequest(panelId, Html));
    }

    [Fact]
    public void PanelId_AllowsDotsUnderscoresAndDashes()
    {
        var request = new DesktopPanelRenderRequest("design.v2_a-b", Html);
        Assert.Equal("design.v2_a-b", request.PanelId);
    }

    [Fact]
    public void PanelId_HasAnUpperBound()
    {
        var tooLong = new string('a', DesktopPanelRenderRequest.MaxPanelIdLength + 1);
        Assert.Throws<ArgumentException>(() => new DesktopPanelRenderRequest(tooLong, Html));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Content_MustBeNonEmpty(string content)
    {
        Assert.Throws<ArgumentException>(() => new DesktopPanelRenderRequest("main", content));
    }

    [Fact]
    public void Content_HasAnUpperBound()
    {
        var tooLong = new string('x', DesktopPanelRenderRequest.MaxContentLength + 1);
        Assert.Throws<ArgumentException>(() => new DesktopPanelRenderRequest("main", tooLong));

        // 边界值本身必须放行：否则「多大的内容还能送」就成了不可测的魔数。
        var atLimit = new string('x', DesktopPanelRenderRequest.MaxContentLength);
        Assert.Equal(DesktopPanelRenderRequest.MaxContentLength, new DesktopPanelRenderRequest("main", atLimit).Content.Length);
    }

    [Theory]
    [InlineData("markdown")]
    [InlineData("HTML")]
    [InlineData("text/plain")]
    [InlineData("")]
    public void ContentType_OnlyAcceptsTheHtmlWireName(string contentType)
    {
        // 大小写也不放过：线名是协议的一部分，不做「差不多就行」的归一会少一类争议。
        Assert.Throws<ArgumentException>(() => new DesktopPanelRenderRequest("main", Html, contentType));
    }

    [Fact]
    public void ToString_NeverEchoesContent()
    {
        var secret = "<p>user-secret-42</p>";
        var request = new DesktopPanelRenderRequest("main", secret);

        Assert.DoesNotContain("user-secret-42", request.ToString());
        Assert.Contains($"contentChars={secret.Length}", request.ToString());
    }

    [Fact]
    public void StatusWire_IsFrozenAndUnknownFallsBackToRendered()
    {
        Assert.Equal("rendered", DesktopPanelRenderStatusWire.NameOf(DesktopPanelRenderStatus.Rendered));
        Assert.Equal("replaced", DesktopPanelRenderStatusWire.NameOf(DesktopPanelRenderStatus.Replaced));

        // 未知线名折叠为 Rendered：比「当作已替换」保守 —— 调用方知道内容已交付，但不必假设路径。
        Assert.Equal(DesktopPanelRenderStatus.Rendered, DesktopPanelRenderStatusWire.Parse("something-new"));
        Assert.Equal(DesktopPanelRenderStatus.Rendered, DesktopPanelRenderStatusWire.Parse(null));
        Assert.Equal(DesktopPanelRenderStatus.Replaced, DesktopPanelRenderStatusWire.Parse("replaced"));
    }
}
