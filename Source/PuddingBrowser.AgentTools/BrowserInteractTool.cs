using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;

using PuddingCode.Models;

using PuddingCode.Tools;



namespace PuddingBrowser.AgentTools;



public sealed record BrowserInteractArgs

{

    [ToolParam("Action: click, fill, type, press, hover, scroll, select, or check.")]

    public required string Action { get; init; }

    [ToolParam("Browser page id to operate.")]

    public required string PageId { get; init; }

    public string? ContextId { get; init; }

    public BrowserLocatorInput? Locator { get; init; }

    [ToolParam("Text for fill/type, or key for press. Values are never returned in activity logs.")]

    public string? Text { get; init; }

    public IReadOnlyList<string>? Values { get; init; }

    public bool? Checked { get; init; }

    public double? DeltaX { get; init; }

    public double? DeltaY { get; init; }

}



[Tool(

    id: BrowserAgentToolIds.Interact,

    name: "Browser interact",

    description: "Click, fill, type, press, hover, scroll, select, or check an element in a visible Desktop browser tab.",

    category: ToolCategory.Network,

    permission: ToolPermissionLevel.Low,

    safety: ToolSafetyFlags.RequiresNetwork)]

public sealed class BrowserInteractTool(

    IDesktopBrowserCapabilitySurface surface,

    IDesktopCapabilityCallContextFactory callContexts,

    IBrowserOperationOriginAccessor originAccessor) : BrowserAgentToolBase<BrowserInteractArgs>(originAccessor)

{

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(

        BrowserInteractArgs args, ToolExecutionContext context, CancellationToken ct)

    {

        var action = args.Action?.Trim().ToLowerInvariant();

        if (action is not ("click" or "fill" or "type" or "press" or "hover" or "scroll" or "select" or "check"))

            return BrowserToolResponse.Failure("browser_invalid_arguments", "unsupported interaction action");

        try

        {

            // 形状校验：错误码与文案沿用迁移前（保持 Agent 可见行为）。

            var locator = args.Locator is null ? null : BrowserLocatorInputMapper.ToDesktopLocator(args.Locator);

            if (action != "scroll" && locator is null)

                return BrowserToolResponse.Failure("browser_invalid_arguments", "locator is required for this action");

            if (action == "press" && string.IsNullOrWhiteSpace(args.Text))

                return BrowserToolResponse.Failure("browser_invalid_arguments", "text key is required for press");

            if (action == "select" && args.Values is not { Count: > 0 })

                return BrowserToolResponse.Failure("browser_invalid_arguments", "values are required for select");

            if (action == "check" && args.Checked is null)

                return BrowserToolResponse.Failure("browser_invalid_arguments", "checked is required for check");





            if (callContexts.TryCreate(CurrentPermissionEvidenceSummary()) is not { } call)

            {

                return BrowserToolResponse.Failure(

                    "browser_not_connected", "No Desktop is connected for browser capabilities");

            }



            // 调用方给了上下文就直接用，不要先去列清单（宿主集成测试按调用序列断言会抓到）。

            var contextId = string.IsNullOrWhiteSpace(args.ContextId) ? null : args.ContextId!.Trim();

            if (contextId is null)

            {

                var contexts = await surface.GetContextsAsync(call, ct);

                if (contexts.IsFailure)

                {

                    return Failure(contexts.Error!);

                }



                contextId = contexts.Value.Contexts.Count > 0 ? contexts.Value.Contexts[0].ContextId : null;

                if (string.IsNullOrWhiteSpace(contextId))

                {

                    return BrowserToolResponse.Failure("browser_context_not_found", "No browser context is available");

                }

            }



            var target = new DesktopPageTarget(contextId, args.PageId.Trim());



            // 交互是变更类：契约要求**固定版本** ⇒ 先读一次当前状态作为基准

            // （与迁移前"直接作用于当前页"语义等价，且让"等待期间的页面变化"可被检出）。

            var state = await surface.GetPageStateAsync(target, call, ct);

            if (state.IsFailure)

            {

                return Failure(state.Error!);

            }



            if (!state.Value.Version.IsKnown)

            {

                return BrowserToolResponse.Failure("browser_page_not_found", "Page has no live version to act on");

            }



            var interaction = await surface.InteractAsync(

                new BrowserInteractRequest(

                    target,

                    MapAction(action, args.Checked),

                    state.Value.Version,

                    locator,

                    // fill/type 省略 text 时按**空文本**处理（= 清空输入框），与迁移前一致（缺口 #13）。
                    action is "fill" or "type" ? args.Text ?? string.Empty : args.Text,

                    args.Values,

                    args.Checked,

                    args.DeltaX,

                    args.DeltaY),

                call,

                ct);

            if (interaction.IsFailure)

            {

                return Failure(interaction.Error!);

            }



            var page = interaction.Value.Page;

            return BrowserToolResponse.Success(

                new BrowserInteractionToolValue

                {

                    Action = action,

                    Page = new BrowserTabToolValue

                    {

                        ContextId = contextId,

                        PageId = args.PageId.Trim(),

                        Title = page.Title ?? string.Empty,

                        Url = page.Url?.AbsoluteUri ?? string.Empty,

                        PageVersion = page.Version.IsKnown ? page.Version.Value : 0,

                    },

                    // 受影响元素由端口在**动作之前**解析（不违反"交互后不得重查旧 Locator"）⇒ 如实回带；

                    // 迁移前这里恒为 null，现在与能力通道的结果一致。

                    Element = interaction.Value.Element is { } element ? ToValue(element) : null,

                },

                new BrowserContextId(contextId),

                new PageId(args.PageId.Trim()),

                page.Version.IsKnown ? page.Version.Value : null);

        }

        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }

        catch (BrowserOperationException ex) { return BrowserToolResponse.FromException(ex); }

    }



    /// <summary>工具动作名 → 契约动作。`check` 由 `checked` 的真假决定 Check/Uncheck（与迁移前一致）。</summary>

    private static DesktopInteractionAction MapAction(string action, bool? isChecked) => action switch

    {

        "click" => DesktopInteractionAction.Click,

        "fill" => DesktopInteractionAction.Fill,

        "type" => DesktopInteractionAction.Type,

        "press" => DesktopInteractionAction.Press,

        "hover" => DesktopInteractionAction.Hover,

        "scroll" => DesktopInteractionAction.Scroll,

        "select" => DesktopInteractionAction.Select,

        _ => isChecked == false ? DesktopInteractionAction.Uncheck : DesktopInteractionAction.Check,

    };



    private static BrowserElementToolValue ToValue(DesktopElementRef element) => new()

    {

        Ref = element.Reference,

        Tag = element.Tag,

        Role = element.Role,

        Name = element.Name,

        Text = element.Text,

        Visible = element.Visible,

        Enabled = element.Enabled,

        Checked = element.IsChecked,

        BoundingBox = element.BoundingBox is { } box

            ? new BoundingBox { X = box.X, Y = box.Y, Width = box.Width, Height = box.Height }

            : null,

    };



    private static ToolExecutionResult Failure(DesktopCapabilityError error) =>

        BrowserCapabilityFailure.From(error, "browser_interact_failed", "stale_element_reference");

}

