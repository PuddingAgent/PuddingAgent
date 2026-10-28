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
}

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
}

/// <summary>
/// <see cref="IRetrievalPort"/> 的第一个实现（R6 B1 切片一）。
/// <para>
/// <b>已支持</b>：<see cref="RetrievalIntent.Auto"/> / <see cref="RetrievalIntent.Namespace"/> /
/// <see cref="RetrievalIntent.Type"/> / <see cref="RetrievalIntent.Member"/> / <see cref="RetrievalIntent.Any"/>。
/// </para>
/// <para>
/// <b>未支持</b>（<see cref="RetrievalIntent.File"/> / <see cref="RetrievalIntent.Comment"/> /
/// <see cref="RetrievalIntent.Implementation"/> / <see cref="RetrievalIntent.References"/> /
/// <see cref="RetrievalIntent.Callers"/> / <see cref="RetrievalIntent.Callees"/> /
/// <see cref="RetrievalIntent.Impact"/>）：返回
/// <see cref="RetrievalEmptyReason.IntentUnsupported"/> + 替代方案，
/// <b>不伪装成空命中</b>（ADR-089 §8.3）。
/// 切片二接关系面（<c>ICodeIndexStore.ListRelationsAsync</c>）后逐个打开。
/// </para>
/// <para>
/// <b>ScopeId 语义（v1）</b>：等同于 code index 的 <c>workspaceId</c>。
/// </para>
/// </summary>
public sealed class RetrievalPortV1(IRetrievalSymbolSource source) : IRetrievalPort
{
    private readonly IRetrievalSymbolSource _source =
        source ?? throw new ArgumentNullException(nameof(source));

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
        RetrievalIntent.File,
        RetrievalIntent.Comment,
        RetrievalIntent.Implementation,
        RetrievalIntent.References,
        RetrievalIntent.Callers,
        RetrievalIntent.Callees,
        RetrievalIntent.Impact,
    ];

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
