using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingCore.Documents;

namespace PuddingBrowser.AgentTools;

public sealed record PreviewDocumentArgs
{
    [ToolParam("Workspace-relative path, e.g. Docs/design.md. Absolute paths, drive letters and '..' are rejected.")]
    public required string Path { get; init; }

    [ToolParam("Optional tab title. Defaults to the file name.")]
    public string? Title { get; init; }
}

/// <summary>
/// 在桌面工具区打开一份文档。
///
/// 边界（与 <c>read_office_document</c> 的分工）：本工具**不读正文、不回传内容**，
/// 只把相对路径交给 Desktop，由它自己读盘与渲染 ⇒ 不产生文件 I/O，
/// 也不会把用户文档内容带进对话上下文与审计。
///
/// **为什么继承 <see cref="PuddingToolBase{TArgs}"/> 而不是 BrowserAgentToolBase**：
/// 后者在 `ExecuteCoreAsync` 里强制要求浏览器调用者身份（`BrowserCallerIdentityScope.Current`），
/// 没有就 fail-closed。文档预览不是浏览器操作 —— 能力层（DesktopService.ValidateAdmission）
/// 也只在能力映射到浏览器操作时才要求 caller ⇒ 继承它会让本工具**永远失败**，
/// 且编译期与既有测试都看不出来。
///
/// 为什么留在这个组件：它依赖 <see cref="IDesktopDocumentCapabilitySurface"/> 与
/// Desktop 能力调用上下文工厂，而这两个接缝与工具基类都在本组件；
/// 另起工程只会多一份 DI 注册，隔离度并不会提高（边界已由 Contracts 承担）。
/// </summary>
[Tool(
    id: DesktopDocumentToolIds.Preview,
    name: "Preview document",
    description: "Open a workspace document in the Desktop tool area (path only; the Desktop reads and renders it). Supports .md/.markdown/.txt. Re-opening the same path activates the existing tab instead of creating a second one.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe)]
public sealed class PreviewDocumentTool(
    IDesktopDocumentCapabilitySurface surface,
    IDesktopCapabilityCallContextFactory callContexts) : PuddingToolBase<PreviewDocumentArgs>
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        PreviewDocumentArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        // 路径语义在这里拦一次；Desktop 收到后会用同一套策略再拦一次 —— 各守自己的边界，
        // 任何一方单独失效都不会让绝对路径或 traversal 穿透。
        if (!DocumentPathPolicy.TryNormalize(args.Path, out var normalized, out var errorCode, out var errorMessage))
        {
            return BrowserToolResponse.Failure(errorCode!, errorMessage!);
        }

        if (callContexts.TryCreate() is not { } call)
        {
            return BrowserToolResponse.Failure(
                "desktop_not_connected", "No Desktop is connected for document preview");
        }

        var result = await surface.OpenDocumentAsync(
            new DesktopDocumentPreviewRequest(normalized, args.Title), call, ct);

        if (result.IsFailure)
        {
            return BrowserToolResponse.Failure(result.Error!.WireCode, result.Error.Message);
        }

        // 只回身份与阶段：正文永不出现在结果里；accepted 只表示 Desktop 已受理，不代表已渲染。
        return BrowserToolResponse.Success(new DocumentPreviewToolValue
        {
            TabId = result.Value.TabId,
            Path = result.Value.Path,
            Status = DesktopDocumentPreviewStatusWire.NameOf(result.Value.Status),
        });
    }
}

/// <summary>工具结果：标签身份 + 回显路径 + 阶段线名。刻意不含正文。</summary>
public sealed record DocumentPreviewToolValue
{
    public required string TabId { get; init; }

    public required string Path { get; init; }

    public required string Status { get; init; }
}

public static class DesktopDocumentToolIds
{
    public const string Preview = "preview_document";
}
