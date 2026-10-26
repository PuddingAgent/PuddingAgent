using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

public sealed record BrowserEvaluateArgs
{
    [ToolParam("Browser page id to evaluate the script in.")]
    public required string PageId { get; init; }

    [ToolParam("Optional browser context id. Use it to disambiguate page ids.")]
    public string? ContextId { get; init; }

    [ToolParam("JavaScript expression or IIFE body to evaluate in the page. Wrap multi-statement code in (() => { ... })() and return a value.")]
    public required string Script { get; init; }

    [ToolParam("Optional page version this request is pinned to. Omit to use the current page version.")]
    public long? ExpectedPageVersion { get; init; }

    [ToolParam("Result size cap in bytes. Defaults to 32768 and is capped at 262144; larger results are truncated and flagged.")]
    public int MaxResultBytes { get; init; } = 32 * 1024;
}

/// <summary>
/// 只读定位的**页面内求值**：把「页面现在到底是什么状态」这件事，从"外挂 CDP 探针"变成一等工具。
///
/// 为什么需要它（2026-10-26 效率审计）：抖音评论/私信那几轮里，判断"字有没有写进去""按钮有没有出现"
/// 只能靠 PowerShell 连 CDP 自己发 `Runtime.evaluate`，每个判断要 2~3 次工具调用，还要绕开控制台 GBK 乱码
/// （改成回字符码点数组）。这条链路的契约（`IPuddingDesktopWebViewApi.ExecuteJavascriptAsync`）
/// 与 Desktop 实现本来就存在，缺的只是这一层。
///
/// 两条边界：
/// ① **不是只读保证**——JS 本身可以写页面。工具层能做的是收紧额度（脚本长度、结果字节数）与权限等级，
///    而不是假装它只读；真正的影响面由调用方的权限证据与页面自身的授权决定。
/// ② 结果是**裸 JSON 片段**（见 `JavascriptResult`），不再二次编码；超限即截断并如实置 `Truncated`。
/// </summary>
[Tool(
    id: BrowserAgentToolIds.Evaluate,
    name: "Browser evaluate",
    description: "Evaluate a JavaScript expression in a Desktop browser tab and return its value as JSON. Use it to inspect page state (element presence, values, computed styles) instead of guessing from screenshots.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Medium,
    safety: ToolSafetyFlags.RequiresNetwork)]
public sealed class BrowserEvaluateTool(
    IDesktopBrowserCapabilitySurface surface,
    IDesktopCapabilityCallContextFactory callContexts,
    IBrowserOperationOriginAccessor originAccessor) : BrowserAgentToolBase<BrowserEvaluateArgs>(originAccessor)
{
    /// <summary>脚本正文上限：够写一段探针，又不至于把页面当成脚本宿主。</summary>
    private const int MaxScriptChars = 64 * 1024;

    /// <summary>结果字节上限：工具结果会进入模型上下文，必须比契约的 4 MiB 紧得多。</summary>
    private const int MaxResultBytesCeiling = 256 * 1024;

    protected override async Task<ToolExecutionResult> ExecuteBrowserCoreAsync(
        BrowserEvaluateArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var script = args.Script?.Trim();
        if (string.IsNullOrEmpty(script))
        {
            return BrowserToolResponse.Failure(
                "browser_invalid_arguments", "script must be a non-empty JavaScript expression");
        }

        if (script.Length > MaxScriptChars)
        {
            return BrowserToolResponse.Failure(
                "browser_invalid_arguments", $"script must be at most {MaxScriptChars} characters");
        }

        if (args.MaxResultBytes is < 1 or > MaxResultBytesCeiling)
        {
            return BrowserToolResponse.Failure(
                "browser_invalid_arguments",
                $"max_result_bytes must be between 1 and {MaxResultBytesCeiling}");
        }

        try
        {
            if (callContexts.TryCreate(CurrentPermissionEvidenceSummary()) is not { } discoveryCall)
            {
                return BrowserToolResponse.Failure(
                    "browser_not_connected", "No Desktop is connected for browser capabilities");
            }

            // 调用方给了上下文就直接用，不要先去列清单（宿主集成测试按调用序列断言会抓到）。
            var contextId = string.IsNullOrWhiteSpace(args.ContextId) ? null : args.ContextId!.Trim();
            if (contextId is null)
            {
                var contexts = await surface.GetContextsAsync(discoveryCall, ct);
                if (contexts.IsFailure)
                {
                    return BrowserCapabilityFailure.From(
                        contexts.Error!, "browser_evaluate_failed", "stale_page_version");
                }

                contextId = contexts.Value.Contexts.Count > 0 ? contexts.Value.Contexts[0].ContextId : null;
                if (string.IsNullOrWhiteSpace(contextId))
                {
                    return BrowserToolResponse.Failure("browser_context_not_found", "No browser context is available");
                }
            }

            var target = new DesktopPageTarget(contextId, args.PageId.Trim());
            var browserContextId = new BrowserContextId(contextId);
            var pageId = new PageId(args.PageId.Trim());

            // 与 navigate 同理：上面可能已经用掉一个 operation id，这里必须重新签发，
            // 否则会撞 broker 的「same operation id with a different payload」。
            if (callContexts.TryCreate(CurrentPermissionEvidenceSummary()) is not { } evaluateCall)
            {
                return BrowserToolResponse.Failure(
                    "browser_not_connected", "No Desktop is connected for browser capabilities");
            }

            // 不强钉版本（与 navigate/snapshot 一致）：契约用 Unknown 表达"当前版本即可"；
            // 调用方要更强的一致性时显式传 expected_page_version。
            var expectedVersion = args.ExpectedPageVersion is { } pinned and > 0
                ? DesktopPageVersion.Require(pinned)
                : DesktopPageVersion.Unknown;

            var evaluated = await surface.ExecuteJavascriptAsync(
                new JavascriptRequest(target, script, expectedVersion, args.MaxResultBytes),
                evaluateCall,
                ct);
            if (evaluated.IsFailure)
            {
                return BrowserCapabilityFailure.From(
                    evaluated.Error!, "browser_evaluate_failed", "stale_page_version");
            }

            var result = evaluated.Value;
            return BrowserToolResponse.Success(
                new BrowserEvaluateToolValue
                {
                    ContextId = contextId,
                    PageId = args.PageId.Trim(),
                    // 形态按契约原样回带（Undefined/Null/Boolean/Number/String/Json），不推断、不美化。
                    Kind = result.Kind.ToString().ToLowerInvariant(),
                    // JsonValue 是**裸 JSON 片段**：String 自带引号，Undefined/Null 为 null。
                    JsonValue = result.JsonValue,
                    Truncated = result.Truncated,
                },
                browserContextId,
                pageId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BrowserOperationException ex)
        {
            return BrowserToolResponse.FromException(ex);
        }
    }
}
