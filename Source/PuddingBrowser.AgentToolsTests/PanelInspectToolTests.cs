using System.Text.Json;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.AgentTools;
using PuddingCode.Tools;
using PuddingCore.Panels;

namespace PuddingBrowser.AgentToolsTests;

/// <summary>
/// 交互面板的**读取面 / 列举 / 删除**与 <c>htmlPath</c> 发布。
///
/// <para>
/// 这套工具存在的原因：只用 <c>render_panel</c> 时，「现在画布上到底是什么」没有工具能回答，
/// 每次核对都得靠外部手段（curl、翻日志）—— 那是一次性手段被当成常规操作。
/// </para>
/// <para>
/// 这里跑的是工具本体：<c>PuddingToolBase</c> 或参数契约选错只在真正执行时暴露。
/// </para>
/// </summary>
public sealed class PanelInspectToolTests
{
    private static ToolExecutionRequest Request(string argumentsJson, string? workingDirectory = null) => new()
    {
        ToolCallId = "call-1",
        ArgumentsJson = argumentsJson,
        Context = new ToolExecutionContext
        {
            WorkspaceId = "default",
            SessionId = "session-1",
            AgentInstanceId = "agent-1",
            WorkingDirectory = workingDirectory,
        },
    };

    // ---------- read_panel ----------

