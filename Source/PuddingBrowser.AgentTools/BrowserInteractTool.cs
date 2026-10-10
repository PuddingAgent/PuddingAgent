using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;

using PuddingBrowser.Automation;

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

    /// <summary>拖拽终点（仅 action=drag 使用）。</summary>
    [ToolParam("Target locator for action=drag (required together with locator).")]
    public BrowserLocatorInput? ToLocator { get; init; }

    /// <summary>上传的文件（绝对路径；仅 action=upload 使用）。</summary>
    [ToolParam("Absolute file paths to upload (action=upload only).")]
    public List<string>? Files { get; init; }

    [ToolParam("Text for fill/type, or key for press. Values are never returned in activity logs.")]

    public string? Text { get; init; }

    public IReadOnlyList<string>? Values { get; init; }

    public bool? Checked { get; init; }

    public double? DeltaX { get; init; }

    public double? DeltaY { get; init; }

    /// <summary>
    /// 显式声明「动作完成后该元素的值应当是这个」——回执会据此给出 <c>verified</c>，
    /// 于是**一次调用就能确认写入成功**，不需要再发一次 locate/snapshot（设计方案 §4.1/§4.2、G7）。
    /// 表单值只以 <c>matched</c> + 长度形式回传，绝不回显内容。
    /// </summary>
    [ToolParam("Optional expected element value after the action; when set, the receipt reports verified/failed instead of dispatched. The value itself is never echoed.")]
    public string? ExpectValue { get; init; }

    /// <summary>
    /// 输入路径（设计方案 §7）：<c>dom</c>（默认，已验证适用）或 <c>native</c>（真实浏览器输入）。
    /// <b>native 尚未实现 ⇒ 请求它必须明确失败</b>，绝不静默降级成 dom 再回一个"成功"——
    /// 否则调用方会以为走了真实输入路径（设计明确禁止"静默切换语义"）。
    /// </summary>
    [ToolParam("Input path: dom (default, verified DOM events) or native (real browser input). native is not implemented yet and fails explicitly instead of silently falling back.")]
    public string? InputMode { get; init; }

    /// <summary>
    /// 滚动目标模型（§7，仅对 <c>scroll</c> 有意义）：<c>page</c>（滚文档，默认无 locator 时）/
    /// <c>element</c>（滚指定元素的滚动盒）/ <c>to_ref</c>（把元素滚入视野）/ <c>to_top</c> / <c>to_bottom</c>。
    /// 省略时按参数推导（有 locator 无位移 ⇒ <c>to_ref</c>，有 locator 有位移 ⇒ <c>element</c>，无 locator ⇒ <c>page</c>）。
    /// </summary>
    [ToolParam("Scroll target: page | element | to_ref | to_top | to_bottom. Derived from the locator/delta when omitted. Element targets require a ref locator.")]
    public string? ScrollTarget { get; init; }

    /// <summary>坐标 X（CSS viewport 像素，§8.2；action=pointer_click/pointer_move/pointer_drag）。</summary>
    [ToolParam("X coordinate in CSS viewport pixels for pointer actions.")]
    public double? X { get; init; }

    /// <summary>坐标 Y（CSS viewport 像素，§8.2）。</summary>
    [ToolParam("Y coordinate in CSS viewport pixels for pointer actions.")]
    public double? Y { get; init; }

    /// <summary>截图来源坐标必须携带的 observationId；滚动/缩放/布局或页面世代变化后失效（§8.2）。</summary>
    [ToolParam("Screenshot observation id when the coordinates came from a screenshot.")]
    public string? ObservationId { get; init; }
    /// <summary>坐标拖拽的终点 X（CSS viewport 像素，§8.2；仅 action=pointer_drag）。</summary>
    [ToolParam("Target X for action=pointer_drag (CSS viewport pixels).")]
    public double? ToX { get; init; }

    /// <summary>坐标拖拽的终点 Y（CSS viewport 像素）。</summary>
    [ToolParam("Target Y for action=pointer_drag (CSS viewport pixels).")]
    public double? ToY { get; init; }
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

    protected override async Task<ToolExecutionResult> ExecuteBrowserCoreAsync(

        BrowserInteractArgs args, ToolExecutionContext context, CancellationToken ct)

    {

        var action = args.Action?.Trim().ToLowerInvariant();

        if (action is not ("click" or "fill" or "type" or "press" or "hover" or "scroll" or "select" or "check" or "pointer_click" or "pointer_move" or "pointer_drag"))

            return BrowserToolResponse.Failure("browser_invalid_arguments", "unsupported interaction action");

        // 输入路径必须显式且被尊重（§7）。未登记的线名 fail closed。
        var inputMode = DesktopInputMode.Dom;
        if (!string.IsNullOrWhiteSpace(args.InputMode))
        {
            if (!DesktopInputModeWire.TryParse(args.InputMode.Trim(), out inputMode))
            {
                return BrowserToolResponse.Failure(
                    "browser_invalid_arguments", "input_mode must be one of dom or native");
            }

            if (inputMode == DesktopInputMode.Native && !DesktopInteractionActionWire.SupportsNativeInput(MapAction(action, args.Checked)))
            {
                // **绝不静默降级成 dom**：那会让调用方以为走了真实输入路径。
                // 原生输入目前覆盖键鼠能表达的动作；其余动作在这里明确 unsupported。
                const string hint =
                    "native input covers click/fill/type/press/check/hover/scroll/drag/pointer_click/pointer_move; use input_mode=dom for this action";
                return BrowserToolResponse.Failure(
                    "browser_input_mode_not_supported",
                    $"native input is not implemented for action '{action}'",
                    value: BrowserOperationFailure.Receipt(
                        new DesktopCapabilityError(
                            DesktopCapabilityErrorCode.UnsupportedCapability,
                            $"native input is not implemented for action '{action}'",
                            retryable: false),
                        hint,
                        MapAction(action, args.Checked),
                        args.ExpectValue),
                    warnings: [hint]);
            }
        }

        // 滚动目标：显式优先；未登记线名 fail closed（不猜测、不回退）。
        DesktopScrollTarget? scrollTarget = null;
        if (!string.IsNullOrWhiteSpace(args.ScrollTarget))
        {
            if (!DesktopScrollTargetWire.TryParse(args.ScrollTarget.Trim(), out var parsedScrollTarget))
            {
                return BrowserToolResponse.Failure(
                    "browser_invalid_arguments",
                    "scroll_target must be one of page, element, to_ref, to_top or to_bottom");
            }

            if (action != "scroll")
            {
                return BrowserToolResponse.Failure(
                    "browser_invalid_arguments", "scroll_target is only valid for the scroll action");
            }

            scrollTarget = parsedScrollTarget;
        }

        try

        {

            // 形状校验：错误码与文案沿用迁移前（保持 Agent 可见行为）。

            var locator = args.Locator is null ? null : BrowserLocatorInputMapper.ToDesktopLocator(args.Locator);

            if (action != "scroll" && locator is null)

                return BrowserToolResponse.Failure("browser_invalid_arguments", "locator is required for this action");

            // 拖拽必须**两个端点都给**：只有一个端点无法表达「拖到哪儿」（契约层也会拦，这里给更早的提示）。
            var toLocator = args.ToLocator is null ? null : BrowserLocatorInputMapper.ToDesktopLocator(args.ToLocator);

            if (action == "drag" && toLocator is null)

                return BrowserToolResponse.Failure(
                    "browser_invalid_arguments", "action=drag requires both locator (source) and to_locator (target)");

            if (action == "upload")
        {
            var files = args.Files?.Where(file => !string.IsNullOrWhiteSpace(file)).Select(file => file.Trim()).ToArray();
            if (files is not { Length: > 0 })
            {
                return BrowserToolResponse.Failure(
                    "browser_invalid_arguments", "action=upload requires files (absolute paths)");
            }

            var relative = files.FirstOrDefault(file => !Path.IsPathFullyQualified(file));
            if (relative is not null)
            {
                // 相对路径的含义取决于浏览器进程的工作目录——那是别人的目录，猜一个基准是不诚实的。
                return BrowserToolResponse.Failure(
                    "browser_invalid_arguments", $"upload requires absolute file paths; '{relative}' is relative");
            }
        }

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

                    args.DeltaY,

                    // 输入路径随请求下传：驱动将来据它决定走 DOM 还是 CDP；
                    // 实际用了哪条路径由回执的 inputBackend 如实报告。
                    inputMode,

                    // 滚动目标：显式优先，否则由契约按 locator/位移推导。
                    scrollTarget,

                    // 拖拽终点（仅 action=drag；契约层强制两个端点都给）。
                    toLocator,

                    // 上传文件（仅 action=upload；契约层强制至少一个）。
                    args.Files, x: args.X, y: args.Y, observationId: args.ObservationId, toX: args.ToX, toY: args.ToY),

                call,

                ct);

            if (interaction.IsFailure)
            {
                // 失败也必须给出回执：调用方需要知道「执行到哪一步、是否可能已产生副作用、能否重试」。
                // 只给 code/message 会把"可能已经执行"误读成"没执行"（设计 §4.1）。
                return Failure(interaction.Error!, action, args, contextId);
            }



            var page = interaction.Value.Page;

            // 证据化回执：把「调用方声明的期望」与「驱动回读的观测值」交给共享判定器，
            // 于是两条传输天然同形（判定只在这里发生一次，映射层不各自再验）。
            var element = interaction.Value.Element;
            var receipt = BrowserInteractionReceipt.Build(new BrowserInteractionEvidence(
                new OperationId(Guid.NewGuid().ToString("N")),
                MapAction(action, args.Checked),
                RequestedText: args.Text,
                RequestedValues: args.Values,
                RequestedChecked: args.Checked,
                ExpectedValue: args.ExpectValue,
                ElementObserved: element is not null,
                ObservedValue: element?.Value,
                ObservedSelectedValues: element?.SelectedValues,
                ObservedChecked: element?.IsChecked,
                Execution: BrowserActionExecution.Completed,
                Target: new DesktopPageTarget(contextId, args.PageId.Trim())));
            var retry = BrowserRetryPolicy.Decide(receipt);

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

                    Element = element is { } observed ? ToValue(observed) : null,

                    Ok = receipt.Ok,
                    Completion = BrowserActionCompletionWire.NameOf(receipt.Completion),
                    Execution = BrowserActionExecutionWire.NameOf(receipt.Execution),
                    Verification = BrowserActionVerificationWire.NameOf(receipt.Verification),
                    Assertions = receipt.Assertions
                        .Select(assertion => new BrowserAssertionToolValue
                        {
                            Kind = BrowserAssertionKindWire.NameOf(assertion.Kind),
                            Matched = assertion.Matched,
                            Length = assertion.RawLength,
                            Actual = assertion.Actual,
                        })
                        .ToArray(),
                    MayHaveSideEffects = receipt.MayHaveSideEffects,
                    SafeToRetry = receipt.SafeToRetry,
                    RequiresFollowUp = receipt.RequiresFollowUp,
                    RetryDisposition = BrowserRetryDispositionWire.NameOf(retry.Disposition),
                    RetryDetail = retry.Detail,
                    // 实际 backend：本切片只有 DOM 路径（请求 native 已在入口明确失败）。
                    // 实际 backend：**没有静默回落**（native 不支持的动作在上游/驱动明确失败），
                    // 因此请求成功就意味着用的就是这条路径。native 路径不做动作后回读（避免重查旧定位符），
                    // 所以它的回执通常是 dispatched —— 要确认写入请用 dom 或后续 snapshot/locate。
                    InputBackend = DesktopInputModeWire.NameOf(inputMode),

                    // 滚动结果事实（§7）：容器/位移/边界/内容变化。**内容没变不是失败**，
                    // 因此这里只如实报告，不据此改判 completion。
                    Scroll = interaction.Value.Scroll is { } scroll ? BrowserScrollToolValue.From(scroll) : null,

                },

                new BrowserContextId(contextId),

                new PageId(args.PageId.Trim()),

                page.Version.IsKnown ? page.Version.Value : null);

        }

        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }

        catch (BrowserOperationException ex)
        {
            // 异常路径也要带回执：驱动抛出的失败同样是"一次交互的结果"，
            // 只给 code/message 会让调用方看不出到底派发没有（设计 §4.1）。
            var (error, hint) = BrowserOperationFailure.Map(ex.Code, ex.Message);
            return BrowserToolResponse.Failure(
                ex.Code,
                ex.Message,
                value: BrowserOperationFailure.Receipt(
                    error, hint, MapAction(action, args.Checked), args.ExpectValue),
                warnings: hint is null ? null : [hint]);
        }

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

        "drag" => DesktopInteractionAction.Drag,

        "upload" => DesktopInteractionAction.Upload,

        "select" => DesktopInteractionAction.Select,
                // §8.2 坐标动作（CSS viewport 像素）：必须显式映射，**绝不能落到 _ 兜底**（那会变成 check/uncheck）。
                "pointer_click" => DesktopInteractionAction.PointerClick,
                "pointer_move" => DesktopInteractionAction.PointerMove,
                "pointer_drag" => DesktopInteractionAction.PointerDrag,

        _ => isChecked == false ? DesktopInteractionAction.Uncheck : DesktopInteractionAction.Check,

    };



    /// <summary>
    /// 受影响元素的投影：与定位共用同一套展示规则（text 与 name 相同则省略、bbox 最多一位小数）。
    /// <c>checked</c> 在这里保留 —— 交互后"到底勾上没有"正是这一条的直接证据，
    /// 而表单值仍不回显（回执走脱敏断言）。
    /// </summary>
    private static BrowserElementToolValue ToValue(DesktopElementRef element) =>
        BrowserToolResponse.Element(element, includeValue: false, includeChecked: true);



    /// <summary>
    /// 交互失败：把错误判成**回执**（执行阶段由错误目录推导）并随失败一起返回。
    /// 关键事实是「是否已经派发」——超时/断连可能已经产生副作用，绝不能只给一个 code 就完事。
    /// </summary>
    private static ToolExecutionResult Failure(
        DesktopCapabilityError error,
        string? action = null,
        BrowserInteractArgs? args = null,
        string? contextId = null)
    {
        var receipt = BrowserInteractionReceipt.Build(new BrowserInteractionEvidence(
            new OperationId(Guid.NewGuid().ToString("N")),
            MapAction(action ?? "click", args?.Checked),
            ExpectedValue: args?.ExpectValue,
            Execution: BrowserInteractionReceipt.ExecutionFor(error),
            Target: contextId is null ? null : new DesktopPageTarget(contextId, args!.PageId.Trim()),
            Error: error));
        var retry = BrowserRetryPolicy.Decide(receipt);

        return BrowserCapabilityFailure.From(
            error,
            "browser_interact_failed",
            "stale_element_reference",
            value: new BrowserReceiptToolValue
            {
                Operation = receipt.Operation.ToString(),
                Completion = BrowserActionCompletionWire.NameOf(receipt.Completion),
                Execution = BrowserActionExecutionWire.NameOf(receipt.Execution),
                Verification = BrowserActionVerificationWire.NameOf(receipt.Verification),
                MayHaveSideEffects = receipt.MayHaveSideEffects,
                SafeToRetry = receipt.SafeToRetry,
                RequiresFollowUp = receipt.RequiresFollowUp,
                RetryDisposition = BrowserRetryDispositionWire.NameOf(retry.Disposition),
                RetryDetail = retry.Detail,
            });
    }

}

