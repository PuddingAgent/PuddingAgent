using PuddingCodeIndex.Contracts.Retrieval;
using PuddingCodeIntelligence.Contracts;

namespace PuddingRuntime.Services.Tools;

/// <summary>workspace_open 支持的视图（v1：excerpt / outline / map / relations）。</summary>
public enum WorkspaceOpenView
{
    /// <summary>文件切片：给路径 + 行范围，拿回那几行。</summary>
    Excerpt = 0,

    /// <summary>结构预览：给路径，拿回顶层类型树。</summary>
    Outline = 1,

    /// <summary>目录地图：给目录路径，拿回目录树与每个文件的核心类型。</summary>
    Map = 2,

    /// <summary>关系：给 ref + relation，拿回该符号的调用方 / 被调用方 / 引用 / 实现 / 传递影响。</summary>
    Relations = 3,
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
    string? Ref = null,

    /// <summary>
    /// <c>view=relations</c> 专用：关系种类 —— <c>references</c> / <c>callers</c> / <c>callees</c> /
    /// <c>implementation</c> / <c>impact</c>（规格 §8.3）。缺失或未知值一律显式报错，不猜默认种类。
    /// </summary>
    string? Relation = null);

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

        var hasRelation = !string.IsNullOrWhiteSpace(request.Relation);

        if (request.View == WorkspaceOpenView.Relations)
        {
            if (!hasRef)
            {
                return "view=relations 必须给 ref：关系是按符号身份查的，path 给不出\"哪个符号\"。";
            }

            if (!hasRelation)
            {
                return "view=relations 必须给 relation（references / callers / callees / implementation / impact）。";
            }
        }
        else if (hasRelation)
        {
            // 显式拒绝而不是静默忽略 —— 给了 relation 却跑 excerpt，等于把参数当空气。
            return $"relation 只用于 view=relations（当前 view={request.View.ToString().ToLowerInvariant()}）。";
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
/// 关系条目 —— <b>只写来源真的给了什么</b>。
/// <para>
/// 关系记录的 <c>SourceLine</c> 指向<b>关系发生处（源侧）</b>，因此对 <c>callees</c>（取下游符号）
/// 而言它属于<b>当前符号自己</b>、不是下游符号的位置。拿不到的位置一律留 <c>null</c>，
/// 不用当前行号顶替 —— 那会让调用方以为下一跳就长在那里。
/// </para>
/// </summary>
public sealed record RelationEntry(
    string SymbolId,
    string Kind,
    string? FilePath = null,
    int? Line = null);

/// <summary>
/// 关系负载。
/// <para>
/// <b>空结果必须带说明</b>：按 <c>symbol_id</c> 精确查不到关系，既可能是「确实没有」，
/// 也可能是「索引还没建完」。ADR-089 §8.3 禁止把后者说成前者，故 <paramref name="Note"/> 在
/// 0 条时必填（由构造校验强制）。
/// </para>
/// </summary>
public sealed record RelationsPayload(
    IReadOnlyList<RelationEntry> Entries,
    int TotalCount,
    string Relation,
    string? Note = null) : WorkspaceOpenPayload
{
    /// <summary>0 条关系却没有任何说明 ⇒ 参数错误（空洞否定）。</summary>
    public void Validate()
    {
        if (Entries.Count == 0 && string.IsNullOrWhiteSpace(Note))
        {
            throw new ArgumentException(
                "0 条关系必须给出说明：可能是确实没有，也可能是索引未建完，不能默认说成\"没有\"。",
                nameof(Note));
        }
    }
}

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
public sealed record MapPayload(MapEntry Root, int TotalFileCount) : WorkspaceOpenPayload
{
    /// <summary>
    /// <see cref="TotalFileCount"/> 的口径：顶层值是「本图所有已展开目录的文件数之和」，
    /// 与节点级的「该目录真实文件数」不是一回事。不带这句时，根目录恰好没有直属 .cs
    /// 会让顶层报 0，被调用方读成「地图里没有文件」（ADR-089 §8.3）。
    /// </summary>
    public const string TotalFileCountScope =
        "本图所有已展开目录的 .cs 文件数之和（受 depth 限制，未展开层级不计）；单目录真实数见各节点 total_file_count";
}

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

        var root = MapEntryOf(outcome.Root!);
        return new MapPayload(root, SumFileCount(root));
    }

    /// <summary>
    /// 树内所有节点的直属文件数之和。
    /// 顶层 <c>TotalFileCount</c> 必须用它：仓库根的 Source 目录常常没有直属 .cs，
    /// 直接用 <c>root.TotalFileCount</c> 会让一份有几千个文件的地图报 0
    /// （ADR-089 §8.3：不得把「局部为 0」当成「整体为空」）。
    /// </summary>
    private static int SumFileCount(MapEntry node) =>
        node.TotalFileCount + node.Children.Sum(child => SumFileCount(child));

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
