namespace PuddingBrowser.Automation;

/// <summary>
/// **引用的宿主绑定身份**（设计方案 §1.1/§5：禁止自动重绑；ref 必须能证明"还是同一个节点"）。
///
/// 现有的 ref（<c>p{pageToken}-v{version}-n{index}</c>）只绑定**页面 token + 文档版本 + 序号**。
/// 这在 SPA 里有一个真实缺口：页面原地重渲染、版本计数**没动**时，同一个位置的序号会指向
/// **另一个节点** —— 校验会通过，动作却打在别的元素上（错位读取）。
///
/// 这里补上宿主侧身份（CDP <c>backendNodeId</c>，随引用一起编码）与**判定表**：
/// <list type="bullet">
///   <item><c>Valid</c>：页面、版本、序号与宿主身份都一致；</item>
///   <item><c>Replaced</c>：页面/版本/序号一致，但宿主身份不同 ⇒ **节点被换掉了**，必须拒绝；</item>
///   <item><c>StaleVersion</c>/<c>WrongPage</c>：沿用既有"引用过期"语义；</item>
///   <item><c>Unverifiable</c>：任一侧没有身份（旧引用、或驱动拿不到身份）⇒
///     **不声称有效、也不凭空判死**，如实报"无法证明"。</item>
/// </list>
///
/// 判定是纯函数：驱动只负责采集"现在这个引用是什么"，判定不散落在各处。
/// </summary>
public static class BrowserNodeIdentity
{
    /// <summary>身份在 ref 里的前缀（`p…-v3-n7-b42`）。</summary>
    public const char BackendNodeIdPrefix = 'b';

    /// <summary>一条引用的身份事实（页面 token + 版本 + 序号 + 可选的宿主节点身份）。</summary>
    public sealed record Identity(string PageToken, long Version, int NodeIndex, long? BackendNodeId)
    {
        /// <summary>宿主身份是否已知（未知 ⇒ 永远判 <see cref="Verdict.Unverifiable"/>）。</summary>
        public bool HasHostIdentity => BackendNodeId is > 0;

        /// <summary>编码成 ref 字符串（未提供宿主身份时不追加后缀）。</summary>
        public string Encode() =>
            BackendNodeId is > 0
                ? $"p{PageToken}-v{Version}-n{NodeIndex}-{BackendNodeIdPrefix}{BackendNodeId}"
                : $"p{PageToken}-v{Version}-n{NodeIndex}";
    }

    /// <summary>判定结论。**不是布尔**：把"证明相同"与"无法证明"分开，才不会把未知当有效。</summary>
    public enum Verdict
    {
        /// <summary>页面、版本、序号与宿主身份一致。</summary>
        Valid,

        /// <summary>页面与版本一致、宿主身份不同：同一位置已经是**另一个节点**。</summary>
        Replaced,

        /// <summary>文档版本不同（页面已重观察过）。</summary>
        StaleVersion,

        /// <summary>页面 token 不同（引用了别的页面/文档世代）。</summary>
        WrongPage,

        /// <summary>任一侧缺宿主身份：**无法证明**是否同一节点（旧引用属于此类）。</summary>
        Unverifiable,
    }

    /// <summary>
    /// 把"记录下来的身份"与"现在观察到的身份"对照。
    /// 判定顺序是刻意的：先排除**明确的错误**（页面/版本/节点被换），最后才落到"无法证明"。
    /// </summary>
    public static Verdict Judge(Identity recorded, Identity observed)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(observed);

        if (!string.Equals(recorded.PageToken, observed.PageToken, StringComparison.OrdinalIgnoreCase))
        {
            return Verdict.WrongPage;
        }

        if (recorded.Version != observed.Version)
        {
            return Verdict.StaleVersion;
        }

        if (recorded.NodeIndex != observed.NodeIndex)
        {
            // 同一页同一版却对不上序号：说明序号本身不可信（定位结果变了）⇒ 与"节点被换"同义处理。
            return Verdict.Replaced;
        }

        if (!recorded.HasHostIdentity || !observed.HasHostIdentity)
        {
            return Verdict.Unverifiable;
        }

        return recorded.BackendNodeId == observed.BackendNodeId ? Verdict.Valid : Verdict.Replaced;
    }

    /// <summary>
    /// 是否**允许继续**（不因引用问题拒绝）。
    /// `Unverifiable` 允许继续 —— 但调用方必须把它当作"未证明"，而不是"已证明相同"。
    /// </summary>
    public static bool IsSafeToProceed(Verdict verdict) =>
        verdict is Verdict.Valid or Verdict.Unverifiable;

    /// <summary>判定 → 驱动错误码（可继续时为 <c>null</c>）。</summary>
    public static string? ErrorCodeOf(Verdict verdict) => verdict switch
    {
        Verdict.Valid or Verdict.Unverifiable => null,
        Verdict.Replaced or Verdict.StaleVersion or Verdict.WrongPage => "stale_element_reference",
        _ => "stale_element_reference",
    };

    /// <summary>判定 → 给调用方的提示（点明"重新观察"，**不**提示自动重绑）。</summary>
    public static string? Describe(Verdict verdict) => verdict switch
    {
        Verdict.Valid => null,
        Verdict.Unverifiable =>
            "browser ref has no host-bound node identity on one side, so it is neither confirmed nor rejected;"
            + " re-observe the page if the action must be certain",
        Verdict.Replaced =>
            "the node at this ref was replaced after it was observed;"
            + " re-observe the page and locate a fresh ref (refs are never re-bound to another node)",
        Verdict.StaleVersion =>
            "browser ref belongs to an older document version;"
            + " re-observe the page and locate a fresh ref",
        Verdict.WrongPage =>
            "browser ref belongs to another page or document;"
            + " re-observe the page and locate a fresh ref",
        _ => "browser ref is not usable; re-observe the page and locate a fresh ref",
    };

    /// <summary>
    /// 解析 ref 文本。**宽松**：只有形如 <c>p…-v…-n…</c> 的前缀被识别；
    /// 后缀（宿主身份）缺失时如实返回 <c>null</c>（不猜、不补 0）。
    /// </summary>
    public static bool TryParse(string? refValue, out Identity identity)
    {
        identity = null!;
        if (string.IsNullOrWhiteSpace(refValue))
        {
            return false;
        }

        // 复用驱动侧同一份格式：p{token}-v{version}-n{index}(-b{id})?
        var parts = refValue.Trim().Split('-');
        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        if (parts[0].Length < 2 || parts[0][0] != 'p'
            || !parts[1].StartsWith('v') || !parts[2].StartsWith('n'))
        {
            return false;
        }

        if (!long.TryParse(parts[1].AsSpan(1), out var version)
            || !int.TryParse(parts[2].AsSpan(1), out var index))
        {
            return false;
        }

        long? backendNodeId = null;
        if (parts.Length == 4)
        {
            if (parts[3].Length < 2 || parts[3][0] != BackendNodeIdPrefix
                || !long.TryParse(parts[3].AsSpan(1), out var parsed)
                || parsed <= 0)
            {
                return false;
            }

            backendNodeId = parsed;
        }

        identity = new Identity(parts[0][1..], version, index, backendNodeId);
        return true;
    }

    /// <summary>把宿主身份**追加**到一条已存在的 ref 上（已有后缀则替换）。</summary>
    public static string? WithHostIdentity(string? refValue, long? backendNodeId)
    {
        if (!TryParse(refValue, out var identity) || backendNodeId is not > 0)
        {
            return refValue;
        }

        return (identity with { BackendNodeId = backendNodeId }).Encode();
    }
}
