using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.Retrieval;

namespace PuddingRuntime.Services.Tools;

/// <summary>签发结果：token 表 + 全局说明。</summary>
/// <param name="Tokens">键为（项目, 符号）→ 可用的 ref token；不含未签发的条目。</param>
/// <param name="Note">全局不可签发的诚实说明（如 manifest 读取失败）；正常时为 null。</param>
public sealed record SymbolRefIssuanceResult(
    IReadOnlyDictionary<(string ProjectId, string SymbolId), string> Tokens,
    string? Note)
{
    /// <summary>空结果（不签发任何 ref）。</summary>
    public static SymbolRefIssuanceResult Empty { get; } =
        new(new Dictionary<(string, string), string>(), null);

    /// <summary>本轮签发了几条。</summary>
    public int IssuedCount => Tokens.Count;
}

/// <summary>
/// 把搜索命中接入 <see cref="SymbolRefIssuer"/> 的**宿主侧桥接**：
/// 负责按项目装载 manifest、取项目根、把异常收敛成「不签发 + 说明」。
/// <para>
/// <b>为什么单独一层</b>：这些是「与外部存储打交道的胶水」，把胶水放进工具会让工具无法在
/// 无存储的环境里测；放这里之后，签发决策可以用一个假 manifest 直接验证。
/// </para>
/// <para>
/// <b>每次搜索每个项目只装载一次 manifest</b>：实现是 O(文件数)，逐条命中调用会把搜索拖成秒级。
/// </para>
/// <para>
/// <b>失败一律降级，不打断搜索</b>：manifest 读不到时搜到的符号仍然是有用的，
/// 只是拿不到可跨请求引用的 ref。此时给出 <see cref="SymbolRefIssuanceResult.Note"/>，
/// 而不是静默返回空表 —— 静默会让调用方以为「这个符号本来就没有 ref」。
/// </para>
/// </summary>
public sealed class SymbolRefIssuanceService(ICodeSourceMaintenanceStore? maintenanceStore)
{
    /// <summary>
    /// ref 的 provider generation 占位值。
    /// <para>
    /// <b>刻意不绑程序集版本</b>：版本每次部署都会变，而索引并不会随之重建，
    /// 绑版本会把全部历史 ref 一次性判成 Stale —— 那不是「保守」，是让整条通道定期失效。
    /// 真正的取值（索引器代际 + 解析器名）待 §8.3 裁定，改这里一行即可。
    /// </para>
    /// </summary>
    public const string ProviderGeneration = "codeindex-v1";

    /// <summary>
    /// 为一批命中签发 ref。
    /// </summary>
    /// <param name="workspaceId">工作空间。</param>
    /// <param name="hits">**可信**命中（已被判定为陈旧的不应传进来）。</param>
    /// <param name="projects">已登记项目（用来取项目根；缺项目根时绝对路径无法换算成相对路径）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<SymbolRefIssuanceResult> IssueAsync(
        string workspaceId,
        IReadOnlyList<CodeSymbolDetail> hits,
        IReadOnlyList<CodeProjectRecord> projects,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(projects);

        if (maintenanceStore is null || hits.Count == 0)
        {
            return SymbolRefIssuanceResult.Empty;
        }

        var tokens = new Dictionary<(string ProjectId, string SymbolId), string>();

        foreach (var group in hits.GroupBy(hit => hit.Symbol.ProjectId, StringComparer.Ordinal))
        {
            var projectId = group.Key;
            if (string.IsNullOrWhiteSpace(projectId))
            {
                continue;
            }

            CodeSourceMaintenanceSnapshot snapshot;
            try
            {
                snapshot = await maintenanceStore
                    .LoadSourceMaintenanceAsync(workspaceId, projectId, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 只报类型名：不把存储路径、SQL 或凭据带进工具输出。
                return new SymbolRefIssuanceResult(
                    tokens,
                    $"manifest 读取失败（{ex.GetType().Name}）：本轮不签发 ref，搜索结果本身仍然有效。");
            }

            var projectRoot = projects
                .FirstOrDefault(p => string.Equals(p.ProjectId, projectId, StringComparison.Ordinal))
                ?.ProjectPath;

            var issuer = SymbolRefIssuer.FromManifest(snapshot.Manifest, projectRoot);

            foreach (var hit in group)
            {
                var issue = issuer.Issue(
                    projectId,
                    ProviderGeneration,
                    new SymbolRefIssueRequest(
                        hit.Symbol.FilePath,
                        hit.Symbol.SymbolId,
                        hit.Symbol.StartLine,
                        hit.Symbol.EndLine));

                if (issue.Issued)
                {
                    tokens[(projectId, hit.Symbol.SymbolId)] = issue.Token!;
                }
            }
        }

        return new SymbolRefIssuanceResult(tokens, null);
    }
}
