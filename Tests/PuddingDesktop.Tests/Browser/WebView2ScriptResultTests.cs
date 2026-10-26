using System.Text.Json;
using PuddingBrowser.Abstractions;
using PuddingBrowser.WebView2;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// <c>IBrowserPage.EvaluateAsync</c> 的回执映射（<c>WebView2BrowserPage.MapScriptResult</c>）。
///
/// 背景：这条路径此前是 <c>throw new NotSupportedException("CDP not in Phase 2A-1")</c> 的占位，
/// 于是 <c>browser_evaluate</c> 工具虽然注册成功、一调用就报
/// <c>ui surface failed (NotSupportedException)</c>。映射抽成纯函数之后，
/// 可以脱离 WebView2 运行时验证「什么算成功、什么必须抛」。
/// </summary>
public sealed class WebView2ScriptResultTests
{
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void ScriptResultKeepsStringValueAndType()
    {
        var mapped = WebView2BrowserPage.MapScriptResult(
            Parse("""{"result":{"type":"string","value":"大肥鱼"}}"""));

        Assert.Equal("string", mapped.Type);
        Assert.Equal("大肥鱼", mapped.Value?.GetString());
    }

    [Fact]
    public void ScriptResultMapsObjectValueAsRawJson()
    {
        var mapped = WebView2BrowserPage.MapScriptResult(
            Parse("""{"result":{"type":"object","value":{"editable":2,"keys":["loadTimes","csi","app"]}}}"""));

        Assert.Equal("object", mapped.Type);
        Assert.Equal(JsonValueKind.Object, mapped.Value?.ValueKind);
        Assert.Equal(3, mapped.Value?.GetProperty("keys").GetArrayLength());
    }

    /// <summary>undefined 没有 <c>value</c>：保留形态，不编造一个 null 出来。</summary>
    [Fact]
    public void ScriptResultKeepsUndefinedWithoutInventingValue()
    {
        var mapped = WebView2BrowserPage.MapScriptResult(Parse("""{"result":{"type":"undefined"}}"""));

        Assert.Equal("undefined", mapped.Type);
        Assert.Null(mapped.Value);
    }

    /// <summary>页面抛异常必须抛出，而不是回一个「看起来正常」的 undefined。</summary>
    [Fact]
    public void ScriptResultThrowsWhenPageThrows()
    {
        var error = Assert.Throws<BrowserOperationException>(() =>
            WebView2BrowserPage.MapScriptResult(
                Parse("""{"result":{"type":"object"},"exceptionDetails":{"text":"Uncaught","exception":{"description":"ReferenceError: hi is not defined"}}}""")));

        Assert.Equal("browser_script_error", error.Code);
        Assert.Contains("ReferenceError", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptResultFallsBackToExceptionTextWhenNoDescription()
    {
        var error = Assert.Throws<BrowserOperationException>(() =>
            WebView2BrowserPage.MapScriptResult(Parse("""{"exceptionDetails":{"text":"Uncaught SyntaxError"}}""")));

        Assert.Contains("SyntaxError", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptResultThrowsWhenResultIsMissing()
    {
        var error = Assert.Throws<BrowserOperationException>(() =>
            WebView2BrowserPage.MapScriptResult(Parse("""{"id":1}""")));

        Assert.Equal("browser_script_error", error.Code);
    }

    [Fact]
    public void ScriptResultThrowsOnNonObjectPayload()
    {
        var error = Assert.Throws<BrowserOperationException>(() =>
            WebView2BrowserPage.MapScriptResult(Parse("[1,2,3]")));

        Assert.Equal("browser_script_error", error.Code);
    }

    /// <summary>
    /// 值必须在 <c>JsonDocument</c> 释放后仍可读——这就是实现里 <c>value.Clone()</c> 的理由。
    /// 去掉 Clone 会变成 <c>ObjectDisposedException</c>，所以这条用例是真守门人。
    /// </summary>
    [Fact]
    public void ScriptResultValueSurvivesSourceDocumentDisposal()
    {
        BrowserScriptValue mapped;
        using (var document = JsonDocument.Parse("""{"result":{"type":"string","value":"after-dispose"}}"""))
        {
            mapped = WebView2BrowserPage.MapScriptResult(document.RootElement);
        }

        Assert.Equal("after-dispose", mapped.Value?.GetString());
    }
}
