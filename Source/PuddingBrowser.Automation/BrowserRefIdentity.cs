namespace PuddingBrowser.Automation;

/// <summary>
/// 引用作用域：一个 ref 只在 <c>(context, page, frame)</c> 三元组内有效。
///
/// 为什么必须有它（设计方案 §5）：现有 ref 形如 <c>v{pageVersion}-n{seq}</c>，只携带文档版本，
/// 于是**跨页、跨 frame 使用同一版本号的引用会被静默接受** —— 设计明确点名「即使两页都恰好处于 v7」。
/// 身份必须绑定到具体页面与 frame，而不是靠版本号碰巧相等。
/// </summary>
public sealed record BrowserRefScope
{
    public const int MaxIdLength = 128;

    public BrowserRefScope(string contextId, string pageId, string? frameId = null)
    {
        ContextId = RequireId(contextId, nameof(contextId));
        PageId = RequireId(pageId, nameof(pageId));
        FrameId = frameId is null ? null : RequireId(frameId, nameof(frameId));
    }

    public string ContextId { get; }

    public string PageId { get; }

    /// <summary>frame 身份；<c>null</c> 表示顶层文档。</summary>
    public string? FrameId { get; }

    /// <summary>页面键（不含 frame）：作废整页引用时用它匹配。</summary>
    public string PageKey => string.Concat(ContextId, "/", PageId);

    /// <summary>同一页且同一 frame ⇒ 同一文档上下文。</summary>
    public bool IsSameDocumentAs(BrowserRefScope other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(PageKey, other.PageKey, StringComparison.Ordinal)
            && string.Equals(FrameId, other.FrameId, StringComparison.Ordinal);
    }

    public bool IsSamePageAs(BrowserRefScope other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(PageKey, other.PageKey, StringComparison.Ordinal);
    }

    public override string ToString() =>
        FrameId is null ? PageKey : $"{PageKey}[{FrameId}]";

    private static string RequireId(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdLength)
        {
            throw new ArgumentException(
                $"Value must be non-empty and at most {MaxIdLength} characters.", parameterName);
        }

        return value;
    }
}

/// <summary>
/// 三个**必须分开**的观察事实（设计方案 §5）。
///
/// 把它们糊成一个版本号是现有缺陷的根源：那样「只读调用」与「导航提交」无法区分，
/// 「普通 value 改变」会误伤全部引用、「滚动」会让节点身份失效。
/// </summary>
/// <param name="DocumentGeneration">已提交文档世代：**只有**新文档提交才推进。</param>
/// <param name="DomRevision">内容变更计数：DOM 变更推进，只读调用不推进。</param>
/// <param name="ViewportRevision">滚动/缩放/布局观察：只让**坐标**失效，不影响节点身份。</param>
public readonly record struct BrowserObservationRevisions(
    long DocumentGeneration,
    long DomRevision,
    long ViewportRevision)
{
    public static readonly BrowserObservationRevisions Initial = new(0, 0, 0);

    public override string ToString() => $"doc{DocumentGeneration}/dom{DomRevision}/vp{ViewportRevision}";
}

/// <summary>三个事实各自的推进规则（机器可验，取代「一个版本号走天下」）。</summary>
public static class BrowserRevisionPolicy
{
    /// <summary>
    /// 只读调用（snapshot/locate/wait 等）**不推进任何**事实。
    /// 设计 §5：只读调用不推进版本；G4 门禁要求「读操作导致 PageVersion 增长次数 = 0」。
    /// </summary>
    public static BrowserObservationRevisions AfterReadOnly(BrowserObservationRevisions current) => current;

    /// <summary>新文档提交：三个事实全部推进，并据此作废该页全部引用。</summary>
    public static BrowserObservationRevisions AfterDocumentCommitted(BrowserObservationRevisions current) =>
        new(current.DocumentGeneration + 1, current.DomRevision + 1, current.ViewportRevision + 1);

    /// <summary>普通 DOM 变更：只推进 domRevision，<b>不</b>作废节点引用。</summary>
    public static BrowserObservationRevisions AfterDomMutation(BrowserObservationRevisions current) =>
        current with { DomRevision = current.DomRevision + 1 };

    /// <summary>滚动/缩放/布局：只推进 viewportRevision，节点身份仍然有效。</summary>
    public static BrowserObservationRevisions AfterViewportChange(BrowserObservationRevisions current) =>
        current with { ViewportRevision = current.ViewportRevision + 1 };
}

/// <summary>失效类别：决定「作废多少引用」，而不是一律全清（设计方案 §5）。</summary>
public enum BrowserRefInvalidation
{
    /// <summary>新文档提交：作废该页**全部**引用（含所有 frame）。</summary>
    DocumentCommitted,

    /// <summary>frame 导航：只作废该 frame 的引用。</summary>
    FrameNavigated,

    /// <summary>页面关闭：作废该页全部引用。</summary>
    PageClosed,

    /// <summary>浏览器进程失败：作废该页全部引用。</summary>
    BrowserProcessFailed,

    /// <summary>普通 DOM 变更：<b>不</b>作废任何引用。</summary>
    DomChanged,

    /// <summary>滚动/缩放/布局：<b>不</b>作废节点身份（只让坐标观察失效）。</summary>
    ViewportChanged,

