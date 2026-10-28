using PuddingCode.Runtime;
using PuddingCodeIndex.Services.Retrieval;
using PuddingFullTextIndex.Contracts;

namespace PuddingRuntime.Services;

/// <summary>
/// 检索 scope → 全文索引目录。
/// <para>
/// <b>为什么单独抽这一层</b>：检索端口的 scope 语义由调用方定义（v1 等同 code index 的
/// workspaceId），而全文索引认的是<b>目录路径</b>。把两者的对应关系挡在接口后面，
/// 端口与全文引擎就都不必知道对方的存在 —— 依赖方向仍由编译期强制。
/// </para>
/// </summary>
public interface IRetrievalScopeDirectories
{
    /// <summary>把检索 scope 解析成全文索引目录列表；空列表 = 该 scope 映射不到任何目录。</summary>
    IReadOnlyList<string> Resolve(string scopeId);
}

/// <summary>默认实现：scope 即工作区 id，映射到该工作区根目录。</summary>
public sealed class WorkspaceRootScopeDirectories(IExecutionEnvironmentProvider environment)
    : IRetrievalScopeDirectories
{
    private readonly IExecutionEnvironmentProvider _environment =
        environment ?? throw new ArgumentNullException(nameof(environment));

    /// <inheritdoc />
    public IReadOnlyList<string> Resolve(string scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            return [];

        var root = _environment.GetWorkspaceRoot(scopeId);
        return string.IsNullOrWhiteSpace(root) ? [] : [root];
    }
}

/// <summary>
/// 把 <see cref="IFullTextSearchEngine"/> 适配成检索端口的文本数据面。
/// <para>
/// <b>失败语义（本类存在的理由）</b>：文本面返回空列表<b>只能</b>表示"查了，没匹配"。
/// 因此"scope 映射不到目录"与"没有已建立的索引"都抛
/// <see cref="RetrievalTextSourceUnavailableException"/>，由端口转成 NotIndexed；
/// 引擎自身报错同样抛出，不吞。<b>任何情况下都不把"没法查"伪装成"没有"。</b>
/// </para>
/// <para>
/// 多个目录时按顺序检索并累计到 <c>maxMatches</c> 为止；每次都把剩余配额传给引擎，
/// 避免为凑数而多取。
/// </para>
/// </summary>
public sealed class FullTextRetrievalTextSource(
    IFullTextSearchEngine engine,
    IRetrievalScopeDirectories directories) : IRetrievalTextSource
{
    private readonly IFullTextSearchEngine _engine =
        engine ?? throw new ArgumentNullException(nameof(engine));

    private readonly IRetrievalScopeDirectories _directories =
        directories ?? throw new ArgumentNullException(nameof(directories));

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievalTextMatch>> SearchAsync(
        string scopeId,
        string query,
        int maxMatches,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (maxMatches <= 0)
            return [];

        var roots = _directories.Resolve(scopeId);
        if (roots.Count == 0)
        {
            throw new RetrievalTextSourceUnavailableException(
                $"scope '{scopeId}' 无法映射到任何索引目录");
        }

        var indexedRoots = roots.Where(_engine.HasIndex).ToList();
        if (indexedRoots.Count == 0)
        {
            throw new RetrievalTextSourceUnavailableException(
                $"scope '{scopeId}' 下没有已建立的全文索引（候选目录 {roots.Count} 个）");
        }

        var collected = new List<RetrievalTextMatch>(maxMatches);

        foreach (var root in indexedRoots)
        {
            if (collected.Count >= maxMatches)
                break;

            var remaining = maxMatches - collected.Count;
            var result = await _engine
                .SearchAsync(query, root, remaining, fileExtensionFilter: null, subDirectoryFilter: null, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                // 引擎报错 ≠ 没有匹配：上抛让端口/调用方看到"查不了"。
                throw new RetrievalTextSourceUnavailableException(
                    $"全文检索失败（{root}）：{result.Error}", 
                    innerException: null);
            }

            foreach (var match in result.Matches)
            {
                if (collected.Count >= maxMatches)
                    break;

                collected.Add(new RetrievalTextMatch(match.FilePath, match.LineNumber, match.LineText));
            }
        }

        return collected;
    }
}
