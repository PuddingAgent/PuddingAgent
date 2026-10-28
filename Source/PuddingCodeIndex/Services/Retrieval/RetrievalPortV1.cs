using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Contracts.Retrieval;

namespace PuddingCodeIndex.Services.Retrieval;

/// <summary>
/// 检索端口需要的最小数据面（ADR-089 §2.1 第 1 条：单次调用、多路融合、一次交付）。
/// <para>
/// <b>为什么单独抽这一层</b>：<see cref="IRetrievalPort"/> 的实现要做意图路由、去重、排序、
/// 预算与空原因裁决；这些逻辑都能用"两个方法"喂饱。把它直接绑在
/// <see cref="ICodeIndexStore"/>（30+ 成员）上，测试就得实现一整个 store，
/// 于是测试会退化成"能编译就算过"。窄端口让测试可写、让依赖方向显式。
/// </para>
/// </summary>
public interface IRetrievalSymbolSource
{
    /// <summary>列出该 scope 下的项目（用于判定索引可用性 + 生成索引版本）。</summary>
    Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(
        string scopeId,
        CancellationToken cancellationToken);

    /// <summary>按符号检索请求取符号。</summary>
    Task<IReadOnlyList<CodeSymbolRecord>> SearchSymbolsAsync(
        CodeSymbolSearchRequest request,
        CancellationToken cancellationToken);

    /// <summary>取单个符号（关系面用：把关系边上的 symbolId 解析成可描述的对端符号）。</summary>
    Task<CodeSymbolRecord?> GetSymbolAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken);

    /// <summary>列出该项目下已索引的文件（文件面用：查询词匹配路径）。</summary>
    Task<IReadOnlyList<CodeFileRecord>> ListFilesAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken);

    /// <summary>指向该符号的关系边（谁调用了它 / 谁引用了它 / 谁实现了它）。</summary>
    Task<IReadOnlyList<CodeRelationRecord>> ListIncomingRelationsAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        IReadOnlyList<CodeRelationKind> relationKinds,
        CancellationToken cancellationToken);

    /// <summary>从该符号出发的关系边（它调用了谁）。</summary>
    Task<IReadOnlyList<CodeRelationRecord>> ListOutgoingRelationsAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        IReadOnlyList<CodeRelationKind> relationKinds,
        CancellationToken cancellationToken);
}

/// <summary>
/// 文本面数据源（注释面用）：把查询词匹配到文件行。
/// <para>
/// <b>为什么不直接依赖 PuddingFullTextIndex</b>：依赖方向必须由编译期强制 ——
/// 代码索引不该知道全文索引用的是 Lucene 还是别的。本接口只有“给定 scope + 查询词，
/// 返回文件行”，把 scopeId → 目录 的解析留给 Host。
/// </para>
/// </summary>
public interface IRetrievalTextSource
{
    /// <summary>在指定 scope 内检索文本，最多返回 <paramref name="maxMatches"/> 条文件行匹配。</summary>
    Task<IReadOnlyList<RetrievalTextMatch>> SearchAsync(
        string scopeId,
        string query,
        int maxMatches,
        CancellationToken cancellationToken);
}

/// <summary>文本面的一条匹配：文件 + 行号 + 该行文本。</summary>
public sealed record RetrievalTextMatch(string FilePath, int Line, string LineText);

/// <summary>把既有 <see cref="ICodeIndexStore"/> 适配成窄端口（生产路径唯一实现）。</summary>
public sealed class CodeIndexStoreSymbolSource(ICodeIndexStore store) : IRetrievalSymbolSource
{
    private readonly ICodeIndexStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    public Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(
        string scopeId,
        CancellationToken cancellationToken) =>
        _store.ListProjectsAsync(scopeId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<CodeSymbolRecord>> SearchSymbolsAsync(
        CodeSymbolSearchRequest request,
        CancellationToken cancellationToken) =>
        _store.SearchSymbolsAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<CodeSymbolRecord?> GetSymbolAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken) =>
        _store.GetSymbolAsync(workspaceId, projectId, symbolId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<CodeFileRecord>> ListFilesAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken) =>
        _store.ListFilesAsync(workspaceId, projectId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<CodeRelationRecord>> ListIncomingRelationsAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        IReadOnlyList<CodeRelationKind> relationKinds,
        CancellationToken cancellationToken) =>
        _store.ListIncomingRelationsAsync(
            workspaceId,
            projectId,
            symbolId,
            relationKind: null,
            relationKinds: relationKinds,
            cancellationToken: cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<CodeRelationRecord>> ListOutgoingRelationsAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        IReadOnlyList<CodeRelationKind> relationKinds,
        CancellationToken cancellationToken) =>
        _store.ListRelationsAsync(
            workspaceId,
            projectId,
            symbolId,
            relationKind: null,
            relationKinds: relationKinds,
            cancellationToken: cancellationToken);
}