    /// <summary>单个节点被替换/移除：只作废该引用（由 <c>RevokeRef</c> 处理）。</summary>
    NodeDetached,
}

/// <summary>引用被拒的原因（线名由 <see cref="BrowserRefDenialWire"/> 给出）。</summary>
public enum BrowserRefDenial
{
    None,

    /// <summary>登记表里没有这个引用（进程重启、从未签发、已过期清理）。</summary>
    UnknownRef,

    /// <summary>引用属于另一个页面 —— 即使两个页面的文档世代恰好相同也必须拒绝。</summary>
    PageMismatch,

    /// <summary>引用属于另一个 frame。</summary>
    FrameMismatch,

    /// <summary>文档世代已变（导航提交过），引用整体作废。</summary>
    DocumentChanged,

    /// <summary>宿主说该节点已断开/被替换。</summary>
    NodeDetached,

    /// <summary>引用已被显式作废（关页/进程失败/frame 导航）。</summary>
    Revoked,

    /// <summary>用于坐标操作时，视口观察已过期（节点身份仍有效）。</summary>
    ViewportMismatch,
}

/// <summary>引用拒绝原因 ↔ 线名（fail closed：未登记值取红）。</summary>
public static class BrowserRefDenialWire
{
    public static string NameOf(BrowserRefDenial denial) => denial switch
    {
        BrowserRefDenial.None => "none",
        BrowserRefDenial.UnknownRef => "unknown_ref",
        BrowserRefDenial.PageMismatch => "page_mismatch",
        BrowserRefDenial.FrameMismatch => "frame_mismatch",
        BrowserRefDenial.DocumentChanged => "document_changed",
        BrowserRefDenial.NodeDetached => "node_detached",
        BrowserRefDenial.Revoked => "revoked",
        BrowserRefDenial.ViewportMismatch => "viewport_mismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(denial), denial, "Ref denial is not registered."),
    };

    public static bool TryParse(string? name, out BrowserRefDenial denial)
    {
        foreach (var candidate in Enum.GetValues<BrowserRefDenial>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                denial = candidate;
                return true;
            }
        }

        denial = default;
        return false;
    }

    public static IReadOnlyList<BrowserRefDenial> All { get; } = Enum.GetValues<BrowserRefDenial>();
}

/// <summary>
/// 一个已签发的引用：**宿主绑定**的节点身份 + 作用域 + 文档世代。
///
/// <see cref="HostNodeKey"/> 是驱动侧的真实节点身份（例如 CDP <c>backendNodeId</c>），
/// <b>不是</b>页面可自行写入的 <c>data-pudding-ref</c> 属性 —— 设计 §5 明确禁止只信任后者：
/// 页面可以随便写属性，但写不出宿主侧的身份。
///
/// <see cref="RoleHint"/>/<see cref="NameHint"/> 只用于给调用方**最小刷新提示**，
/// 绝不参与身份判定，也绝不用于自动重绑（设计方案 §1.1 修正）。
/// </summary>
public sealed record BrowserRefToken(
    string Value,
    BrowserRefScope Scope,
    long DocumentGeneration,
    long ViewportRevision,
    string HostNodeKey,
    string? RoleHint = null,
    string? NameHint = null)
{
    /// <summary>刷新提示：只描述「重新观察一次」，不指回任何同名节点。</summary>
    public string RefreshHint
    {
        get
        {
            var described = RoleHint is null
                ? "(no role hint)"
                : NameHint is null
                    ? $"role={RoleHint}"
                    : $"role={RoleHint} name={NameHint}";

            return $"re-observe the page and locate a fresh ref on {Scope} ({described});"
                + " refs are never re-bound to a look-alike node automatically";
        }
    }

    public override string ToString() => $"{Value}@{Scope} doc{DocumentGeneration}";
}

/// <summary>引用校验结论。</summary>
public sealed record BrowserRefDecision(
    bool IsValid,
    BrowserRefDenial Denial,
    BrowserRefToken? Token = null)
{
    /// <summary>节点身份仍有效，但视口（坐标）观察已过期：坐标操作必须重新观察。</summary>
    public bool ViewportStale { get; init; }

    /// <summary>最小刷新提示：只说「重新观察」，绝不指回同名节点（禁止自动重绑）。</summary>
    public string? RefreshHint => Token?.RefreshHint
        ?? (IsValid ? null : "re-observe the page and locate a fresh ref; the previous ref cannot be re-bound automatically");

    public string DenialWire => BrowserRefDenialWire.NameOf(Denial);

    public static BrowserRefDecision Valid(BrowserRefToken token, bool viewportStale = false) =>
        new(true, BrowserRefDenial.None, token) { ViewportStale = viewportStale };

    public static BrowserRefDecision Denied(BrowserRefDenial denial, BrowserRefToken? token = null) =>
        new(false, denial, token);

    /// <summary>结论 + 刷新提示的一行摘要（回执/日志用，不含页面内容）。</summary>
    public override string ToString() =>
        IsValid
            ? $"valid {Token?.Value}{(ViewportStale ? " (viewport stale)" : string.Empty)}"
            : $"denied {DenialWire}{(Token is null ? string.Empty : $" (hint: {Token.RefreshHint})")}";
}
