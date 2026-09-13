// ADR-089 U0-S1：统一检索查询合同与覆盖合同（纯新增，未接入任何既有工具）。
// 契约来源：Docs/Features/Agent统一检索与渐进展开工具链设计-2026-09-13.md
// 切片任务书：temp/adr089-u0-s1-taskbook-20260913.md（U0-S1）。
using System.Collections.Generic;

namespace PuddingCore.Tools.Retrieval;

/// <summary>匹配模式：字面量或正则。</summary>
public enum RetrievalMatchMode
{
    Literal,
    Regex,
}

/// <summary>大小写模式：敏感或不敏感。</summary>
public enum RetrievalCaseMode
{
    Sensitive,
    Insensitive,
}

/// <summary>结果新鲜度要求：允许命中索引数据，或要求读取当前数据。</summary>
public enum RetrievalFreshness
{
    Indexed,
    Current,
}

/// <summary>
/// 覆盖状态。语义硬约束：只有 <see cref="Complete"/> 允许表达"覆盖完成"；
/// 其余状态一律表示声明范围未覆盖完毕，必须通过 <see cref="RetrievalCoverage.Reasons"/> 说明原因。
/// </summary>
public enum RetrievalCoverageStatus
{
    /// <summary>声明范围已全部完成。</summary>
    Complete,

    /// <summary>只覆盖了部分范围。</summary>
    Partial,

    /// <summary>预算/上限截断（结果集固有上限）。</summary>
    Truncated,

    /// <summary>时间预算耗尽。</summary>
    Timeout,

    /// <summary>后端/磁盘/权限不可用。</summary>
    Unavailable,

    /// <summary>参数或查询合同非法。</summary>
    ContractError,
}

/// <summary>统一查询合同。scope 单位为绝对路径；glob 为独立文件过滤器。</summary>
public sealed record RetrievalQuery(
    string Query,
    string Scope,
    RetrievalMatchMode Match = RetrievalMatchMode.Literal,
    RetrievalCaseMode Case = RetrievalCaseMode.Insensitive,
    string? Glob = null,
    RetrievalFreshness Freshness = RetrievalFreshness.Indexed,
    int Limit = 20,
    string? Cursor = null);

/// <summary>
/// 覆盖报告：必须能表达"为什么不是 Complete"。
/// 硬性语义：<see cref="IsComplete"/> 为 true 当且仅当 <see cref="Status"/> 为
/// <see cref="RetrievalCoverageStatus.Complete"/>。isComplete 构造参数仅为满足既定签名保留，
/// 实际值由 Status 唯一推导，防止调用方构造出互相矛盾的状态。
/// </summary>
public sealed record RetrievalCoverage
{
    /// <summary>创建覆盖报告（isComplete 参数值会被推导值覆盖，保留以兼容既定签名）。</summary>
    public RetrievalCoverage(RetrievalCoverageStatus status, IReadOnlyList<string>? reasons, bool isComplete)
    {
        Status = status;
        Reasons = reasons ?? [];
        _ = isComplete; // 语义由 Status 推导，见 IsComplete。
    }

    /// <summary>覆盖状态。</summary>
    public RetrievalCoverageStatus Status { get; }

    /// <summary>状态原因（Complete 可为空列表；其余状态至少一条）。</summary>
    public IReadOnlyList<string> Reasons { get; }

    /// <summary>当且仅当 Status 为 Complete 时为 true。</summary>
    public bool IsComplete => Status == RetrievalCoverageStatus.Complete;

    /// <summary>构造 Complete 覆盖报告。</summary>
    public static RetrievalCoverage Complete(IReadOnlyList<string>? reasons = null) =>
        new(RetrievalCoverageStatus.Complete, reasons ?? [], true);

    /// <summary>构造 Partial 覆盖报告（必须给出原因）。</summary>
    public static RetrievalCoverage Partial(string reason, IReadOnlyList<string>? extra = null) =>
        new(RetrievalCoverageStatus.Partial, [reason, .. extra ?? []], false);

    /// <summary>按状态构造单原因覆盖报告。</summary>
    public static RetrievalCoverage Of(RetrievalCoverageStatus status, string reason) =>
        new(status, [reason], status == RetrievalCoverageStatus.Complete);
}
