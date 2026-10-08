using System.Security.Cryptography;
using System.Text;

namespace Pudding.Contracts.Desktop;

/// <summary>
/// 定位/读取分页游标（设计方案 §6.2：locate 提供 <c>cursor</c>）。
///
/// 三条硬性语义：
/// ①**对模型不透明**：游标只由宿主生成与解释，模型只能原样回传，不能拼装或修改；
/// ②**绑定三件事**：页面版本、查询指纹（同一组定位条件）、已消费的偏移量。
///    动态页面变化或换了查询条件都让游标**过期**，而不是拿旧游标去套新内容（§6.2「cursor_expired」）；
/// ③**过期与非法要分开**：格式不合法 ⇒ 参数错误；版本/指纹不符 ⇒ 过期（需要重新读取）。
///
/// 线格式：<c>c1-{pageVersion}-{fingerprint8}-{offset}</c>。前缀 <c>c1</c> 是方案版本，
/// 便于将来换编码而不必猜旧格式。
/// </summary>
public readonly record struct BrowserLocateCursor
{
    private const string Prefix = "c1";

    private BrowserLocateCursor(long pageVersion, string fingerprint, int offset)
    {
        PageVersion = pageVersion;
        Fingerprint = fingerprint;
        Offset = offset;
    }

    /// <summary>游标签发时的页面版本。</summary>
    public long PageVersion { get; }

    /// <summary>查询指纹（定位条件 + 影响匹配集合的开关）。</summary>
    public string Fingerprint { get; }

    /// <summary>已经返回过的匹配数（下一次从它开始）。</summary>
    public int Offset { get; }

    public static BrowserLocateCursor Create(long pageVersion, string fingerprint, int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        return new BrowserLocateCursor(pageVersion, fingerprint, offset);
    }

    /// <summary>编码成对模型不透明的线格式。</summary>
    public string Encode() => $"{Prefix}-{PageVersion}-{Fingerprint}-{Offset}";

    /// <summary>严格解析：任何偏差都返回 <c>false</c>（不猜测、不修补）。</summary>
    public static bool TryParse(string? wire, out BrowserLocateCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(wire))
        {
            return false;
        }

        var parts = wire.Trim().Split('-');
        if (parts.Length != 4
            || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)
            || !long.TryParse(parts[1], out var pageVersion)
            || pageVersion <= 0
            || !int.TryParse(parts[3], out var offset)
            || offset < 0)
        {
            return false;
        }

        var fingerprint = parts[2];
        if (fingerprint.Length != FingerprintLength || !IsHex(fingerprint))
        {
            return false;
        }

        cursor = new BrowserLocateCursor(pageVersion, fingerprint, offset);
        return true;
    }

    /// <summary>游标是否仍然适用于「这个版本 + 这组查询条件」。</summary>
    public bool Matches(long pageVersion, string fingerprint) =>
        PageVersion == pageVersion
        && string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal);

    /// <summary>
    /// 解析并校验调用方给的游标，给出**分页偏移**；失败时给出**分类好的**错误：
    /// 过期（版本/查询变了 ⇒ <see cref="DesktopCapabilityErrorCode.CursorExpired"/>，唯一正确动作是重新读取）
    /// 与参数错（格式不合法 ⇒ <see cref="DesktopCapabilityErrorCode.InvalidRequest"/>）必须分开。
    ///
    /// 两条传输**共用这一个判定**：分页语义各写一份迟早会漂移（本系列已吃过这个亏）。
    /// </summary>
    public static bool TryResolve(
        string? wire,
        long pageVersion,
        string fingerprint,
        out int offset,
        out DesktopCapabilityError failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        offset = 0;
        failure = null!;

        if (string.IsNullOrWhiteSpace(wire))
        {
            return true;
        }

        if (!TryParse(wire, out var cursor))
        {
            failure = new DesktopCapabilityError(
                DesktopCapabilityErrorCode.InvalidRequest,
                "the locate cursor is malformed; omit it to start from the beginning");
            return false;
        }

        if (!cursor.Matches(pageVersion, fingerprint))
        {
            failure = new DesktopCapabilityError(
                DesktopCapabilityErrorCode.CursorExpired,
                $"the locate cursor was issued for v{cursor.PageVersion} / a different locator, "
                + $"but the page is at v{pageVersion}",
                retryable: false);
            return false;
        }

        offset = cursor.Offset;
        return true;
    }

    /// <summary>
    /// 查询指纹：**只看影响匹配集合的输入**（定位条件与 includeHidden/inViewportOnly）。
    /// 故意不含 limit/cursor 自身——分页不该改变"这是同一组结果"这一事实。
    /// </summary>
    public static string FingerprintOf(DesktopLocator locator, bool includeHidden, bool inViewportOnly)
    {
        ArgumentNullException.ThrowIfNull(locator);

        var canonical = string.Join(
            '\u001f',
            DesktopLocatorKindWire.NameOf(locator.Kind),
            locator.Value ?? string.Empty,
            locator.Name ?? string.Empty,
            locator.Exact ? "1" : "0",
            locator.Nth?.ToString() ?? string.Empty,
            locator.HasText ?? string.Empty,
            includeHidden ? "1" : "0",
            inViewportOnly ? "1" : "0");

        return ShortHash(canonical);
    }

    /// <summary>定位结果的查询指纹（便捷重载）。</summary>
    public static string FingerprintOf(DesktopLocator locator) =>
        FingerprintOf(locator, includeHidden: false, inViewportOnly: false);

    private const int FingerprintLength = 8;

    private static string ShortHash(string value)
    {
        // SHA-256 截断：跨进程/跨版本稳定（不能用 string.GetHashCode——它按进程加盐）。
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest)[..FingerprintLength].ToLowerInvariant();
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() => $"cursor v{PageVersion} @{Offset} ({Fingerprint})";
}
