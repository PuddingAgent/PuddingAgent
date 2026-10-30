using System.Text;
using PuddingCodeIndex.Contracts;
using PuddingRetrieval.Contracts;
using PuddingCodeIndex.Services.Retrieval;
using PuddingCodeIntelligence.Contracts;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// <see cref="IWorkspaceOpenService"/> 的 v1 实现：<c>excerpt</c>（文件切片）/ <c>outline</c>（结构预览）/
/// <c>map</c>（目录地图）/ <c>relations</c>（关系：callers / callees / references / implementation / impact）。
/// <para>
/// <b>边界校验在本层</b>：契约只表达意图（<c>Path</c>），策略在这里 —— 路径必须落在
/// 该 workspace 的 scope 根子树内。这比 <c>code_outline</c> 严，因为 open 属于检索面。
/// </para>
/// </summary>
public sealed class WorkspaceOpenService(
    ICodeOutlineService? outlineService,
    ICodeIndexScopeResolver? resolver,
    ICodeProjectMapService? mapService = null,
    ISymbolRefSource? refSource = null,
    ICodeIndexStore? indexStore = null,
    ICodeQueryService? codeQueryService = null) : IWorkspaceOpenService
{
    /// <summary>excerpt 未给行数时默认取多少行。</summary>
    public const int DefaultExcerptLines = 80;

    /// <inheritdoc />
    public async Task<WorkspaceOpenResult> OpenAsync(
        WorkspaceOpenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (resolver is null)
        {
            return WorkspaceOpenResult.Failed(request.View, "ICodeIndexScopeResolver is not registered.");
        }

        if (string.IsNullOrWhiteSpace(request.Path) && string.IsNullOrWhiteSpace(request.Ref))
        {
            // ref 与 path 二选一：两者都空才报缺参数（ref 分支在下面单独走）。
            return WorkspaceOpenResult.Failed(request.View, "必须给 ref 或 path 之一。");
        }

        var resolution = await resolver
            .ResolveAsync(request.WorkspaceId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (resolution is null)
        {
            return WorkspaceOpenResult.Failed(request.View, $"(未解析到工作区 scope: {request.WorkspaceId})");
        }

        var root = resolution.Scope.RootPath;

        if (!string.IsNullOrWhiteSpace(request.Ref))
        {
            return await OpenByRefAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var fullPath = Path.GetFullPath(
            Path.IsPathRooted(request.Path) ? request.Path : Path.Combine(root, request.Path));

        if (!IsWithin(root, fullPath))
        {
            return WorkspaceOpenResult.Failed(
                request.View,
                $"路径不在工作区内: {request.Path}（工作区根: {root}）");
        }

        if (request.View == WorkspaceOpenView.Map)
        {
            return await OpenMapAsync(request, root, fullPath, cancellationToken).ConfigureAwait(false);
        }

        if (request.View == WorkspaceOpenView.Outline)
        {
            return await OpenOutlineAsync(request, fullPath, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(fullPath))
        {
            return WorkspaceOpenResult.Failed(request.View, $"(文件不存在: {request.Path})");
        }

        return await OpenExcerptAsync(request, fullPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// ref 分支：先做 FRESH / STALE 判定，**Fresh 是唯一允许按 ref 行号打开的状态**。
    /// <para>
    /// Stale 一律报 <c>stale_ref</c>，即使重定位成功也不代替打开 ——「找到候选」与「旧 ref 仍有效」
    /// 是两件事，混起来就是又一次 hollow negation。
    /// </para>
    /// </summary>
    private async Task<WorkspaceOpenResult> OpenByRefAsync(
        WorkspaceOpenRequest request,
        CancellationToken cancellationToken)
    {
        if (!SymbolRefV1.TryParse(request.Ref, out var parsed, out var refError))
        {
            return WorkspaceOpenResult.Failed(request.View, "ref 不是合法的符号引用：" + refError);
        }

        if (refSource is null)
        {
            return WorkspaceOpenResult.Failed(
                request.View,
                "ref 打开尚未接线：需要先用索引版本做 FRESH/STALE 判定（§8.3 不得沿旧行号打开当前代码）。" +
                "请改用 project_id+path，或先用 workspace_search 拿到当前行号再按 path 打开。");
        }

        if (request.View is not (WorkspaceOpenView.Excerpt or WorkspaceOpenView.Relations))
        {
            return WorkspaceOpenResult.Failed(
                request.View,
                $"ref 打开 v1 只支持 view=excerpt / relations（当前 {request.View}）；请先按 ref 拿到行号再选视图。");
        }

        var reference = parsed!;
        var currentHash = await refSource
            .GetFileContentHashAsync(request.WorkspaceId, reference.ProjectId, reference.FilePath, cancellationToken)
            .ConfigureAwait(false);

        var candidates = await refSource
            .FindSymbolCandidatesAsync(request.WorkspaceId, reference.ProjectId, reference.SymbolId, cancellationToken)
            .ConfigureAwait(false);

        var verdict = SymbolRefFreshness.Evaluate(reference, currentHash, candidates);

        if (verdict.State != SymbolRefState.Fresh)
        {
            var hint = verdict.CanRelocate
                ? $"候选: {verdict.Relocations[0].FilePath}:{verdict.Relocations[0].StartLine}-" +
                  $"{verdict.Relocations[0].EndLine}"
                : string.Empty;

            return WorkspaceOpenResult.Failed(
                request.View,
                "stale_ref：" + verdict.Reason + (hint.Length == 0 ? string.Empty : " " + hint));
        }

        if (request.View == WorkspaceOpenView.Relations)
        {
            return await OpenRelationsAsync(request, reference, cancellationToken).ConfigureAwait(false);
        }

        var fullPath = await ResolveRefFilePathAsync(request, reference, cancellationToken)
            .ConfigureAwait(false);

        if (fullPath is null)
        {
            return WorkspaceOpenResult.Failed(
                request.View,
                $"ref 的路径是项目内相对路径（{reference.FilePath}），但项目 {reference.ProjectId} " +
                "没有可用的根目录记录，无法确定要打开哪个文件。" +
                "（不回落到工作区根：那会把另一个同名文件当成 ref 指向的文件）");
        }

        if (!File.Exists(fullPath))
        {
            return WorkspaceOpenResult.Failed(request.View, $"(文件不存在: {reference.FilePath})");
        }

        var ranged = request with
        {
            Path = reference.FilePath,
            StartLine = reference.StartLine,
            LineCount = reference.EndLine - reference.StartLine + 1,
        };

        return await OpenExcerptAsync(ranged, fullPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把 ref 的项目内相对路径解析成绝对路径。
    /// <para>
    /// 基准必须是**项目根**而不是工作区根：ref 的路径相对的是项目（<see cref="SymbolRefV1"/> 契约）。
    /// 用工作区根去拼会得到一个仍在工作区内、但指向不存在文件的路径 —— 边界检查照样通过，
    /// 只在 <c>File.Exists</c> 处失败并报「文件不存在」，看起来像文件被删了（实测踩过）。
    /// </para>
    /// <para>
    /// <c>..</c> 与绝对路径已由 <see cref="SymbolRefV1.Parse"/> 拒绝，所以这里不再做逃逸检查
    /// —— 边界在契约层，同一件事实现两份只会让两份慢慢不一致。
    /// </para>
    /// <para>
    /// 项目根未知时返回 <c>null</c>（由调用方显式失败），**不回落工作区根**。
    /// </para>
    /// </summary>
    private async Task<string?> ResolveRefFilePathAsync(
        WorkspaceOpenRequest request,
        SymbolRefV1 reference,
        CancellationToken cancellationToken)
    {
        var projectRoot = await refSource!
            .GetProjectRootAsync(request.WorkspaceId, reference.ProjectId, cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(projectRoot)
            ? null
            : Path.GetFullPath(Path.Combine(projectRoot, reference.FilePath));
    }

    /// <summary>
    /// relations 视图：按 <c>relation</c> 取该符号的关系。
    /// <para>
    /// 三种来源各有归属：<c>callers</c> / <c>references</c> / <c>implementation</c> 是<b>入边</b>
    /// （别人指向本符号），<c>callees</c> 是<b>出边</b>（本符号指向别人），<c>impact</c> 由查询服务做多跳 BFS。
    /// 入/出边的区别决定「条目上的文件行号属于谁」：入边属于对方，出边那一行属于<b>当前符号自己</b>，
    /// 所以出边一律不给文件行号 —— 宁可少给，也不能让调用方把当前行当成下一跳的位置。
    /// </para>
    /// </summary>
    private async Task<WorkspaceOpenResult> OpenRelationsAsync(
        WorkspaceOpenRequest request,
        SymbolRefV1 reference,
        CancellationToken cancellationToken)
    {
        const string allowed = "references / callers / callees / implementation / impact";
        var relation = (request.Relation ?? string.Empty).Trim().ToLowerInvariant();
        var entries = new List<RelationEntry>();

        switch (relation)
        {
            case "callers":
            case "references":
            case "implementation":
            {
                if (indexStore is null)
                {
                    return WorkspaceOpenResult.Failed(
                        request.View, "ICodeIndexStore is not registered: 关系查询不可用。");
                }

                var kind = relation switch
                {
                    "callers" => CodeRelationKind.Calls,
                    "references" => CodeRelationKind.References,
                    _ => CodeRelationKind.Implements,
                };

                var incoming = await indexStore
                    .ListIncomingRelationsAsync(
                        request.WorkspaceId,
                        reference.ProjectId,
                        reference.SymbolId,
                        kind,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                entries.AddRange(incoming.Select(r => new RelationEntry(
                    r.SourceSymbolId,
                    kind.ToString().ToLowerInvariant(),
                    r.SourceFilePath,
                    r.SourceLine)));
                break;
            }

            case "callees":
            {
                if (indexStore is null)
                {
                    return WorkspaceOpenResult.Failed(
                        request.View, "ICodeIndexStore is not registered: 关系查询不可用。");
                }

                var outgoing = await indexStore
                    .ListRelationsAsync(
                        request.WorkspaceId,
                        reference.ProjectId,
                        reference.SymbolId,
                        CodeRelationKind.Calls,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                // 出边：记录里的文件行号属于**当前符号**，不是下游符号 ⇒ 只给 id。
                entries.AddRange(outgoing.Select(r => new RelationEntry(
                    r.TargetSymbolId,
                    CodeRelationKind.Calls.ToString().ToLowerInvariant())));
                break;
            }

            case "impact":
            {
                if (codeQueryService is null)
                {
                    return WorkspaceOpenResult.Failed(
                        request.View, "ICodeQueryService is not registered: impact 不可用。");
                }

                var symbols = await codeQueryService
                    .GetImpactAsync(
                        request.WorkspaceId,
                        reference.ProjectId,
                        reference.SymbolId,
                        request.Depth ?? 2,
                        cancellationToken)
                    .ConfigureAwait(false);

                entries.AddRange(symbols.Select(s => new RelationEntry(
                    s.SymbolId,
                    s.Kind.ToString().ToLowerInvariant(),
                    s.FilePath,
                    s.StartLine)));
                break;
            }

            default:
                return WorkspaceOpenResult.Failed(
                    request.View,
                    $"不支持的 relation: {request.Relation}（支持 {allowed}）。");
        }

        // ADR-089 §8.3：0 条不得静默等于「没有」—— 也可能是索引尚未建成。
        var note = entries.Count == 0
            ? $"按 symbol_id 精确查询 {relation} 得到 0 条：可能是该符号确实没有此类关系，" +
              "也可能是索引尚未建成或未收录该符号。"
            : null;

        note = await RefineEmptyRelationsNoteAsync(note, request, reference, cancellationToken)
            .ConfigureAwait(false);

        var payload = new RelationsPayload(entries, entries.Count, relation, note);
        payload.Validate();
        return WorkspaceOpenResult.Ok(request.View, reference.FilePath, payload);
    }

    /// <summary>
    /// 给 0 条关系的说明补上「锚点是容器」这层事实（ADR-089 §8.3）。
    /// <para>
    /// 原说明只说「可能没有此类关系，也可能索引没建成」—— 对容器符号，这个二分法把调用方
    /// 引向「去查索引」，而正确下一步是「去看成员」。容器判定与文案都取自
    /// <see cref="ContainerSymbolGuidance"/>，与关系面检索共用同一份，
    /// 否则同一个问题在两个面会给出不同答案。
    /// </para>
    /// <para>
    /// 查不到符号时不猜：<b>原样返回</b>调用方给的说明。
    /// </para>
    /// </summary>
    private async Task<string?> RefineEmptyRelationsNoteAsync(
        string? note,
        WorkspaceOpenRequest request,
        SymbolRefV1 reference,
        CancellationToken cancellationToken)
    {
        if (note is null || indexStore is null)
        {
            return note;
        }

        var symbol = await indexStore
            .GetSymbolAsync(request.WorkspaceId, reference.ProjectId, reference.SymbolId, cancellationToken)
            .ConfigureAwait(false);

        if (symbol is null || !ContainerSymbolGuidance.IsContainer(symbol.Kind))
        {
            return note;
        }

        return ContainerSymbolGuidance.BuildEmptyRelationNote(
            symbol.Name,
            symbol.Kind,
            ContainerSymbolGuidance.OpenViewFace);
    }

    private async Task<WorkspaceOpenResult> OpenMapAsync(
        WorkspaceOpenRequest request,
        string root,
        string fullPath,
        CancellationToken cancellationToken)
    {
        if (mapService is null)
        {
            return WorkspaceOpenResult.Failed(request.View, "ICodeProjectMapService is not registered.");
        }

        var outcome = await mapService
            .BuildAsync(fullPath, root, request.Depth, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Success)
        {
            return WorkspaceOpenResult.Failed(request.View, outcome.Error ?? "(map 失败但没有原因)");
        }

        var payload = WorkspaceOpenMapping.FromProjectMap(outcome);
        var (truncated, reason) = DescribeMapTruncation(payload.Root);

        return WorkspaceOpenResult.Ok(
            request.View,
            outcome.RootPath ?? fullPath,
            payload,
            truncated,
            reason);
    }

    /// <summary>
    /// 扫描整棵目录树，判断是否发生过「静默截断」。
    /// <para>
    /// map 视图有两处独立上限：每个目录最多 <c>MaxFilesPerDir</c> 个文件、每个文件最多
    /// <c>MaxTopLevelTypes</c> 个类型。两处都保留了真实总数（<c>TotalFileCount</c> /
    /// <c>TotalTypeCount</c>），但<b>顶层 truncated 若不跟着置真</b>，只读这个标志的调用方
    /// 仍会得出「这就是全部」的结论 —— 那正是 ADR-089 §8.3 禁止的 hollow negation。
    /// </para>
    /// </summary>
    private static (bool Truncated, string? Reason) DescribeMapTruncation(MapEntry root)
    {
        var truncatedDirs = 0;
        var truncatedFiles = 0;
        Visit(root);

        if (truncatedDirs == 0 && truncatedFiles == 0)
        {
            return (false, null);
        }

        var parts = new List<string>();
        if (truncatedDirs > 0)
        {
            parts.Add($"{truncatedDirs} 个目录的文件清单被截断");
        }

        if (truncatedFiles > 0)
        {
            parts.Add($"{truncatedFiles} 个文件的类型清单被截断");
        }

        return (true, string.Join("；", parts) + "。真实总数见各节点 total_file_count / total_type_count。");

        void Visit(MapEntry node)
        {
            if (node.Files.Count < node.TotalFileCount)
            {
                truncatedDirs++;
            }

            foreach (var file in node.Files)
            {
                if (file.Types.Count < file.TotalTypeCount)
                {
                    truncatedFiles++;
                }
            }

            foreach (var child in node.Children)
            {
                Visit(child);
            }
        }
    }

    private async Task<WorkspaceOpenResult> OpenOutlineAsync(
        WorkspaceOpenRequest request,
        string fullPath,
        CancellationToken cancellationToken)
    {
        if (outlineService is null)
        {
            return WorkspaceOpenResult.Failed(request.View, "ICodeOutlineService is not registered.");
        }

        var outcome = await outlineService
            .OutlineAsync(fullPath, request.Path!, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Success)
        {
            return WorkspaceOpenResult.Failed(request.View, outcome.Error ?? "(outline 失败但没有原因)");
        }

        var payload = outcome.Origin == CodeOutlineOrigin.CSharp
            ? WorkspaceOpenMapping.FromCSharpOutline(outcome)
            : WorkspaceOpenMapping.FromRegistryOutline(outcome);

        return WorkspaceOpenResult.Ok(
            request.View,
            fullPath,
            payload,
            truncated: payload.Truncated,
            truncatedReason: payload.Truncated
                ? $"上游 outline 只给出前 {payload.Nodes.Count} 个顶层节点（共 {payload.TotalCount} 个）。"
                : null);
    }

    private static async Task<WorkspaceOpenResult> OpenExcerptAsync(
        WorkspaceOpenRequest request,
        string fullPath,
        CancellationToken cancellationToken)
    {
        var all = await File.ReadAllLinesAsync(fullPath, cancellationToken).ConfigureAwait(false);

        var start = Math.Max(1, request.StartLine ?? 1);
        var take = Math.Max(1, request.LineCount ?? DefaultExcerptLines);

        // start_line 超出文件末尾时，空切片是**假成功**：调用方手里多半是编辑前的旧行号，
        // 它会把「0 行」读成「这一段本来就没有东西」。明确指出越界并提示重新定位
        // （ADR-089 §8.3：不完全/定位失效必须可见）。空文件不算越界（确实没东西可给）。
        if (all.Length > 0 && start > all.Length)
        {
            return WorkspaceOpenResult.Failed(
                request.View,
                $"(start_line={start} 超出文件总行数 {all.Length}: {request.Path}；文件可能已变短，请用 line_count/start_line 重新定位)");
        }

        var slice = all.Skip(start - 1).Take(take).ToList();

        // MaxBytes 是**返回内容总字节**上限：超出就丢尾行，并说明丢了多少。
        var kept = new List<string>();
        var bytes = 0;
        var firstDroppedBytes = 0;
        foreach (var line in slice)
        {
            var size = Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes + size > request.MaxBytes)
            {
                // 首行也必须受预算约束。早期的 `&& kept.Count > 0` 豁免会让 MaxBytes 被突破，
                // 而切片只有一行时 truncated 仍为 false —— **硬上限被破坏且看不见**，
                // 比报错更难发现。
                firstDroppedBytes = size;
                break;
            }

            kept.Add(line);
            bytes += size;
        }

        var truncated = kept.Count < slice.Count;
        string? reason = null;
        if (truncated)
        {
            reason = $"达到 MaxBytes={request.MaxBytes} 上限，本段只返回 {kept.Count}/{slice.Count} 行。";
            if (kept.Count == 0)
            {
                reason += $"连第一行都放不下（该行 {firstDroppedBytes} 字节），请调大 max_bytes。";
            }
        }

        return WorkspaceOpenResult.Ok(
            request.View,
            fullPath,
            new ExcerptPayload(kept, start, all.Length),
            truncated: truncated,
            truncatedReason: reason);
    }

    private static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 工作区根本身是合法的打开目标（例如 view=map 直接看根目录）。
        // 原先只比前缀，导致 "." 解析成根后被判为“不在工作区内” —— 测试先抓到了这个。
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }
}
