using PuddingCodeIndex.Contracts;
using PuddingRetrieval.Contracts;

namespace PuddingCodeIndex.Services.Retrieval;

/// <summary>一个待签发 ref 的候选：只含索引已经知道的定位信息，**不含 hash**。</summary>
/// <param name="FilePath">命中文件路径。可以是项目内相对路径，也可以是绝对路径；签发时会换算成相对路径。</param>
/// <param name="SymbolId">符号标识。</param>
/// <param name="StartLine">1 基起始行。</param>
/// <param name="EndLine">1 基结束行。</param>
public sealed record SymbolRefIssueRequest(
    string FilePath,
    string SymbolId,
    int StartLine,
    int EndLine);

/// <summary>签发结果：<see cref="Token"/> 与 <see cref="SkipReason"/> 恰有一个非空。</summary>
/// <param name="Token">可用的不透明 token；未签发时为 null。</param>
/// <param name="SkipReason">未签发的原因（面向调用方的诚实说明）；签发成功时为 null。</param>
public sealed record SymbolRefIssue(string? Token, string? SkipReason)
{
    /// <summary>是否真的签发了。</summary>
    public bool Issued => Token is not null;

    /// <summary>构造一次「拒绝签发」。</summary>
    public static SymbolRefIssue Skip(string reason) => new(null, reason);
}

/// <summary>
/// 把搜索命中批量签发成 <see cref="SymbolRefV1"/> token（§8.3 的签发端；读端见
/// <c>SymbolRefOpenSource</c> / <c>SymbolRefFreshness</c>）。
/// <para>
/// <b>为什么单独存在</b>：读端已经能判 FRESH/STALE，但没有任何地方签发 ref ⇒ 整条通道是死的。
/// 签发必须与读端用同一套口径，否则签出来的 ref 会被自己判成 Stale（表现为「永远打不开」，
/// 而日志里不会有任何错误）。
/// </para>
/// <para>
/// <b>hash 只取自 manifest（索引时），绝不读磁盘</b>：§8.3 要求 ref 绑定索引时的源版本。
/// 拿当前磁盘 hash 去签发，就会把「索引落后于磁盘」的窗口签成一条看起来 Fresh、
/// 实际按旧行号打开新代码的 ref —— 那是最坏的一类错误。
/// </para>
/// <para>
/// <b>ref 只接受项目内相对路径，绝对命中必须先换算</b>：<see cref="SymbolRefV1.Create"/> 会拒绝含
/// <c>:</c> 的路径（盘符 / 备用数据流）。而索引命中给的是绝对路径，所以签发器必须用项目根把它换算成
/// 相对路径；<b>换算不出来就不签发</b>，不能把绝对路径硬塞进去（那会抛异常），也不能猜一个「大概对」的相对路径。
/// </para>
/// <para>
/// <b>manifest 建索引一次，批量复用</b>：一次搜索可能有几十条命中，
/// <see cref="ICodeSourceMaintenanceStore.LoadSourceMaintenanceAsync"/> 是 O(文件数)，
/// 逐条调用会把搜索拖成秒级。这里在构造时建一次路径→hash 映射，之后每条命中是 O(1)。
/// </para>
/// <para>
/// <b>不签发优于签发错的</b>：任何一项证据缺失（manifest 无记录、指纹为 null、行不完整、
/// 参数非法、token 超长、绝对路径且无项目根）都返回 <see cref="SymbolRefIssue.Skip"/>，
/// 而不是抛异常打断整个搜索，也不是签一条注定被判 Stale 的 ref。
/// </para>
/// </summary>
public sealed class SymbolRefIssuer
{
    private readonly Dictionary<string, string> _contentHashByPath;
    private readonly string? _projectRoot;

    private SymbolRefIssuer(Dictionary<string, string> contentHashByPath, string? projectRoot)
    {
        _contentHashByPath = contentHashByPath;
        _projectRoot = projectRoot;
    }

    /// <summary>
    /// 从 manifest 快照建立签发器。
    /// </summary>
    /// <param name="manifest">持久 manifest（键为规范化路径，<b>落库形态是绝对路径</b>）。</param>
    /// <param name="projectRoot">
    /// 项目根目录。给出时会额外建立「绝对路径 → 项目内相对路径」的别名键，
    /// 因此绝对命中才能被换算并签发。为 null 或空时**只**保留原样键 —— 不猜。
    /// </param>
    public static SymbolRefIssuer FromManifest(
        IReadOnlyDictionary<string, CodeSourceEntry> manifest,
        string? projectRoot)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = string.IsNullOrWhiteSpace(projectRoot) ? null : projectRoot;

