using PuddingFullTextIndex.Contracts;

namespace PuddingFullTextIndex.Infrastructure.Supply;

/// <summary>
/// <see cref="IFullTextIndexBuilder"/> 的薄适配器：把 scope 转发给既有的
/// <see cref="IFullTextSearchEngine.BuildIndexAsync"/>（<c>filePatterns = null</c> ⇒ 引擎默认扩展名白名单），
/// 度量沿用引擎自报的 <c>FileCount / TotalBytes / ElapsedMs / Error</c>。
/// <para>
/// ⚠️ <b>staging / 预算硬限 / 成功后原子切换属 A2，本适配器不做</b>（也<b>不应</b>在此做）：
/// 本切片只负责「谁来写、写什么 scope、什么时候写」，不改变引擎的写入方式。
/// </para>
/// </summary>
public sealed class FullTextSearchEngineIndexBuilder : IFullTextIndexBuilder
{
    private readonly IFullTextSearchEngine _engine;

    public FullTextSearchEngineIndexBuilder(IFullTextSearchEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    public async Task<SupplyBuildResult> BuildAsync(SupplyScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var result = await _engine.BuildIndexAsync(scope.RootPath, filePatterns: null, ct).ConfigureAwait(false);
        return new SupplyBuildResult(
            result.Success,
            result.IndexedFileCount,
            result.TotalBytes,
            result.ElapsedMs,
            result.Error);
    }
}
