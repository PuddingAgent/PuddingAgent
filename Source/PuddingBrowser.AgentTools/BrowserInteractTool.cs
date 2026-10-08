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