        foreach (var (path, entry) in manifest)
        {
            // 指纹为 null ⇒ 首次没有基线，不能当作已提交的源版本。
            var hash = entry.Fingerprint?.ContentHash;
            if (string.IsNullOrWhiteSpace(hash))
            {
                continue;
            }

            // 不完整的行（只写了 manifest、索引未提交）不得当作「已应用」。
            if (!entry.Complete)
            {
                continue;
            }

            var normalized = SymbolIdentity.NormalizePath(path);
            if (normalized.Length == 0)
            {
                continue;
            }

            map[normalized] = hash;

            if (root is null || !Path.IsPathRooted(normalized))
            {
                continue;
            }

            if (TryMakeRelative(root, normalized, out var relative))
            {
                // 相对键是签发时真正要用的键；绝对键保留是为了兼容「manifest 本来就是相对形态」的输入。
                map.TryAdd(relative, hash);
            }
        }

        return new SymbolRefIssuer(map, root);
    }

    /// <summary>manifest 里可用于签发的路径条数（诊断用，不作为正确性判据）。</summary>
    public int KnownPathCount => _contentHashByPath.Count;

    /// <summary>
    /// 为一条命中签发 ref。
    /// </summary>
    /// <param name="projectId">项目标识（作为 ref 的 scope 绑定）。</param>
    /// <param name="providerGeneration">产出该符号的解析/索引代际标识。</param>
    /// <param name="request">命中定位信息。</param>
    public SymbolRefIssue Issue(
        string projectId,
        string providerGeneration,
        SymbolRefIssueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalized = SymbolIdentity.NormalizePath(request.FilePath);
        if (normalized.Length == 0)
        {
            return SymbolRefIssue.Skip("命中缺少 file_path，无法定位。");
        }

        string relative;
        if (Path.IsPathRooted(normalized))
        {
            if (_projectRoot is null)
            {
                return SymbolRefIssue.Skip(
                    "命中给的是绝对路径，但签发器没有项目根：无法换算成项目内相对路径，ref 只接受相对路径。");
            }

            if (!TryMakeRelative(_projectRoot, normalized, out relative))
            {
                return SymbolRefIssue.Skip("命中路径不在项目根目录内，不能作为该项目的 ref 签发。");
            }
        }
        else
        {
            relative = normalized;
        }

        // 相对键优先（签发要用它），绝对键兜底（manifest 只有绝对形态且项目根未知的极小情形）。
        if (!_contentHashByPath.TryGetValue(relative, out var contentHash)
            && !_contentHashByPath.TryGetValue(normalized, out contentHash))
        {
            return SymbolRefIssue.Skip(
                "manifest 里没有该文件的已提交内容指纹：无法证明 ref 与索引时的源版本一致。");
        }

        try
        {
            var reference = SymbolRefV1.Create(
                projectId,
                relative,
                request.SymbolId,
                request.StartLine,
                request.EndLine,
                contentHash,
                providerGeneration);

            return new SymbolRefIssue(reference.ToToken(), null);
        }
        catch (ArgumentException ex)
        {
            // 与解析端共用同一套不变量：非法参数在这里被拒绝，而不是抛给搜索调用方。
            return SymbolRefIssue.Skip(ex.Message);
        }
    }

    /// <summary>
    /// 把绝对路径换算成项目内相对路径。越界（<c>..</c>）或换算结果仍是绝对路径时返回 false —— 不猜。
    /// </summary>
    private static bool TryMakeRelative(string projectRoot, string absolutePath, out string relative)
    {
        relative = string.Empty;

        var root = projectRoot.TrimEnd('/', '\\');
        if (root.Length == 0 || !Path.IsPathRooted(absolutePath))
        {
            return false;
        }

        var rel = Path.GetRelativePath(root, absolutePath);
        if (Path.IsPathRooted(rel) || rel.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = SymbolIdentity.NormalizePath(rel);
        if (normalized.Length == 0)
        {
            return false;
        }

        relative = normalized;
        return true;
    }
}
