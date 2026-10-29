using PuddingCodeIndex.Contracts.Retrieval;
using PuddingCodeIntelligence.Contracts;

namespace PuddingRuntime.Services.Tools;

/// <summary>workspace_open 支持的视图（v1 只有两个）。</summary>
public enum WorkspaceOpenView
{
    /// <summary>文件切片：给路径 + 行范围，拿回那几行。</summary>
    Excerpt = 0,

    /// <summary>结构预览：给路径，拿回顶层类型树。</summary>
    Outline = 1,

    /// <summary>目录地图：给目录路径，拿回目录树与每个文件的核心类型。</summary>
    Map = 2,
}

/// <summary>
/// <c>workspace_open</c> 的请求。
/// <para>
/// 规格 §8.3：请求 <c>ref</c> 或 <c>{project_id,path}</c> <b>二选一</b>。
/// </para>
/// </summary>
public sealed record WorkspaceOpenRequest(
    string WorkspaceId,
    string? ProjectId,
    string? Path,
    WorkspaceOpenView View,
    int? StartLine = null,
    int? LineCount = null,
    int? Depth = null,
    int MaxBytes = 64 * 1024,

    /// <summary>
    /// 符号引用 token（<c>pmsym1.…</c>）。与 <see cref="ProjectId"/>/<see cref="Path"/> 二选一，
    /// 两者同给属参数错误（见 <see cref="WorkspaceOpenRequestRules"/>），不做隐式择优。
    /// </summary>
    string? Ref = null);

/// <summary>
/// 请求形状校验（§8.3 的「二选一」与 ref 合法性）。
/// <para>
/// <b>为何不择一执行</b>：同时给 ref 和 path 时静默选一个，会让调用方以为另一种写法生效 ——
/// 例如他改的是 ref，而实际按旧 path 打开了另一个文件，结果看起来完全正常。
/// 同类故障已经在本仓真实发生过：<c>line_count</c> 误写成 <c>lines</c> 被参数绑定静默丢弃，
/// 回落默认值 80，调用方拿着错的答案以为是真答案。
/// </para>
/// </summary>
public static class WorkspaceOpenRequestRules
{
    /// <summary>返回 <c>null</c> 表示形状合法；否则返回可读原因（工具层直接回显）。</summary>
    public static string? Validate(WorkspaceOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var hasRef = !string.IsNullOrWhiteSpace(request.Ref);
        var hasPath = !string.IsNullOrWhiteSpace(request.Path);
        var hasProject = !string.IsNullOrWhiteSpace(request.ProjectId);

        if (hasRef && (hasPath || hasProject))
        {
            return "ref 与 {project_id,path} 二选一，不能同时给：" +
                   "静默择优会让调用方以为另一种写法生效。";
        }

        if (!hasRef && !hasPath)
        {
            return "必须给 ref 或 path 之一。";
        }

        if (hasRef && !SymbolRefV1.TryParse(request.Ref, out _, out var refError))
        {
            return "ref 不是合法的符号引用：" + refError;
        }

        return null;
    }
}

/// <summary>
/// 结构预览的节点 —— <b>open 契约自己的词汇</b>。
/// <para>
/// 为什么不直接用上游类型：outline 服务有**两个**不同的节点类型
/// （C# 路径的 <c>PuddingRuntime.Services.Tools.OutlineNode</c> 与注册表路径的
/// <c>PuddingCodeIntelligence.Contracts.OutlineNode</c>），
/// 而 open 的结果只能有一个 `Nodes` 列表。在这一层做一次归一，比让调用方按来源分支便宜。
/// </para>
/// </summary>
public sealed record OutlineEntry(
    string Name,
    string Kind,
    int StartLine,
    int EndLine,
    string? Signature = null,
    string? Container = null,
    IReadOnlyList<OutlineEntry>? Children = null);

/// <summary>视图负载的判别容器：以后加 summary / map 只是加子类，结果外形不变。</summary>
public abstract record WorkspaceOpenPayload;

/// <summary>文件切片负载。</summary>
public sealed record ExcerptPayload(
    IReadOnlyList<string> Lines,
    int StartLine,
    int TotalLineCount) : WorkspaceOpenPayload;

/// <summary>结构预览负载。</summary>
public sealed record OutlinePayload(
    IReadOnlyList<OutlineEntry> Nodes,
    int TotalCount,
    bool Truncated) : WorkspaceOpenPayload;

/// <summary>
/// 目录地图里的一个文件。
/// <para>
/// <b>为什么带 <paramref name="TotalTypeCount"/></b>：<see cref="Types"/> 被
/// <c>CodeProjectMapService.MaxTopLevelTypes</c> 截断（默认 3），若不给出真实总数，
/// 调用方会把「只列了 3 个」读成「这个文件只有 3 个类型」。这属于 ADR-089 §8.3 禁止的
/// hollow negation —— 不完全必须可见，不能当成完整成功。
/// </para>
/// </summary>
public sealed record MapFileEntry(string Name, IReadOnlyList<string> Types, int TotalTypeCount);