/// <summary>
/// <see cref="IRetrievalPort"/> 的第一个实现。
/// <para>
/// <b>符号面</b>：<see cref="RetrievalIntent.Auto"/> / <see cref="RetrievalIntent.Namespace"/> /
/// <see cref="RetrievalIntent.Type"/> / <see cref="RetrievalIntent.Member"/> / <see cref="RetrievalIntent.Any"/>。
/// </para>
/// <para>
/// <b>关系面</b>（R6 B1 切片二）：<see cref="RetrievalIntent.Callers"/> /
/// <see cref="RetrievalIntent.Callees"/> / <see cref="RetrievalIntent.References"/> /
/// <see cref="RetrievalIntent.Implementation"/>。关系种类不在这里定义，
/// 而是取自冻结契约 <see cref="RetrievalIntentPolicy.RelationFilter"/>。
/// </para>
/// <para>
/// <b>文件面</b>（R6 B1 切片三）：<see cref="RetrievalIntent.File"/> ——
/// 查询词匹配已索引文件的<b>路径</b>，命中层为 <see cref="RetrievalHitKind.TextHit"/>、
/// 身份用 <see cref="SymbolIdentity.ForTextAt"/>（不归属任何符号）。
/// </para>
/// <para>
/// <b>注释面</b>（R6 B1 切片三）：<see cref="RetrievalIntent.Comment"/> —— 走注入的
/// <see cref="IRetrievalTextSource"/>；未注入时<b>显式不支持</b>，而不是先查再报空。
/// 每行按首字符判定是否注释行，分别归入 DocMention / TextHit。
/// </para>
/// <para>
/// <b>未支持</b>（<see cref="RetrievalIntent.Impact"/>）：返回
/// <see cref="RetrievalEmptyReason.IntentUnsupported"/> + 替代方案，
/// <b>不伪装成空命中</b>（ADR-089 §8.3）。Impact 需要多跳遍历（本刀只做深度 1）。
/// </para>
/// <para>
/// <b>ScopeId 语义（v1）</b>：等同于 code index 的 <c>workspaceId</c>。
/// </para>
/// </summary>
public sealed class RetrievalPortV1(IRetrievalSymbolSource source, IRetrievalTextSource? textSource = null) : IRetrievalPort
{
    private readonly IRetrievalSymbolSource _source =
        source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>文本数据面。为 <c>null</c> 时注释面报 IntentUnsupported，而不是先查再报空。</summary>
    private readonly IRetrievalTextSource? _textSource = textSource;

    /// <summary>意图 → 符号种类集合（ADR-089 §2.1；<c>null</c> = 不筛 kind）。</summary>
    private static readonly IReadOnlyDictionary<RetrievalIntent, IReadOnlyList<CodeSymbolKind>> IntentKinds =
        new Dictionary<RetrievalIntent, IReadOnlyList<CodeSymbolKind>>
        {
            [RetrievalIntent.Namespace] = [CodeSymbolKind.Namespace],
            [RetrievalIntent.Type] =
            [
                CodeSymbolKind.Class,
                CodeSymbolKind.Interface,
                CodeSymbolKind.Struct,
                CodeSymbolKind.Enum,
                CodeSymbolKind.Delegate,
            ],
            [RetrievalIntent.Member] =
            [
                CodeSymbolKind.Method,
                CodeSymbolKind.Constructor,
                CodeSymbolKind.Property,
                CodeSymbolKind.Field,
                CodeSymbolKind.Event,
                CodeSymbolKind.Operator,
            ],
        };

    /// <summary>v1 不支持的意图（显式列出，避免"忘了实现"被当成"确实没有"）。</summary>
    private static readonly RetrievalIntent[] UnsupportedIntents =
    [
        RetrievalIntent.Impact,
    ];

    /// <summary>注释面向文本面索要的匹配上限（到达上限时结果标 degraded，不声称完整）。</summary>
    private const int MaxTextMatches = 200;

    /// <summary>关系意图的方向：incoming = 指向目标的边（谁调用了它），outgoing = 从目标出发的边（它调用了谁）。</summary>
    private enum RelationDirection
    {
        Incoming,
        Outgoing,
    }

    private readonly record struct RelationIntentSpec(
        RelationDirection Direction,
        RetrievalHitKind HitKind,
        string Label);

    /// <summary>
    /// 关系面的四个意图（R6 B1 切片二）。
    /// <para>
    /// 关系种类<b>不在这里定义</b>，而是取自冻结契约
    /// <see cref="RetrievalIntentPolicy.RelationFilter"/> —— 两处各写一份必然漂移。
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<RetrievalIntent, RelationIntentSpec> RelationIntents =
        new Dictionary<RetrievalIntent, RelationIntentSpec>
        {
            [RetrievalIntent.Callers] = new(RelationDirection.Incoming, RetrievalHitKind.CallSite, "调用者"),
            [RetrievalIntent.Callees] = new(RelationDirection.Outgoing, RetrievalHitKind.CallSite, "被调用者"),
            [RetrievalIntent.References] = new(RelationDirection.Incoming, RetrievalHitKind.Reference, "引用者"),
            [RetrievalIntent.Implementation] = new(RelationDirection.Incoming, RetrievalHitKind.Implementation, "实现者"),
        };

