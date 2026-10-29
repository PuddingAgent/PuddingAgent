using System.Text;
using PuddingCodeIndex.Contracts;
using PuddingCodeIntelligence.Contracts;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// <see cref="IWorkspaceOpenService"/> 的 v1 实现：只做 <c>excerpt</c>（文件切片）与 <c>outline</c>（结构预览）。
/// <para>
/// <b>边界校验在本层</b>：契约只表达意图（<c>Path</c>），策略在这里 —— 路径必须落在
/// 该 workspace 的 scope 根子树内。这比 <c>code_outline</c> 严，因为 open 属于检索面。
/// </para>
/// </summary>
public sealed class WorkspaceOpenService(
    ICodeOutlineService? outlineService,
    ICodeIndexScopeResolver? resolver,
    ICodeProjectMapService? mapService = null) : IWorkspaceOpenService
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

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            // v1 只支持按路径打开；symbol_ref 需要身份/版本/授权体系，不在本版。
            return WorkspaceOpenResult.Failed(request.View, "path is required（v1 只支持按路径打开）。");
        }

        var resolution = await resolver
            .ResolveAsync(request.WorkspaceId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (resolution is null)
        {
            return WorkspaceOpenResult.Failed(request.View, $"(未解析到工作区 scope: {request.WorkspaceId})");
        }

        var root = resolution.Scope.RootPath;
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
