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
    ICodeIndexScopeResolver? resolver) : IWorkspaceOpenService
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

        var slice = all.Skip(start - 1).Take(take).ToList();

        // MaxBytes 是**返回内容总字节**上限：超出就丢尾行，并说明丢了多少。
        var kept = new List<string>();
        var bytes = 0;
        foreach (var line in slice)
        {
            var size = Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes + size > request.MaxBytes && kept.Count > 0)
            {
                break;
            }

            kept.Add(line);
            bytes += size;
        }

        var truncated = kept.Count < slice.Count;
        return WorkspaceOpenResult.Ok(
            request.View,
            fullPath,
            new ExcerptPayload(kept, start, all.Length),
            truncated: truncated,
            truncatedReason: truncated
                ? $"达到 MaxBytes={request.MaxBytes} 上限，本段只返回 {kept.Count}/{slice.Count} 行。"
                : null);
    }

    private static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
