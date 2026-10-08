using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.DesktopService;

/// <summary>
/// 读取修订与行级差异的**唯一判定点**（设计方案 §145）。
///
/// 为什么必须只有一份：基线存在**真正读到内容的进程**里，而"这条修订能不能比、比出来是什么"
/// 是同一件事的三个部分。两条传输各写一份必然漂移——本系列已经在滚动目标与定位游标上
/// 各栽过一次（同一个参数在两条传输上语义不同）。
///
/// 判定顺序（每一步都给得出明确结论，不含猜测）：
/// ① 没给 <c>since_revision</c> ⇒ 只记住本次基线并报告修订；
/// ② 格式坏 ⇒ <c>invalid_request</c>（**参数错**，不是"内容变了"）；
/// ③ 版本/查询指纹不可比 ⇒ <c>refresh_required</c>（谈不了变化）；
/// ④ 基线已被淘汰/过大未存 ⇒ 记住本次 + <c>refresh_required</c>（**基线缺失不伪造变化**）；
/// ⑤ 基线命中 ⇒ 行级差异；<c>changed</c> 由差异是否为空**推导**，不另外猜。
/// </summary>
public static class BrowserReadingDiffResolver
{
    /// <summary>一次判定的结果：成功时给修订（+可选差异），失败时给领域错误。</summary>
    public readonly record struct Outcome(
        string? Revision,
        bool? Changed,
        BrowserTextDiff? Diff,
        DesktopCapabilityError? Failure)
    {
        public bool IsFailure => Failure is not null;

        public static Outcome Failed(DesktopCapabilityError error) => new(null, null, null, error);
    }

    public static Outcome Resolve(
        BrowserReadingBaselineStore store,
        DesktopPageTarget target,
        DesktopSnapshotOptions options,
        DesktopPageVersion pageVersion,
        string? content)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        // 没有活版本 ⇒ 修订失去依据（不能"大概按 v0 比一比"）。
        if (!pageVersion.IsKnown)
        {
            return options.SinceRevision is null
                ? new Outcome(null, null, null, null)
                : Outcome.Failed(new DesktopCapabilityError(
                    DesktopCapabilityErrorCode.RefreshRequired,
                    "the page has no live version, so the previous revision cannot be compared; "
                    + "re-read to establish a baseline",
                    retryable: false));
        }

        var fingerprint = QueryFingerprintOf(options);
        var revision = BrowserReadingRevision.Create(pageVersion.Value, fingerprint, content);

        if (options.SinceRevision is not { } wire)
        {
            store.Remember(target, revision.Encode(), BrowserTextDiff.SplitLines(content));
            return new Outcome(revision.Encode(), null, null, null);
        }

        if (!BrowserReadingRevision.TryParse(wire, out var baseline))
        {
            return Outcome.Failed(new DesktopCapabilityError(
                DesktopCapabilityErrorCode.InvalidRequest,
                "since_revision is malformed; omit it to read without comparing, or pass the revision string unchanged"));
        }

        if (!baseline.IsComparableTo(pageVersion.Value, fingerprint))
        {
            return Outcome.Failed(new DesktopCapabilityError(
                DesktopCapabilityErrorCode.RefreshRequired,
                $"the given revision belongs to v{baseline.PageVersion} or a different read shape; "
                + "re-read to establish a comparable baseline (no change is reported, none is invented)",
                retryable: false));
        }

        if (!store.TryGet(target, wire, out var lines))
        {
            // 基线缺失/被淘汰：**记住本次**（让调用方的下一次比较有基线），但本次必须如实说"重新读取"。
            store.Remember(target, revision.Encode(), BrowserTextDiff.SplitLines(content));
            return Outcome.Failed(new DesktopCapabilityError(
                DesktopCapabilityErrorCode.RefreshRequired,
                "the baseline for that revision is no longer available; re-read to establish a new one",
                retryable: false));
        }

        var diff = BrowserTextDiff.Compute(lines, BrowserTextDiff.SplitLines(content));
        store.Remember(target, revision.Encode(), BrowserTextDiff.SplitLines(content));

        return new Outcome(revision.Encode(), !diff.IsEmpty, diff, null);
    }

    /// <summary>
    /// 查询指纹：把**影响读取结果**的输入规范化后取短哈希（含作用域与预算——预算变了，
    /// 截断位置就变了，"内容差异"会变成预算差异）。
    /// </summary>
    public static string QueryFingerprintOf(DesktopSnapshotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return BrowserReadingRevision.QueryFingerprintOf(
            $"snapshot|dom={options.IncludeDom}|a11y={options.IncludeAccessibilityTree}|html={options.IncludeHtml}"
            + $"|hidden={options.IncludeHidden}|iframe={options.IncludeIframes}|shadow={options.IncludeShadowDom}"
            + $"|nodes={options.MaxNodes}|text={options.MaxTextLength}|depth={options.MaxDepth}",
            options.ScopeRef,
            null);
    }
}