    /// <summary>
    /// <see cref="RetrievalRelationKind"/> → <see cref="CodeRelationKind"/>（索引层实际写入的边）。
    /// <para>
    /// <b>为什么是显式表而不是按名字对拉</b>：两个枚举就不是同一套词表 ——
    /// <see cref="RetrievalRelationKind.Imports"/> 在索引层没有对应边（<c>CodeRelationKind</c> 无此值），
    /// 名字对齐会静默把「没有这种边」伪装成「查过但没有」。本表不收录的一律丢弃（并在
    /// <see cref="RetrieveRelationsAsync"/> 里对“全部丢弃”报 IntentUnsupported）。
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<RetrievalRelationKind, CodeRelationKind> RelationKindMap =
        new Dictionary<RetrievalRelationKind, CodeRelationKind>
        {
            [RetrievalRelationKind.Calls] = CodeRelationKind.Calls,
            [RetrievalRelationKind.References] = CodeRelationKind.References,
            [RetrievalRelationKind.Inherits] = CodeRelationKind.Inherits,
            [RetrievalRelationKind.Implements] = CodeRelationKind.Implements,
            [RetrievalRelationKind.Overrides] = CodeRelationKind.Overrides,
        };

    /// <summary>关系面的起点上限：查询过泛时宁可少展开几个起点，也不要把整张关系图读进内存。</summary>
    private const int MaxRelationTargets = 20;

    /// <inheritdoc />
    public async Task<RetrievalResult> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scopeId = request.ScopeId;
        var projects = await _source.ListProjectsAsync(scopeId, cancellationToken).ConfigureAwait(false);
        var indexVersion = BuildIndexVersion(projects);
        var availability = projects.Any(p => p.Status is CodeProjectStatus.Active or CodeProjectStatus.Registering)
            ? RetrievalIndexAvailability.Available
            : RetrievalIndexAvailability.NotIndexed;