/// <summary>目录地图里的一个目录。</summary>
public sealed record MapEntry(
    string Name,
    IReadOnlyList<MapFileEntry> Files,
    IReadOnlyList<MapEntry> Children,
    int TotalFileCount);

/// <summary>目录地图负载。</summary>
public sealed record MapPayload(MapEntry Root, int TotalFileCount) : WorkspaceOpenPayload;

/// <summary>
/// <c>workspace_open</c> 的结果。构造即自校验：
/// 成功必须有负载、失败必须有原因、截断必须说明理由 —— 这三条都是<b>能失败的断言</b>，
/// 而不是文档里的约定。
/// </summary>
public sealed record WorkspaceOpenResult
{
    private WorkspaceOpenResult(
        bool success,
        string? error,
        WorkspaceOpenView view,
        string? resolvedPath,
        WorkspaceOpenPayload? payload,
        bool truncated,
        string? truncatedReason)
    {
        Success = success;
        Error = error;
        View = view;
        ResolvedPath = resolvedPath;
        Payload = payload;
        Truncated = truncated;
        TruncatedReason = truncatedReason;
    }

    public bool Success { get; }

    public string? Error { get; }

    public WorkspaceOpenView View { get; }

    public string? ResolvedPath { get; }

    public WorkspaceOpenPayload? Payload { get; }

    public bool Truncated { get; }

    public string? TruncatedReason { get; }

    /// <summary>成功结果。</summary>
    public static WorkspaceOpenResult Ok(
        WorkspaceOpenView view,
        string resolvedPath,
        WorkspaceOpenPayload payload,
        bool truncated = false,
        string? truncatedReason = null)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (truncated && string.IsNullOrWhiteSpace(truncatedReason))
        {
            throw new ArgumentException(
                "截断必须给原因：只说「被截断」调用方无法决定下一步看哪里。",
                nameof(truncatedReason));
        }

        return new WorkspaceOpenResult(true, null, view, resolvedPath, payload, truncated, truncatedReason);
    }

    /// <summary>失败结果。</summary>
    public static WorkspaceOpenResult Failed(WorkspaceOpenView view, string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("失败必须给原因。", nameof(error));
        }

        return new WorkspaceOpenResult(false, error, view, null, null, false, null);
    }
}

/// <summary>把服务结果映射成 open 契约的投影（解耦形状，不解耦数值来源）。</summary>
public static class WorkspaceOpenMapping
{
    /// <summary>C# 路径（<c>PuddingRuntime.Services.Tools.OutlineNode</c>）→ <see cref="OutlinePayload"/>。</summary>
    public static OutlinePayload FromCSharpOutline(CodeOutlineOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var nodes = outcome.CSharpNodes.Select(MapCSharp).ToList();
        return new OutlinePayload(nodes, outcome.TotalCount, outcome.Truncated);
    }

    /// <summary>注册表路径（<c>PuddingCodeIntelligence.Contracts.OutlineNode</c>）→ <see cref="OutlinePayload"/>。</summary>
    public static OutlinePayload FromRegistryOutline(CodeOutlineOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var nodes = outcome.OutlinerNodes.Select(MapRegistry).ToList();
        return new OutlinePayload(nodes, outcome.TotalCount, outcome.Truncated);
    }

    /// <summary>目录树 → <see cref="MapPayload"/>。</summary>
    public static MapPayload FromProjectMap(ProjectMapOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return new MapPayload(MapEntryOf(outcome.Root!), outcome.Root!.TotalFileCount);
    }

    private static MapEntry MapEntryOf(ProjectMapNode node) => new(
        node.Name,
        node.Files.Select(f => new MapFileEntry(f.Name, f.Types, f.TotalTypeCount)).ToList(),
        node.Children.Select(MapEntryOf).ToList(),
        node.TotalFileCount);

    private static OutlineEntry MapCSharp(OutlineNode node) => new(
        node.Name ?? "(anonymous)",
        node.Kind,
        node.Line,
        node.EndLine,
        node.Signature,
        null,
        node.Children is { Count: > 0 } ? node.Children.Select(MapCSharp).ToList() : null);

    private static OutlineEntry MapRegistry(PuddingCodeIntelligence.Contracts.OutlineNode node) => new(
        node.Name,
        node.Kind.ToString(),
        node.StartLine,
        node.EndLine,
        node.Signature,
        node.Container,
        node.Children is { Count: > 0 } ? node.Children.Select(MapRegistry).ToList() : null);
}

/// <summary>
/// <c>workspace_open</c> 的端口。实现负责路径解析、workspace 边界校验、读取与截断。
/// </summary>
public interface IWorkspaceOpenService
{
    Task<WorkspaceOpenResult> OpenAsync(WorkspaceOpenRequest request, CancellationToken cancellationToken);
}