    [Fact]
    public async Task Read_ReportsAbsentCanvasAsNotAnError()
    {
        var store = new InMemoryPanelCanvasStore();

        var result = await new ReadPanelTool(store).ExecuteAsync(Request("{}"));

        // 没有画布不是失败：它只是还没有内容。
        Assert.True(result.Success, result.Error);
        Assert.Contains("\"exists\":false", result.Output, StringComparison.Ordinal);
        Assert.Contains("main", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_ReturnsMetadataWithoutContentByDefault()
    {
        var store = new InMemoryPanelCanvasStore();
        store.Publish("main", "<p>hello</p>", "汽车架构图");

        var result = await new ReadPanelTool(store).ExecuteAsync(Request("{}"));

        Assert.True(result.Success, result.Error);
        Assert.Contains("\"revision\":1", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"contentLength\":12", result.Output, StringComparison.Ordinal);
        // 默认不把正文拉进上下文。
        Assert.DoesNotContain("<p>hello</p>", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_CanPreviewAPrefixAndSaysWhetherItWasTruncated()
    {
        var store = new InMemoryPanelCanvasStore();
        store.Publish("main", "<p>hello</p>");

        var result = await new ReadPanelTool(store).ExecuteAsync(Request("""{"maxChars":5}"""));

        Assert.True(result.Success, result.Error);
        // 预览按 5 个字符截断。不断言具体字符：System.Text.Json 会把 < > 转义成 \u003C \u003E，
        // 按原文断言只会得到一条“看起来在验、实际验不到”的假绿。
        Assert.Contains("\"previewTruncated\":true", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"contentLength\":12", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_CapsThePreviewSoALargeCanvasCannotFloodTheConversation()
    {
        var store = new InMemoryPanelCanvasStore();
        store.Publish("main", new string('x', 20000));

        var result = await new ReadPanelTool(store).ExecuteAsync(Request("""{"maxChars":99999}"""));

        Assert.True(result.Success, result.Error);
        // 8000 是上限；内容 20000 ⇒ 仍标记截断。
        Assert.Contains("\"previewTruncated\":true", result.Output, StringComparison.Ordinal);
        Assert.True(result.Output.Length < 20000, "preview must be capped below the full canvas size");
    }

    [Fact]
    public async Task Read_PrefersTheStoredCanonicalSpellingOfThePanelId()
    {
        var store = new InMemoryPanelCanvasStore();
        store.Publish("Design", "<p>x</p>");

        var result = await new ReadPanelTool(store).ExecuteAsync(Request("""{"panelId":"design"}"""));

        Assert.True(result.Success, result.Error);
        // 存储保留首次发布的书写形式，读取方不该被大小写差异误导。
        Assert.Contains("\"panelId\":\"Design\"", result.Output, StringComparison.Ordinal);
    }

    // ---------- list_panels ----------

    [Fact]
    public async Task List_IsEmptyBeforeAnythingIsPublished()
    {
        var result = await new ListPanelsTool(new InMemoryPanelCanvasStore())
            .ExecuteAsync(Request("{}"));

        Assert.True(result.Success, result.Error);
        Assert.Contains("\"count\":0", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ReportsEveryCanvasWithoutContent()
    {
        var store = new InMemoryPanelCanvasStore();
        store.Publish("main", "<p>one</p>");
        store.Publish("design", "<p>two</p>", "设计稿");

        var result = await new ListPanelsTool(store).ExecuteAsync(Request("{}"));

        Assert.True(result.Success, result.Error);
        Assert.Contains("\"count\":2", result.Output, StringComparison.Ordinal);
        Assert.Contains("design", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>one</p>", result.Output, StringComparison.Ordinal);
    }

    // ---------- clear_panel ----------

    [Fact]
    public async Task Clear_RemovesTheCanvasAndIsIdempotent()
    {
        var store = new InMemoryPanelCanvasStore();
        store.Publish("main", "<p>x</p>");
        var tool = new ClearPanelTool(store);

        var first = await tool.ExecuteAsync(Request("{}"));
        var second = await tool.ExecuteAsync(Request("{}"));

        Assert.True(first.Success, first.Error);
        Assert.Contains("\"removed\":true", first.Output, StringComparison.Ordinal);

        // 再删一次是幂等：报 removed=false，不是报错。
        Assert.True(second.Success, second.Error);
        Assert.Contains("\"removed\":false", second.Output, StringComparison.Ordinal);

        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Clear_ThenRead_SeesAGoneCanvas()
    {
        var store = new InMemoryPanelCanvasStore();
        store.Publish("main", "<p>x</p>");

        await new ClearPanelTool(store).ExecuteAsync(Request("{}"));
        var read = await new ReadPanelTool(store).ExecuteAsync(Request("{}"));

        Assert.True(read.Success, read.Error);
        Assert.Contains("\"exists\":false", read.Output, StringComparison.Ordinal);
    }

    // ---------- render_panel htmlPath ----------

    [Fact]
    public async Task Render_LoadsHtmlFromAFileInsideTheWorkingDirectory()
    {
        using var dir = new TempDirectory();
        var file = dir.Write("car.html", "<svg><rect width=\"1\"/></svg>");
        var store = new InMemoryPanelCanvasStore();
        var surface = new FakeSurface();

        var result = await new RenderPanelTool(store, surface, new FakeCallContexts())
            .ExecuteAsync(Request($$"""{"htmlPath":"{{Escape(file)}}"}""", dir.Path));

        Assert.True(result.Success, result.Error);
        Assert.Equal("<svg><rect width=\"1\"/></svg>", store.Get("main")!.Content);
        // 正文没有穿过工具参数，但它确实到了桌面。
        Assert.Equal("<svg><rect width=\"1\"/></svg>", surface.LastRequest!.Content);
    }

    [Fact]
    public async Task Render_RejectsBothSourcesAtOnce()
    {
        using var dir = new TempDirectory();
        var file = dir.Write("a.html", "<p>file</p>");
        var store = new InMemoryPanelCanvasStore();
        var surface = new FakeSurface();

        var result = await new RenderPanelTool(store, surface, new FakeCallContexts())
            .ExecuteAsync(Request($$"""{"html":"<p>inline</p>","htmlPath":"{{Escape(file)}}"}""", dir.Path));

        // 两个都给时不许静默挑一个：那会让「我以为发布的是文件」变成真事故。
        Assert.False(result.Success);
        Assert.Null(store.Get("main"));
        Assert.Null(surface.LastRequest);
    }

    [Fact]
    public async Task Render_RejectsMissingSource()
    {
        var result = await new RenderPanelTool(
                new InMemoryPanelCanvasStore(), new FakeSurface(), new FakeCallContexts())
            .ExecuteAsync(Request("{}"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Render_RefusesAPathOutsideTheWorkingDirectory()
    {
        using var dir = new TempDirectory();
        var outside = Path.Combine(dir.Path, "..", "outside.html");
        await File.WriteAllTextAsync(outside, "<p>secret</p>");
        var store = new InMemoryPanelCanvasStore();
        var surface = new FakeSurface();

        var result = await new RenderPanelTool(store, surface, new FakeCallContexts())
            .ExecuteAsync(Request("""{"htmlPath":"../outside.html"}""", dir.Path));

        Assert.False(result.Success);
        Assert.Null(store.Get("main"));
        Assert.Null(surface.LastRequest);
    }

    [Fact]
    public async Task Render_ReportsAMissingFileRatherThanPublishingNothing()
    {
        using var dir = new TempDirectory();

        var result = await new RenderPanelTool(
                new InMemoryPanelCanvasStore(), new FakeSurface(), new FakeCallContexts())
            .ExecuteAsync(Request("""{"htmlPath":"nope.html"}""", dir.Path));

        Assert.False(result.Success);
        Assert.Contains("nope.html", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_WithoutAWorkingDirectory_StillAcceptsAnAbsolutePath()
    {
        using var dir = new TempDirectory();
        var file = dir.Write("a.html", "<p>x</p>");
        var store = new InMemoryPanelCanvasStore();
        var surface = new FakeSurface();

        var result = await new RenderPanelTool(store, surface, new FakeCallContexts())
            .ExecuteAsync(Request($$"""{"htmlPath":"{{Escape(file)}}"}"""));

        // 语义已修正：没有执行根时无法解析**相对**路径，但绝对路径不该被拒 ——
        // 内联 html 本来就能发布任意内容，放开绝对路径没有扩大能力边界，
        // 只是省掉「先读出来再整段贴进参数」。旧实现在这里直接 fail closed，
        // 使 htmlPath 在普通对话轮完全不可用（实测踩到过一次）。
        Assert.True(result.Success, result.Error);
        Assert.Equal("<p>x</p>", store.Get("main")!.Content);
    }

    [Fact]
    public async Task Render_WithoutAWorkingDirectory_RejectsARelativePath()
    {
        var store = new InMemoryPanelCanvasStore();
        var surface = new FakeSurface();

        var result = await new RenderPanelTool(store, surface, new FakeCallContexts())
            .ExecuteAsync(Request("""{"htmlPath":"car.html"}"""));

        // 没有根就没有可解析的基准，也不能拿进程当前目录去猜（那可能是 bin/）。
        Assert.False(result.Success);
        Assert.Contains("absolute", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(store.Get("main"));
        Assert.Null(surface.LastRequest);
    }

    [Fact]
    public async Task Read_RefusesToCutAnEmojiInHalf()
    {
        var store = new InMemoryPanelCanvasStore();
        // emoji 占两个 UTF-16 代码单元。maxChars=1 只能容下一半 —— 宁可不给预览，也不给半个。
        store.Publish("main", "\U0001F697车");

        var result = await new ReadPanelTool(store).ExecuteAsync(Request("""{"maxChars":1}"""));

        Assert.True(result.Success, result.Error);

        // 必须解析 JSON 再断言：直接把输出当文本找 U+FFFD 会恒成立（序列化输出的是转义序列），
        // 那就是一条“看起来在验、实际验不到”的假绿。
        using var document = JsonDocument.Parse(result.Output);
        var preview = document.RootElement.GetProperty("value").GetProperty("contentPreview");
        Assert.Equal(JsonValueKind.Null, preview.ValueKind);
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    /// <summary>测试用临时根：用完即删，不给主机留垃圾。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pudding-panel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, string content)
        {
            var full = System.IO.Path.Combine(Path, name);
            File.WriteAllText(full, content);
            return full;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响测试结论。
            }
        }
    }

    private sealed class FakeSurface : IDesktopPanelCapabilitySurface
    {
        public DesktopPanelRenderRequest? LastRequest { get; private set; }

        public Task<CapabilityResult<DesktopPanelRenderResult>> RenderPanelAsync(
            DesktopPanelRenderRequest request,
            DesktopCallContext call,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(CapabilityResult<DesktopPanelRenderResult>.Success(
                new DesktopPanelRenderResult(request.PanelId, DesktopPanelRenderStatus.Rendered)));
        }
    }

    private sealed class FakeCallContexts : IDesktopCapabilityCallContextFactory
    {
        public DesktopCallContext? TryCreate(TimeSpan? timeout = null) => NewCall(timeout);

        public DesktopCallContext? TryCreate(string? permissionEvidenceSummary, TimeSpan? timeout = null)
            => NewCall(timeout);

        private static DesktopCallContext NewCall(TimeSpan? timeout) => new(
            new DesktopInstanceId("desk-1"),
            new OperationId($"op-{Guid.NewGuid():N}"),
            DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30)));
    }
}
