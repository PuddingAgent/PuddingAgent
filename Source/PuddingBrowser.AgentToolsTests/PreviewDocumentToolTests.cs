using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.AgentTools;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentToolsTests;

/// <summary>
/// preview_document 工具的行为边界：路径在**发命令之前**收敛、结果不含正文、
/// 未连接 Desktop 与能力失败都必须如实报错。
///
/// 这些用例的动机是实测过的一次误判：本工具最初继承 BrowserAgentToolBase，
/// 而它的 ExecuteCoreAsync 强制要求浏览器调用者身份 ⇒ 每次调用都会 fail-closed。
/// 编译期与既有测试都看不出来，只有真正执行一次才会暴露 —— 所以这里必须跑工具本体，
/// 而不是只测内部的纯函数。
/// </summary>
public sealed class PreviewDocumentToolTests
{
    private static ToolExecutionRequest Request(string argumentsJson) => new()
    {
        ToolCallId = "call-1",
        ArgumentsJson = argumentsJson,
        Context = new ToolExecutionContext
        {
            WorkspaceId = "default",
            SessionId = "session-1",
            AgentInstanceId = "agent-1",
        },
    };

    [Fact]
    public async Task HappyPath_NormalizesSeparatorsAndReturnsIdentityOnly()
    {
        var surface = new FakeSurface();
        var tool = new PreviewDocumentTool(surface, new FakeCallContexts());

        var result = await tool.ExecuteAsync(Request("""{"path":"Docs\\a.md","title":"设计稿"}"""));

        Assert.True(result.Success, result.Error);
        // 反斜杠归一：同一个文件在 Windows 客户端传过来也只有一个标签身份。
        Assert.Equal("Docs/a.md", surface.LastRequest!.Path);
        Assert.Equal("设计稿", surface.LastRequest.Title);
        Assert.Contains("document:Docs/a.md", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/etc/passwd.md")]
    [InlineData("C:/secret.md")]
    [InlineData("../escape.md")]
    [InlineData("Docs/a.pdf")]
    [InlineData("")]
    public async Task InvalidPaths_FailBeforeAnyDesktopCall(string path)
    {
        var surface = new FakeSurface();
        var tool = new PreviewDocumentTool(surface, new FakeCallContexts());

        var result = await tool.ExecuteAsync(Request($$"""{"path":"{{path}}"}"""));

        Assert.False(result.Success);
        // 关键断言：一条命令都不许发出去（fail closed 在发命令之前）。
        Assert.Null(surface.LastRequest);
    }

    [Fact]
    public async Task NoDesktop_FailsWithExplicitCode()
    {
        var tool = new PreviewDocumentTool(new FakeSurface(), new FakeCallContexts(available: false));

        var result = await tool.ExecuteAsync(Request("""{"path":"Docs/a.md"}"""));

        Assert.False(result.Success);
        Assert.Contains("desktop_not_connected", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapabilityFailure_IsReportedAsFailure()
    {
        var surface = new FakeSurface
        {
            Result = CapabilityResult<DesktopDocumentPreviewResult>.Failure(
                DesktopCapabilityError.UnsupportedCapability("shell.document_preview")),
        };
        var tool = new PreviewDocumentTool(surface, new FakeCallContexts());

        var result = await tool.ExecuteAsync(Request("""{"path":"Docs/a.md"}"""));

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    private sealed class FakeSurface : IDesktopDocumentCapabilitySurface
    {
        public DesktopDocumentPreviewRequest? LastRequest { get; private set; }

        public CapabilityResult<DesktopDocumentPreviewResult> Result { get; set; } =
            CapabilityResult<DesktopDocumentPreviewResult>.Success(new DesktopDocumentPreviewResult(
                "document:Docs/a.md", "Docs/a.md", DesktopDocumentPreviewStatus.Opened));

        public Task<CapabilityResult<DesktopDocumentPreviewResult>> OpenDocumentAsync(
            DesktopDocumentPreviewRequest request,
            DesktopCallContext call,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeCallContexts(bool available = true) : IDesktopCapabilityCallContextFactory
    {
        public DesktopCallContext? TryCreate(TimeSpan? timeout = null) =>
            available ? NewCall(timeout) : null;

        public DesktopCallContext? TryCreate(string? permissionEvidenceSummary, TimeSpan? timeout = null) =>
            available ? NewCall(timeout) : null;

        private static DesktopCallContext NewCall(TimeSpan? timeout) => new(
            new DesktopInstanceId("desk-1"),
            new OperationId($"op-{Guid.NewGuid():N}"),
            DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30)));
    }
}