        if (UnsupportedIntents.Contains(request.Intent))
        {
            return RetrievalResult.Empty(
                request,
                RetrievalEmptyReason.IntentUnsupported,
                RetrievalNextStep.UseAlternativeFace(
                    $"意图 {request.Intent} 尚未接入 v1；改用符号面（Type / Member / Any）或文件面检索。",
                    "RetrievalIntent.Type / Member / Any"),
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        if (availability != RetrievalIndexAvailability.Available)
        {
            return RetrievalResult.Empty(
                request,
                RetrievalEmptyReason.NotIndexed,
                RetrievalNextStep.RebuildIndex(
                    "该 scope 尚无可用代码索引；先注册并完成一次索引，再重试检索。",
                    scopeId),
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        // 四条路由各自分页：注释面走文本表，文件面走文件表，符号面走符号表，关系面走关系表。
        if (request.Intent == RetrievalIntent.Comment)
        {
            return await RetrieveCommentsAsync(request, scopeId, indexVersion, cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.Intent == RetrievalIntent.File)
        {
            return await RetrieveFilesAsync(request, scopeId, indexVersion, projects, cancellationToken)
                .ConfigureAwait(false);
        }

        // 关系面与符号面是两条路由：关系面的分页游标在“命中”层面生效（全量展开后切片），
        // 符号面的游标直接下推到 SQL 的 Skip。共用同一个 skip:N 编码，但不共用分页时机。
        if (RelationIntents.ContainsKey(request.Intent))
        {
            return await RetrieveRelationsAsync(request, scopeId, indexVersion, cancellationToken)
                .ConfigureAwait(false);
        }

        var offset = DecodeCursor(request.Cursor);
        if (offset is null)
        {
            return RetrievalResult.Empty(
                request,
                RetrievalEmptyReason.FilteredOut,
                RetrievalNextStep.Narrow(
                    RetrievalSuggestionKind.CompositeFilter,
                    "分页游标无效（非本端口签发）；去掉 cursor 重新检索。"),
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        // 多取一条用于判定"是否还有下一页"，避免把截断静默成完整返回。
        var records = await _source.SearchSymbolsAsync(
            new CodeSymbolSearchRequest(
                WorkspaceId: scopeId,
                Query: request.Query,
                ProjectId: null,
                Kind: null,
                Limit: request.PageSize + 1,
                Skip: offset.Value,
                MatchTarget: MapMatchTarget(request),
                FileExtensions: null,
                Kinds: IntentKinds.TryGetValue(request.Intent, out var kinds) ? kinds : null),
            cancellationToken).ConfigureAwait(false);

        var hasMore = records.Count > request.PageSize;
        var pageOfRecords = hasMore ? records.Take(request.PageSize).ToList() : records.ToList();

        var hits = Deduplicate(
            pageOfRecords
                .Select(r => MapHit(r, request, scopeId))
                .Where(h => request.Filter is null || request.Filter.Matches(h))
                .ToList(),
            request.Intent);

        if (hits.Count == 0)
        {
            var reason = RetrievalEmptyReasonPolicy.ClassifyForZeroHits(
                request.Filter,
                availability,
                RetrievalCapabilityLevel.Semantic,
                intentSupported: true);

            return RetrievalResult.Empty(
                request,
                reason,
                BuildEmptyStep(reason, request),
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        var totalCount = offset!.Value + hits.Count + (hasMore ? 1 : 0);

        if (!hasMore)
        {
            return RetrievalResult.Create(
                request,
                hits,
                totalCount,
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        // 部分返回：必须落盘 + 给分布 + 给游标（§2.2 A / §8.7），禁止静默截断。
        var spillPath = SpillPage(hits, request);
        return RetrievalResult.Create(
            request,
            hits,
            totalCount,
            indexVersion,
            nextSteps:
            [
                RetrievalNextStep.Narrow(
                    RetrievalSuggestionKind.CompositeFilter,
                    "结果超出页大小；用更具体的符号名、限定目录或显式 intent 收窄后重试。"),
            ],
            overflow: RetrievalOverflow.Create(spillPath, hits.Count, RetrievalBudgetKind.ItemCount),
            distribution: RetrievalDistribution.FromHits(hits),
            appliedFilters: AppliedFilters(request),
            nextCursor: EncodeCursor(offset.Value + hits.Count));
    }

    /// <summary>
    /// 注释面检索（<see cref="RetrievalIntent.Comment"/>）：查询词匹配文件行。
    /// <para>
    /// <b>未注入文本面时直接报 IntentUnsupported</b>：先去查、再报空，会把“没接数据面”
    /// 读成“确实没提过”——这正是 ADR-089 §8.3 禁止的空洞否定。
    /// </para>
    /// <para>
    /// <b>到达上限时标 degraded</b>：文本面最多返回 <see cref="MaxTextMatches"/> 条，
    /// 这时 totalCount 只能当下限用，不能声称“就这么多”。
    /// </para>
    /// </summary>
    private async Task<RetrievalResult> RetrieveCommentsAsync(
        RetrievalRequest request,
        string scopeId,
        string indexVersion,
        CancellationToken cancellationToken)
    {
        if (_textSource is null)
        {
            return RetrievalResult.Empty(
                request,
                RetrievalEmptyReason.IntentUnsupported,
                RetrievalNextStep.UseAlternativeFace(
                    "注释面需要文本/全文数据面；当前端口未注入 IRetrievalTextSource。改用 File 或符号面检索。",
                    "RetrievalIntent.File / Type / Member"),
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        var offset = DecodeCursor(request.Cursor);
        if (offset is null)
        {
            return RetrievalResult.Empty(
                request,
                RetrievalEmptyReason.FilteredOut,
                RetrievalNextStep.Narrow(
                    RetrievalSuggestionKind.CompositeFilter,
                    "分页游标无效（非本端口签发）；去掉 cursor 重新检索。"),
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        var matches = await _textSource
            .SearchAsync(scopeId, request.Query, MaxTextMatches, cancellationToken)
            .ConfigureAwait(false);

        var reachedCap = matches.Count >= MaxTextMatches;

        var hits = Deduplicate(
            matches
                .Select(m => MapTextHit(m, scopeId))
                .Where(h => request.Filter is null || request.Filter.Matches(h))
                .ToList(),
            request.Intent);

        if (hits.Count == 0)
        {
            var reason = RetrievalEmptyReasonPolicy.ClassifyForZeroHits(
                request.Filter,
                RetrievalIndexAvailability.Available,
                RetrievalCapabilityLevel.Semantic,
                intentSupported: true);

            return RetrievalResult.Empty(
                request,
                reason,
                BuildEmptyStep(reason, request),
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        if (offset.Value >= hits.Count)
        {
            return RetrievalResult.Empty(
                request,
                RetrievalEmptyReason.FilteredOut,
                RetrievalNextStep.Narrow(
                    RetrievalSuggestionKind.CompositeFilter,
                    $"分页游标越界（文本面已返回 {hits.Count} 条匹配）；去掉 cursor 重新检索。"),
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        var page = hits.Skip(offset.Value).Take(request.PageSize).ToList();
        var hasMore = offset.Value + page.Count < hits.Count;

        var degraded = reachedCap;
        var degradedReason = reachedCap
            ? $"文本面已按上限 {MaxTextMatches} 条返回，totalCount 只能当下限；结果可能不完整。"
            : null;

        if (!hasMore)
        {
            return RetrievalResult.Create(
                request,
                page,
                hits.Count,
                indexVersion,
                degraded: degraded,
                degradedReason: degradedReason,
                appliedFilters: AppliedFilters(request));
        }

        return RetrievalResult.Create(
            request,
            page,
            hits.Count,
            indexVersion,
            degraded: degraded,
            degradedReason: degradedReason,
            nextSteps:
            [
                RetrievalNextStep.Narrow(
                    RetrievalSuggestionKind.CompositeFilter,
                    "文本匹配超出页大小；加上目录、扩展名或更精确的词后重试。"),
            ],
            overflow: RetrievalOverflow.Create(SpillPage(page, request), page.Count, RetrievalBudgetKind.ItemCount),
            distribution: RetrievalDistribution.FromHits(page),
            appliedFilters: AppliedFilters(request),
            nextCursor: EncodeCursor(offset.Value + page.Count));
    }

    /// <summary>
    /// 文本行 ⇒ 命中。按行首字符区分注释行与代码正文：
    /// 注释归 <see cref="RetrievalHitKind.DocMention"/>，正文归 <see cref="RetrievalHitKind.TextHit"/>，
    /// 并把“到底是哪一种”写进 Why —— 否则调用方分不清“提到过”和“只是同名代码”。
    /// </summary>
    private static RetrievalHit MapTextHit(RetrievalTextMatch match, string scopeId)
    {
        var line = Math.Max(match.Line, 1);
        var text = (match.LineText ?? string.Empty).Trim();
        var isComment = LooksLikeComment(text);

        var display = text.Length == 0
            ? $"{Path.GetFileName(match.FilePath)}:{line}"
            : text.Length > 120 ? text[..120] : text;

        return new RetrievalHit(
            SymbolIdentity.ForTextAt(match.FilePath, line),
            display,
            CodeSymbolKind.Unknown,
            isComment ? RetrievalHitKind.DocMention : RetrievalHitKind.TextHit,
            new RetrievalEvidence(match.FilePath, line, line),
            RetrievalConfidence.Lexical,
            isComment ? 1.0 : 0.5,
            isComment
                ? "注释/文档行命中查询词"
                : "代码正文行命中查询词（不是注释）",
            isComment ? RetrievalMatchTarget.DocComment : RetrievalMatchTarget.Body,
            RetrievalRelationKind.Unknown,
            languageRawKind: null,
            container: null,
            scopeIds: [scopeId]);
    }

    /// <summary>行首注释标记判定（只认确定的几种；不认识的宁可当正文，也不假装是注释）。</summary>
    private static bool LooksLikeComment(string text) =>
        text.StartsWith("//", StringComparison.Ordinal)
        || text.StartsWith("/*", StringComparison.Ordinal)
        || text.StartsWith("*", StringComparison.Ordinal)
        || text.StartsWith("<!--", StringComparison.Ordinal);

    /// <summary>
    /// 文件面检索（<see cref="RetrievalIntent.File"/>）：查询词匹配已索引文件的路径。
    /// <para>
    /// <b>为什么是不是“列目录再过滤”</b>：本端口只接代码索引，索引里的文件表就是
    /// “已索引文件”的权威集合；未索引的文件本来就不该出现在检索结果里
    /// （否则会把“没索引”读成“没搜到”）。
    /// </para>
    /// <para>
    /// <b>代价是每次查询枚举全部项目下的全部文件记录</b>。v1 接受：文件记录只有
    /// path / language / lastIndexed 三列，不碰符号表。若将来文件规模成为瓶颈，
    /// 应在 store 层把路径匹配下推到 SQL，而不是在这一层加缓存。
    /// </para>
    /// </summary>
    private async Task<RetrievalResult> RetrieveFilesAsync(
        RetrievalRequest request,
        string scopeId,
        string indexVersion,
        IReadOnlyList<CodeProjectRecord> projects,
        CancellationToken cancellationToken)
    {
        var offset = DecodeCursor(request.Cursor);
        if (offset is null)
        {
            return RetrievalResult.Empty(
                request,
                RetrievalEmptyReason.FilteredOut,
                RetrievalNextStep.Narrow(
                    RetrievalSuggestionKind.CompositeFilter,
                    "分页游标无效（非本端口签发）；去掉 cursor 重新检索。"),
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        var collected = new List<RetrievalHit>();
        foreach (var project in projects)
        {
            var files = await _source
                .ListFilesAsync(scopeId, project.ProjectId, cancellationToken)
                .ConfigureAwait(false);

            foreach (var file in files)
            {
                var hit = MapFileHit(file, request, scopeId);
                if (hit is null || (request.Filter is not null && !request.Filter.Matches(hit)))
                    continue;

                collected.Add(hit);
            }
        }

        var hits = Deduplicate(collected, request.Intent);

        if (hits.Count == 0)
        {
            var reason = RetrievalEmptyReasonPolicy.ClassifyForZeroHits(
                request.Filter,
                RetrievalIndexAvailability.Available,
                RetrievalCapabilityLevel.Semantic,
                intentSupported: true);

            return RetrievalResult.Empty(
                request,
                reason,
                BuildEmptyStep(reason, request),
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        if (offset.Value >= hits.Count)
        {
            return RetrievalResult.Empty(
                request,
                RetrievalEmptyReason.FilteredOut,
                RetrievalNextStep.Narrow(
                    RetrievalSuggestionKind.CompositeFilter,
                    $"分页游标越界（文件面已全量枚举，共 {hits.Count} 个匹配文件）；去掉 cursor 重新检索。"),
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        var page = hits.Skip(offset.Value).Take(request.PageSize).ToList();
        var hasMore = offset.Value + page.Count < hits.Count;

        if (!hasMore)
        {
            return RetrievalResult.Create(
                request,
                page,
                hits.Count,
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        return RetrievalResult.Create(
            request,
            page,
            hits.Count,
            indexVersion,
            nextSteps:
            [
                RetrievalNextStep.Narrow(
                    RetrievalSuggestionKind.CompositeFilter,
                    "匹配文件超出页大小；加上目录或扩展名限定后重试。"),
            ],
            overflow: RetrievalOverflow.Create(SpillPage(page, request), page.Count, RetrievalBudgetKind.ItemCount),
            distribution: RetrievalDistribution.FromHits(page),
            appliedFilters: AppliedFilters(request),
            nextCursor: EncodeCursor(offset.Value + page.Count));
    }

    /// <summary>
    /// 路径匹配 ⇒ 文件命中；不匹配返回 <c>null</c>。
    /// <para>
    /// 因为文件表是<b>全量枚举</b>，“哪些文件算命中”必须在这里收口；否则每个已索引文件
    /// 都会变成一条命中，把“全量”伪装成“搜到很多”。
    /// </para>
    /// </summary>
    private static RetrievalHit? MapFileHit(CodeFileRecord file, RetrievalRequest request, string scopeId)
    {
        var query = request.Query;
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(file.FilePath))
            return null;

        var path = file.FilePath;
        if (!path.Contains(query, StringComparison.OrdinalIgnoreCase))
            return null;

        var fileName = Path.GetFileName(path);
        var nameEquals = string.Equals(fileName, query, StringComparison.OrdinalIgnoreCase);
        var nameContains = fileName.Contains(query, StringComparison.OrdinalIgnoreCase);

        var score = nameEquals ? 2.0 : nameContains ? 1.5 : 1.0;
        var why = nameEquals
            ? "文件名与查询完全相同（最高置信）"
            : nameContains
                ? "文件名包含查询词"
                : "文件路径包含查询词（不是文件名）";

        return new RetrievalHit(
            SymbolIdentity.ForTextAt(path, 1),
            string.IsNullOrWhiteSpace(fileName) ? path : fileName,
            CodeSymbolKind.Unknown,
            RetrievalHitKind.TextHit,
            new RetrievalEvidence(path, 1, 1),
            RetrievalConfidence.Lexical,
            score,
            why,
            RetrievalMatchTarget.Path,
            RetrievalRelationKind.Unknown,
            languageRawKind: file.Language,
            container: null,
            scopeIds: [scopeId]);
    }

    /// <summary>
    /// 关系面检索（<see cref="RetrievalIntent.Callers"/> / <see cref="RetrievalIntent.Callees"/> /
    /// <see cref="RetrievalIntent.References"/> / <see cref="RetrievalIntent.Implementation"/>）。
    /// <para>
    /// <b>两段式</b>：查询词只描述“名字”，而关系边挂在 symbolId 上 ⇒ 先把查询解析成目标符号，
    /// 再沿边展开。目标上限 <see cref="MaxRelationTargets"/>。
    /// </para>
    /// <para>
    /// <b>去重先于分页</b>：同一调用者在多处调用同一目标会产生多条边，按对端符号去重后
    /// 每个符号只留一条代表证据 —— 否则 10 个调用点会把 1 个调用者顶成 10 条命中。
    /// </para>
    /// </summary>
    private async Task<RetrievalResult> RetrieveRelationsAsync(
        RetrievalRequest request,
        string scopeId,
        string indexVersion,
        CancellationToken cancellationToken)
    {
        var spec = RelationIntents[request.Intent];

        var offset = DecodeCursor(request.Cursor);
        if (offset is null)
        {
            return RetrievalResult.Empty(
                request,
                RetrievalEmptyReason.FilteredOut,
                RetrievalNextStep.Narrow(
                    RetrievalSuggestionKind.CompositeFilter,
                    "分页游标无效（非本端口签发）；去掉 cursor 重新检索。"),
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        var policyKinds = RetrievalIntentPolicy.RelationFilter(request.Intent);
        var relationKinds = policyKinds is null
            ? []
            : policyKinds.Where(RelationKindMap.ContainsKey).Select(k => RelationKindMap[k]).Distinct().ToList();

        if (relationKinds.Count == 0)
        {
            // 冻结契约给出的关系种类在索引层一条边都没有 ⇒ 是“能力没接”，不是“没有数据”。
            return RetrievalResult.Empty(
                request,
                RetrievalEmptyReason.IntentUnsupported,
                RetrievalNextStep.UseAlternativeFace(
                    $"意图 {request.Intent} 需要的关系边类型在索引层尚不存在；改用符号面检索。",
                    "RetrievalIntent.Type / Member / Any"),
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        var targets = await _source.SearchSymbolsAsync(
            new CodeSymbolSearchRequest(
                WorkspaceId: scopeId,
                Query: request.Query,
                ProjectId: null,
                Kind: null,
                Limit: MaxRelationTargets,
                Skip: 0,
                MatchTarget: MapMatchTarget(request),
                FileExtensions: null,
                Kinds: null),
            cancellationToken).ConfigureAwait(false);

        if (targets.Count == 0)
        {
            var reason = RetrievalEmptyReasonPolicy.ClassifyForZeroHits(
                request.Filter,
                RetrievalIndexAvailability.Available,
                RetrievalCapabilityLevel.Semantic,
                intentSupported: true);

            return RetrievalResult.Empty(
                request,
                reason,
                BuildEmptyStep(reason, request),
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        var collected = new List<RetrievalHit>();
        foreach (var target in targets)
        {
            var relations = spec.Direction == RelationDirection.Incoming
                ? await _source
                    .ListIncomingRelationsAsync(scopeId, target.ProjectId, target.SymbolId, relationKinds, cancellationToken)
                    .ConfigureAwait(false)
                : await _source
                    .ListOutgoingRelationsAsync(scopeId, target.ProjectId, target.SymbolId, relationKinds, cancellationToken)
                    .ConfigureAwait(false);

            foreach (var relation in relations)
            {
                var otherSymbolId = spec.Direction == RelationDirection.Incoming
                    ? relation.SourceSymbolId
                    : relation.TargetSymbolId;

                var other = await _source
                    .GetSymbolAsync(scopeId, relation.ProjectId, otherSymbolId, cancellationToken)
                    .ConfigureAwait(false);

                collected.Add(MapRelationHit(scopeId, target, relation, otherSymbolId, other, spec));
            }
        }

        var hits = Deduplicate(
            collected.Where(h => request.Filter is null || request.Filter.Matches(h)).ToList(),
            request.Intent);

        if (hits.Count == 0)
        {
            var reason = RetrievalEmptyReasonPolicy.ClassifyForZeroHits(
                request.Filter,
                RetrievalIndexAvailability.Available,
                RetrievalCapabilityLevel.Semantic,
                intentSupported: true);

            return RetrievalResult.Empty(
                request,
                reason,
                BuildEmptyStep(reason, request),
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        if (offset.Value >= hits.Count)
        {
            return RetrievalResult.Empty(
                request,
                RetrievalEmptyReason.FilteredOut,
                RetrievalNextStep.Narrow(
                    RetrievalSuggestionKind.CompositeFilter,
                    $"分页游标越界（关系面已全量展开，共 {hits.Count} 条）；去掉 cursor 重新检索。"),
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        var page = hits.Skip(offset.Value).Take(request.PageSize).ToList();
        var hasMore = offset.Value + page.Count < hits.Count;

        if (!hasMore)
        {
            return RetrievalResult.Create(
                request,
                page,
                hits.Count,
                indexVersion,
                appliedFilters: AppliedFilters(request));
        }

        return RetrievalResult.Create(
            request,
            page,
            hits.Count,
            indexVersion,
            nextSteps:
            [
                RetrievalNextStep.Narrow(
                    RetrievalSuggestionKind.CompositeFilter,
                    "关系结果超出页大小；用更明确的符号名或关系过滤收窄后重试。"),
            ],
            overflow: RetrievalOverflow.Create(SpillPage(page, request), page.Count, RetrievalBudgetKind.ItemCount),
            distribution: RetrievalDistribution.FromHits(page),
            appliedFilters: AppliedFilters(request),
            nextCursor: EncodeCursor(offset.Value + page.Count));
    }

    /// <summary>
    /// 把一条关系边映射成命中。
    /// <para>
    /// <b>身份与证据刻意分开</b>：身份 = 对端符号的<b>声明处</b>（同一声明被多处调用 ⇒ 身份相同 ⇒
    /// 先去重），证据 = 关系边<b>所在处</b>（调用点 / 引用点），这样命中既能回答“是谁”，
    /// 也能回答“凭什么说”。
    /// </para>
    /// </summary>
    private static RetrievalHit MapRelationHit(
        string scopeId,
        CodeSymbolRecord target,
        CodeRelationRecord relation,
        string otherSymbolId,
        CodeSymbolRecord? other,
        RelationIntentSpec spec)
    {
        var evidencePath = relation.SourceFilePath ?? other?.FilePath ?? target.FilePath;
        var evidenceLine = relation.SourceLine ?? other?.StartLine ?? target.StartLine;
        var evidenceEnd = relation.SourceLine is { } sourceLine
            ? sourceLine
            : other?.EndLine ?? evidenceLine;

        var symbolId = other?.SymbolId ?? otherSymbolId;
        var name = other?.Name ?? $"(未索引符号 {otherSymbolId})";
        var kind = other?.Kind ?? CodeSymbolKind.Unknown;
        var declarationLine = other?.StartLine ?? target.StartLine;

        var why = other is null
            ? $"通过 {relation.Kind} 关系从「{target.Name}」到达 {otherSymbolId}，但对端符号未在索引中（索引不一致）"
            : $"通过 {relation.Kind} 关系从「{target.Name}」到达的{spec.Label}（深度 1）";

        return new RetrievalHit(
            SymbolIdentity.Create(symbolId, other?.FilePath ?? evidencePath, declarationLine),
            name,
            kind,
            spec.HitKind,
            new RetrievalEvidence(evidencePath, evidenceLine, evidenceEnd),
            other is null ? RetrievalConfidence.Unknown : RetrievalConfidence.Semantic,
            1.0,
            why,
            RetrievalMatchTarget.SymbolName,
            MapRelationKind(relation.Kind),
            languageRawKind: null,
            container: other?.Container ?? target.Container,
            scopeIds: [scopeId]);
    }

    /// <summary>索引层关系边 → 检索面关系类型（<see cref="CodeRelationKind.Uses"/> 归入 References）。</summary>
    private static RetrievalRelationKind MapRelationKind(CodeRelationKind kind) => kind switch
    {
        CodeRelationKind.Calls => RetrievalRelationKind.Calls,
        CodeRelationKind.References or CodeRelationKind.Uses => RetrievalRelationKind.References,
        CodeRelationKind.Inherits => RetrievalRelationKind.Inherits,
        CodeRelationKind.Implements => RetrievalRelationKind.Implements,
        CodeRelationKind.Overrides => RetrievalRelationKind.Overrides,
        _ => RetrievalRelationKind.Unknown,
    };

    /// <summary>把索引记录映射成命中。命中层恒为声明处（索引只描述声明）。</summary>
    private static RetrievalHit MapHit(CodeSymbolRecord record, RetrievalRequest request, string scopeId)
    {
        var nameEquals = string.Equals(record.Name, request.Query, StringComparison.OrdinalIgnoreCase);
        var nameContains = record.Name.Contains(request.Query, StringComparison.OrdinalIgnoreCase);

        var matchTargets = nameContains || nameEquals
            ? RetrievalMatchTarget.SymbolName
            : RetrievalMatchTarget.Declaration;

        var score = nameEquals ? 2.0 : nameContains ? 1.0 : 0.5;
        var why = nameEquals
            ? "符号名与查询完全相同（最高置信）"
            : nameContains
                ? "符号名包含查询词"
                : "查询词命中声明文本（签名/容器），不是符号名";

        return new RetrievalHit(
            SymbolIdentity.Create(record.SymbolId, record.FilePath, record.StartLine),
            record.Name,
            record.Kind,
            RetrievalHitKind.Declaration,
            new RetrievalEvidence(record.FilePath, record.StartLine, record.EndLine),
            RetrievalConfidence.Semantic,
            score,
            why,
            matchTargets,
            RetrievalRelationKind.Unknown,
            languageRawKind: null,
            container: record.Container,
            scopeIds: [scopeId]);
    }

    /// <summary>
    /// 跨 scope 去重（§2.4）：按 <see cref="SymbolIdentity.Key"/> 归并，保留得分更高的那条，
    /// 并把多 scope 合成佐证。必须先于过载判定 —— 否则"索引结构性膨胀"会被误判成"查询过泛"。
    /// </summary>
    private static List<RetrievalHit> Deduplicate(List<RetrievalHit> hits, RetrievalIntent intent)
    {
        var byIdentity = new Dictionary<string, RetrievalHit>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var hit in hits)
        {
            var key = hit.Identity.Key;
            if (byIdentity.TryGetValue(key, out var existing))
            {
                var mergedScopes = existing.Scopes.Union(hit.Scopes).Ids;
                var keep = hit.Score > existing.Score ? hit : existing;
                byIdentity[key] = keep.WithScopes(mergedScopes);
                continue;
            }

            byIdentity[key] = hit;
            order.Add(key);
        }

        var deduped = order.Select(k => byIdentity[k]).ToList();
        return [.. RetrievalHitComparer.ForIntent(intent).Sort(deduped)];
    }

    private static RetrievalNextStep BuildEmptyStep(RetrievalEmptyReason reason, RetrievalRequest request)
    {
        switch (reason)
        {
            case RetrievalEmptyReason.NotIndexed:
            case RetrievalEmptyReason.IndexCorrupted:
                return RetrievalNextStep.RebuildIndex("索引不可用；先重建索引再重试。", request.ScopeId);

            case RetrievalEmptyReason.LanguageUnsupported:
            case RetrievalEmptyReason.IntentUnsupported:
                return RetrievalNextStep.UseAlternativeFace(
                    "该意图/语言能力尚未接入；改用符号面检索。",
                    "RetrievalIntent.Type / Member / Any");

            case RetrievalEmptyReason.FilteredOut:
                return RetrievalNextStep.Relax(
                    RetrievalEmptyReasonPolicy.SuggestRelaxationFacet(request.Filter!),
                    "过滤面导致 0 命中；放宽该面后重试。");

            default:
                return RetrievalNextStep.AcceptAbsence("索引可用、过滤面干净，确实没有匹配项。");
        }
    }

    private static CodeSymbolMatchTarget MapMatchTarget(RetrievalRequest request)
    {
        var targets = request.Filter?.MatchTargets;
        if (targets == RetrievalMatchTarget.SymbolName)
            return CodeSymbolMatchTarget.Name;

        if (targets is not null && (targets & RetrievalMatchTarget.SymbolName) != 0
            && (targets & (RetrievalMatchTarget.Declaration | RetrievalMatchTarget.Body
                | RetrievalMatchTarget.DocComment)) == 0)
        {
            return CodeSymbolMatchTarget.Name;
        }

        return CodeSymbolMatchTarget.All;
    }

    private static IReadOnlyList<string>? AppliedFilters(RetrievalRequest request)
    {
        var describe = request.Filter?.Describe();
        return string.IsNullOrWhiteSpace(describe) || describe == "no-filter" ? null : [describe];
    }

    /// <summary>索引版本：由项目集合决定（同一 scope + 同一索引状态 ⇒ 同一字符串）。</summary>
    private static string BuildIndexVersion(IReadOnlyList<CodeProjectRecord> projects)
    {
        if (projects.Count == 0)
            return "empty";

        var builder = new StringBuilder();
        foreach (var project in projects.OrderBy(p => p.ProjectId, StringComparer.Ordinal))
        {
            builder.Append(project.ProjectId).Append(':').Append(project.Status)
                .Append(':').Append(project.UpdatedAtUtc?.UtcTicks ?? 0).Append(';');
        }

        return builder.ToString();
    }

    private static string EncodeCursor(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"skip:{offset}"));

    private static int? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return 0;

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (!raw.StartsWith("skip:", StringComparison.Ordinal))
                return null;

            return int.TryParse(raw["skip:".Length..], out var value) && value >= 0 ? value : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>把本页落盘到系统临时目录（<see cref="RetrievalSpillPolicy"/> 允许范围之一）。</summary>
    private static string SpillPage(IReadOnlyList<RetrievalHit> hits, RetrievalRequest request)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pudding-retrieval");
        Directory.CreateDirectory(directory);

        var body = string.Join(
            Environment.NewLine,
            hits.Select(h =>
                $"{h.Identity.Key}\t{h.SymbolKind}\t{h.Score.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}\t{h.Evidence.FilePath}:{h.Evidence.Line}"));

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(request.Query + "|" + request.ScopeId + "|" + body));
        var path = Path.Combine(directory, Convert.ToHexString(digest)[..16].ToLowerInvariant() + ".txt");
        File.WriteAllText(path, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
