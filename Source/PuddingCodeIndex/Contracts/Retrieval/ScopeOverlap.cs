namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 已注册范围的可判定描述（ADR-089 §2.4 的检测输入）。
/// </summary>
public sealed record RetrievalScopeDescriptor
{
    /// <summary>构造描述（scope 标识与根目录都不得为空）。</summary>
    public RetrievalScopeDescriptor(string scopeId, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("scope 标识不得为空。", nameof(scopeId));

        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("scope 根目录不得为空。", nameof(rootPath));

        ScopeId = scopeId.Trim();
        RootPath = rootPath.Trim();
    }

    /// <summary>scope 标识（即 project_id / scope_id）。</summary>
    public string ScopeId { get; }

    /// <summary>scope 根目录（原样保留）。</summary>
    public string RootPath { get; }

    /// <summary>归一化后的根目录（<c>/</c> 分隔、小写）。</summary>
    public string NormalizedRootPath => SymbolIdentity.NormalizePath(RootPath);
}

/// <summary>
/// 重叠 / 嵌套 scope 的警告（ADR-089 §2.4 / §8.10）：
/// "root 与 subdir 同时注册时，显式提示'重叠索引会放大结果'"。
/// </summary>
public sealed record ScopeOverlapWarning
{
    internal ScopeOverlapWarning(
        RetrievalScopeDescriptor outer,
        RetrievalScopeDescriptor inner,
        bool sameRoot,
        string message)
    {
        Outer = outer;
        Inner = inner;
        SameRoot = sameRoot;
        Message = message;
    }

    /// <summary>外层（包含方）。</summary>
    public RetrievalScopeDescriptor Outer { get; }

    /// <summary>内层（被包含方）。</summary>
    public RetrievalScopeDescriptor Inner { get; }

    /// <summary>是否两根目录**相同**（重复注册同一根）。</summary>
    public bool SameRoot { get; }

    /// <summary>人类可读说明。</summary>
    public string Message { get; }
}

/// <summary>
/// 嵌套 / 重叠 scope 检测（ADR-089 §2.4 / §8.10 的**合同**部分）。
/// <para>
/// 实测事实：<c>code_index_list_projects</c> 返回 4 个互相嵌套的项目
/// （仓库根 + PuddingCore / PuddingPlatform / PuddingRuntime）⇒ 同一文件被索引多次。
/// 这类噪音**加任何过滤条件都消不掉**，必须靠"能被检测 + 能被警告"来治理。
/// </para>
/// <para>
/// 索引侧的治理实现（Registering 超时收敛等）属 U3 收尾项，<b>不在本刀</b>；
/// 本刀只冻结"检测/警告"这一可机械验证的合同。
/// </para>
/// </summary>
public static class ScopeOverlapDetector
{
    /// <summary>检测重叠/嵌套：返回按 (外层 id, 内层 id) 确定性排序的警告。</summary>
    public static IReadOnlyList<ScopeOverlapWarning> Detect(IEnumerable<RetrievalScopeDescriptor> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var ordered = scopes
            .OrderBy(scope => scope.ScopeId, StringComparer.Ordinal)
            .ThenBy(scope => scope.NormalizedRootPath, StringComparer.Ordinal)
            .ToList();

        var warnings = new List<ScopeOverlapWarning>();
        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                var first = ordered[i];
                var second = ordered[j];
                var firstRoot = first.NormalizedRootPath;
                var secondRoot = second.NormalizedRootPath;

                if (string.Equals(firstRoot, secondRoot, StringComparison.Ordinal))
                {
                    warnings.Add(Create(first, second, sameRoot: true));
                    continue;
                }

                if (IsStrictlyInside(secondRoot, firstRoot))
                    warnings.Add(Create(first, second, sameRoot: false));
                else if (IsStrictlyInside(firstRoot, secondRoot))
                    warnings.Add(Create(second, first, sameRoot: false));
            }
        }

        return [.. warnings
            .OrderBy(warning => warning.Outer.ScopeId, StringComparer.Ordinal)
            .ThenBy(warning => warning.Inner.ScopeId, StringComparer.Ordinal)];
    }

    private static ScopeOverlapWarning Create(RetrievalScopeDescriptor outer, RetrievalScopeDescriptor inner, bool sameRoot)
    {
        var message = sameRoot
            ? $"scope '{outer.ScopeId}' 与 '{inner.ScopeId}' 注册了同一个根目录（{outer.RootPath}）："
              + "同一文件会被索引多次，结果会结构性放大（ADR-089 §2.4）。"
            : $"scope '{inner.ScopeId}'（{inner.RootPath}）嵌套在 '{outer.ScopeId}'（{outer.RootPath}）内："
              + "同一文件会被索引多次，结果会结构性放大，过载判定前必须先按符号身份去重（ADR-089 §2.4 / §8.10）。";

        return new ScopeOverlapWarning(outer, inner, sameRoot, message);
    }

    private static bool IsStrictlyInside(string inner, string outer) =>
        inner.StartsWith(outer + "/", StringComparison.Ordinal);
}
