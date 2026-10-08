using System.Text.Json;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

public sealed record BrowserToolError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public sealed record BrowserToolResult<T>
{
    public required bool Ok { get; init; }
    public string? ContextId { get; init; }
    public string? PageId { get; init; }
    public long? PageVersion { get; init; }
    public T? Value { get; init; }
    public BrowserToolError? Error { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record BrowserContextToolValue
{
    public required string ContextId { get; init; }
    public bool Persistent { get; init; }
    public int PageCount { get; init; }
}

public sealed record BrowserTabToolValue
{
    public required string ContextId { get; init; }
    public required string PageId { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public long PageVersion { get; init; }
}

public sealed record BrowserNavigationToolValue
{
    public required string Action { get; init; }
    public required BrowserTabToolValue Page { get; init; }
    public bool? NavigationOk { get; init; }
    public int? StatusCode { get; init; }
    public string? ErrorText { get; init; }
}

/// <summary>
/// 快照输出。
/// outline/interactive 模式下 <see cref="AccessibilityTree"/> 是**按模式过滤并收敛后**的投影，
/// <see cref="DomText"/>/<see cref="Html"/> 为 <c>null</c>（不请求，省下体积大头）；
/// full 模式下三者按请求原样返回。
/// </summary>
public sealed record BrowserSnapshotToolValue
{
    /// <summary>实际使用的阅读模式线名：outline / interactive / full。</summary>
    public string? Mode { get; init; }

    public string? DomText { get; init; }
    public string? AccessibilityTree { get; init; }
    public string? Html { get; init; }
    public bool Truncated { get; init; }

    /// <summary>
    /// 截断原因线名：<c>incomplete_scan</c>（扫描侧被截断，可能缺整片区域）
    /// 或 <c>output_budget</c>（扫描完整，只是输出收敛，可用 <see cref="NextCursor"/> 续读）。
    /// </summary>
    public string? TruncationReason { get; init; }

    /// <summary>继续读取的游标；仅在 <c>output_budget</c> 截断时给出。</summary>
    public int? NextCursor { get; init; }

    /// <summary>本次返回的行数（outline/interactive）。</summary>
    public int? ReturnedLines { get; init; }

    /// <summary>本次返回文本的 UTF-8 字节数（outline/interactive）——省 token 的实测口径。</summary>
    public int? Bytes { get; init; }

    /// <summary>驱动扫到的节点数（页面侧事实，不是返回行数）。</summary>
    public int NodeCount { get; init; }

    /// <summary>
    /// 本次读取的修订标识（§145）：把它原样回传给下次的 <c>since_revision</c> 即可问"内容变了吗"。
    /// 对调用方不透明。
    /// </summary>
    public string? Revision { get; init; }

    /// <summary>
    /// 与 <c>since_revision</c> 相比内容是否变化；**没给 <c>since_revision</c> 时为 <c>null</c>**
    /// （不比较就不能声称"没变"）。不可比时整个调用以 `refresh_required` 失败，而不是在这里猜一个值。
    /// </summary>
    public bool? Changed { get; init; }

    /// <summary>行级差异（要求比较且基线可用时给出）；计数真实，列表可能被预算截断。</summary>
    public BrowserDiffToolValue? Diff { get; init; }
}

/// <summary>
/// 行级差异的工具投影（§145）：**变化了什么**，而不是"变了/没变"。
/// </summary>
public sealed record BrowserDiffToolValue
{
    public IReadOnlyList<string> Added { get; init; } = [];
    public IReadOnlyList<string> Removed { get; init; } = [];

    /// <summary>真实总数（可能大于列表长度——超预算时列表被截断且 <see cref="Truncated"/> 为真）。</summary>
    public int AddedCount { get; init; }
    public int RemovedCount { get; init; }

    /// <summary>差异本身是否被预算截断（不静默砍掉）。</summary>
    public bool Truncated { get; init; }

    public static BrowserDiffToolValue? From(BrowserTextDiff? diff) => diff is null
        ? null
        : new BrowserDiffToolValue
        {
            Added = diff.Added,
            Removed = diff.Removed,
            AddedCount = diff.AddedCount,
            RemovedCount = diff.RemovedCount,
            Truncated = diff.Truncated,
        };
}

public sealed record BrowserElementToolValue
{
    public required string Ref { get; init; }
    public required string Tag { get; init; }
    public string? Role { get; init; }
    public string? Name { get; init; }
    public string? Text { get; init; }
    public bool Visible { get; init; }

    /// <summary>
    /// 是否与视口相交。与 <see cref="Visible"/> **分开**（设计方案 §6.2）：
    /// 屏外但有尺寸的元素 <c>visible=true</c> 而 <c>inViewport=false</c>；
    /// 写操作要求前者为真（驱动会先滚入可见区域并复检）。
    /// </summary>
    public bool? InViewport { get; init; }

    public bool Enabled { get; init; }
    public bool? Checked { get; init; }

    /// <summary>
    /// 表单当前值（显式请求才返回，见设计方案 §6.2）。
    /// 它是「输入是否写入成功」的唯一可靠回执：<c>text</c> 不含表单值。
    /// 敏感输入按策略只给长度/是否匹配，不回显原文。
    /// </summary>
    public string? Value { get; init; }

    /// <summary><c>select</c> 当前选中的值（显式请求才返回）。</summary>
    public IReadOnlyList<string>? SelectedValues { get; init; }

    public BoundingBox? BoundingBox { get; init; }
}

public sealed record BrowserLocateToolValue
{
    public required int Count { get; init; }
    public required IReadOnlyList<BrowserElementToolValue> Elements { get; init; }

    /// <summary>
    /// 下一页游标（原样回传给 <c>browser_locate.cursor</c>）；<c>null</c> 表示**已取完**。
    /// 对模型不透明：只回传，不解释。
    /// </summary>
    public string? NextCursor { get; init; }
}

public sealed record BrowserInteractionToolValue
{
    public required string Action { get; init; }
    public required BrowserTabToolValue Page { get; init; }
    public BrowserElementToolValue? Element { get; init; }

    /// <summary>
    /// 回执是否可当作成功：只对 <c>verified</c> 或明确标注的 <c>dispatched</c> 为真；
    /// unknown/blocked/failed 为假，但**不丢失已派发的事实**（见 <see cref="Completion"/> 与
    /// <see cref="MayHaveSideEffects"/>）。
    /// </summary>
    public bool Ok { get; init; }

    /// <summary>完成度线名：verified / dispatched / blocked / failed / unknown。</summary>
    public required string Completion { get; init; }

    /// <summary>执行阶段线名：not_started / dispatched / completed / unknown。</summary>
    public string? Execution { get; init; }

    /// <summary>验证结论线名：passed / failed / unverified / not_requested。</summary>
    public string? Verification { get; init; }

    /// <summary>后置断言证据。表单值只给 matched 与长度，绝不回显内容。</summary>
    public IReadOnlyList<BrowserAssertionToolValue> Assertions { get; init; } = [];

    public bool MayHaveSideEffects { get; init; }

    public bool SafeToRetry { get; init; }

    /// <summary>是否应先观察（已派发未验证 / 结果不明）。</summary>
    public bool RequiresFollowUp { get; init; }

    /// <summary>重试裁定线名：safe_to_retry / dispatched_observe_only / unknown_outcome / not_retryable。</summary>
    public string? RetryDisposition { get; init; }

    /// <summary>裁定理由（例如"已派发但效果未验证，先观察"）。</summary>
    public string? RetryDetail { get; init; }

    /// <summary>
    /// **实际使用的**输入路径（设计方案 §7：回执报告实际 backend，而不是请求值）。
    /// 当前实现只有 <c>dom</c>；请求 <c>native</c> 会明确失败，因此这里不会出现"请求 native、报告 dom"的成功。
    /// </summary>
    public string? InputBackend { get; init; }

    /// <summary>滚动结果事实（§7，仅 <c>scroll</c> 动作且驱动报告了时存在）。</summary>
    public BrowserScrollToolValue? Scroll { get; init; }
}

/// <summary>
/// 失败时的回执投影（放进失败的 <c>value</c>）。与成功路径同名字段，因此调用方读法一致：
/// 失败同样能看出「执行到哪一步、是否可能已产生副作用、能不能重试」。
/// </summary>
public sealed record BrowserReceiptToolValue
{
    public required string Operation { get; init; }
    public required string Completion { get; init; }
    public string? Execution { get; init; }
    public string? Verification { get; init; }
    public bool MayHaveSideEffects { get; init; }
    public bool SafeToRetry { get; init; }
    public bool RequiresFollowUp { get; init; }
    public string? RetryDisposition { get; init; }
    public string? RetryDetail { get; init; }

    /// <summary>实际使用的输入路径（目前只有 <c>dom</c>）。</summary>
    public string? InputBackend { get; init; }

    /// <summary>滚动结果事实（§7，仅 <c>scroll</c> 动作且驱动报告了时存在）。</summary>
    public BrowserScrollToolValue? Scroll { get; init; }
    public IReadOnlyList<BrowserAssertionToolValue> Assertions { get; init; } = [];
}

/// <summary>
/// 滚动结果事实（设计 §7）：目标容器、前后位置、消耗位移、边界、内容变化。
/// <b><see cref="ContentChanged"/> 为 false 不是失败</b>——「无限滚动另等新增内容/条目数」。
/// </summary>
public sealed record BrowserScrollToolValue
{
    public string? Container { get; init; }
    public double BeforeX { get; init; }
    public double BeforeY { get; init; }
    public double AfterX { get; init; }
    public double AfterY { get; init; }
    public double ConsumedDeltaX { get; init; }
    public double ConsumedDeltaY { get; init; }

    /// <summary>是否已到边界（请求了位移但滚不动）。到边界是**事实**，不是失败。</summary>
    public bool AtBoundary { get; init; }

    /// <summary>滚动前后是否观察到内容变化（新增内容/条目）。</summary>
    public bool ContentChanged { get; init; }

    /// <summary>请求了位移但一个像素都没动（"滚不动"）：可据此决定要不要换 native wheel。</summary>
    public bool NotScrolled => ConsumedDeltaX == 0 && ConsumedDeltaY == 0;

    public static BrowserScrollToolValue From(DesktopScrollReport report) => new()
    {
        Container = report.Container,
        BeforeX = report.BeforeX,
        BeforeY = report.BeforeY,
        AfterX = report.AfterX,
        AfterY = report.AfterY,
        ConsumedDeltaX = report.ConsumedDeltaX,
        ConsumedDeltaY = report.ConsumedDeltaY,
        AtBoundary = report.AtBoundary,
        ContentChanged = report.ContentChanged,
    };
}

/// <summary>
/// 一条后置断言证据的工具投影。
/// <see cref="Actual"/> 对表单值是 <c>null</c>（<see cref="Length"/> 仍给出长度），
/// 因此「fill 到底写进去了没有」可被证明而内容不外泄。
/// </summary>
public sealed record BrowserAssertionToolValue
{
    public required string Kind { get; init; }
    public required bool Matched { get; init; }

    /// <summary>观测值的原始长度；未观测到为 <c>null</c>。</summary>
    public int? Length { get; init; }

    /// <summary>非敏感断言的实际值（已截断）；敏感断言恒为 <c>null</c>。</summary>
    public string? Actual { get; init; }
}

public sealed record BrowserWaitToolValue
{
    public bool TimedOut { get; init; }
    public string? Error { get; init; }
    public required BrowserTabToolValue Page { get; init; }
}

internal static class BrowserToolResponse
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static ToolExecutionResult Success(
        object? value,
        BrowserContextId? contextId = null,
        PageId? pageId = null,
        long? pageVersion = null,
        IReadOnlyList<string>? warnings = null)
        => ToolExecutionResult.Ok(JsonSerializer.Serialize(new BrowserToolResult<object>
        {
            Ok = true,
            ContextId = contextId?.Value,
            PageId = pageId?.Value,
            PageVersion = pageVersion,
            Value = value,
            Warnings = warnings ?? []
        }, s_jsonOptions));

    public static ToolExecutionResult Failure(
        string code,
        string message,
        BrowserContextId? contextId = null,
        PageId? pageId = null,
        object? value = null,
        IReadOnlyList<string>? warnings = null)
        => ToolExecutionResult.Fail(JsonSerializer.Serialize(new BrowserToolResult<object>
        {
            Ok = false,
            ContextId = contextId?.Value,
            PageId = pageId?.Value,
            // 失败也要能带结构化证据：交互失败时"是否已经派发"必须随错误一起给出，
            // 否则调用方只能看到 code/message，把"可能已经执行"当成"没执行"（设计 §4.1）。
            Value = value,
            Warnings = warnings ?? [],
            Error = new BrowserToolError { Code = code, Message = message }
        }, s_jsonOptions));

    public static ToolExecutionResult FromException(
        BrowserOperationException exception,
        BrowserContextId? contextId = null,
        PageId? pageId = null)
        => Failure(exception.Code, exception.Message, contextId, pageId);

    public static BrowserContextToolValue Context(BrowserContextInfo info) => new()
    {
        ContextId = info.Id.Value,
        Persistent = info.Persistent,
        PageCount = info.PageCount
    };

    public static BrowserTabToolValue Page(PageInfo info) => new()
    {
        ContextId = info.ContextId.Value,
        PageId = info.Id.Value,
        Title = info.Title,
        Url = info.Url,
        PageVersion = info.PageVersion
    };

    public static BrowserElementToolValue Element(BrowserElementInfo info) => new()
    {
        Ref = info.Ref,
        Tag = info.Tag,
        Role = info.Role,
        Name = info.Name,
        Text = info.Text,
        Visible = info.Visible,
        InViewport = info.InViewport,
        Enabled = info.Enabled,
        Checked = info.Checked,
        Value = info.Value,
        SelectedValues = info.SelectedValues,
        BoundingBox = info.BoundingBox
    };

    /// <summary>
    /// 元素的能力层记录 → 工具输出（设计方案 §6.2「定位结果」）。
    ///
    /// 三条输出收敛规则（都在**展示边界**做，不影响驱动内部精度与准入判定）：
    /// ① <c>text</c> 与 <c>name</c> 相同时省略 —— 重复内容只是白付 token；
    /// ② <c>checked</c> 属于「可显式请求」的状态，默认不给；
    /// ③ bbox 保留**最多一位小数**（驱动内部仍是原精度；这里只用于展示，
    ///    真实点击走 ref，绝不使用四舍五入后的坐标）。
    ///
    /// 另有 <paramref name="fields"/> 白名单（§6.2 的 <c>fields</c>）：给了就**只回它要的**。
    /// 核心字段（ref/tag/visible/enabled）恒在——没有 <c>ref</c> 的定位结果无法继续操作。
    /// </summary>
    public static BrowserElementToolValue Element(
        DesktopElementRef element,
        bool includeValue = false,
        bool includeChecked = false,
        OutputFieldSet? fields = null)
    {
        var set = fields ?? BrowserElementFields.Default;

        // 显式白名单时它**就是权威**：列了 value 就等于要值（不必再传 include_value）。
        var wantValue = set.IsExplicit ? set.Includes("value") : includeValue;
        var wantSelected = set.IsExplicit ? set.Includes("selectedValues") : includeValue;
        var wantChecked = set.IsExplicit ? set.Includes("checked") : includeChecked;

        return new()
        {
            Ref = element.Reference,
            Tag = element.Tag,
            Role = set.Includes("role") ? element.Role : null,
            Name = set.Includes("name") ? element.Name : null,
            Text = set.Includes("text") && !SameAsName(element.Text, element.Name) ? element.Text : null,
            Visible = element.Visible,
            InViewport = set.Includes("inViewport") ? element.InViewport : null,
            Enabled = element.Enabled,
            Checked = wantChecked ? element.IsChecked : null,
            Value = wantValue ? element.Value : null,
            SelectedValues = wantSelected ? element.SelectedValues : null,
            BoundingBox = set.Includes("boundingBox") && element.BoundingBox is { } box
                ? new BoundingBox
                {
                    X = Round1(box.X),
                    Y = Round1(box.Y),
                    Width = Round1(box.Width),
                    Height = Round1(box.Height),
                }
                : null,
        };
    }

    private static bool SameAsName(string? text, string? name) =>
        text is not null && name is not null && string.Equals(text, name, StringComparison.Ordinal);

    /// <summary>展示用：最多一位小数（负零归一成 0，避免出现 "-0"）。</summary>
    private static double Round1(double value)
    {
        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }
}
