using System.Security;

namespace HarnessAgent.Core.Connectors.Feishu;

/// <summary>
/// 文件投递的取材策略。
///
/// <para>
/// 2026-11-16 修正：最初我把它设计成「必须落在当前工作区内」，那是把安全与可用混为一谈——
/// 用户要发的 PDF 就在 <c>D:\Data\output</c>，本来就在工作区之外。
/// 真正的风险边界不是「哪个目录」，而是两件事：
/// ① 收件人被渠道白名单钉死（只能发给自己已授权的飞书用户）；
/// ② 不能把机器上的凭据类文件当普通文件发出去。
/// </para>
///
/// <para>
/// 所以这里做三件事：允许根可配置（工作区 + 额外根，如 Data\output、Downloads），
/// 目录/不存在/越界一律拒绝，凭据类路径按段与扩展名拦下。
/// 根比较必须按分隔符边界判断，否则 <c>D:\Data\output2</c> 会被 <c>D:\Data\output</c> 误放过。
/// </para>
/// </summary>
public sealed class FeishuFileSourcePolicy
{
    /// <summary>允许根（已规范化为不带尾分隔符的完整路径）。</summary>
    public IReadOnlyList<string> AllowedRoots { get; }

    /// <summary>绝不允许随消息发出的目录段（小写比较）。</summary>
    public static IReadOnlyList<string> DeniedDirectorySegments { get; } =
        [".ssh", ".aws", ".gnupg", ".azure", ".kube", "user data", "login data"];

    /// <summary>绝不允许发出的扩展名（小写比较）。</summary>
    public static IReadOnlyList<string> DeniedExtensions { get; } =
        [".pem", ".pfx", ".p12", ".key", ".kdbx", ".ppk"];

    /// <summary>绝不允许发出的文件名（小写比较，含无扩展名的凭据文件）。</summary>
    public static IReadOnlyList<string> DeniedFileNames { get; } =
        ["id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "credentials", ".env", "cookies"];

    public FeishuFileSourcePolicy(IEnumerable<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        var roots = new List<string>();
        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var full = NormalizeRoot(root);
            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase))
                roots.Add(full);
        }

        AllowedRoots = roots;
    }

    /// <summary>判定一个候选路径能否作为投递素材。返回完整路径或拒绝原因，不抛异常。</summary>
    public FeishuFileSourceDecision Evaluate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return FeishuFileSourceDecision.Deny("path is required");

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return FeishuFileSourceDecision.Deny("path is not a valid file system path");
        }

        if (!File.Exists(full))
        {
            return Directory.Exists(full)
                ? FeishuFileSourceDecision.Deny("path is a directory, not a file")
                : FeishuFileSourceDecision.Deny("file not found");
        }

        if (!IsUnderAnyRoot(full))
            return FeishuFileSourceDecision.Deny("path is outside the allowed roots");

        if (HitsCredentialGuard(full))
            return FeishuFileSourceDecision.Deny("path looks like a credential file and will not be sent");

        return FeishuFileSourceDecision.Allow(full);
    }

    private bool IsUnderAnyRoot(string fullPath)
    {
        foreach (var root in AllowedRoots)
        {
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                return true;

            // 边界必须带分隔符：D:\Data\output2 不属于 D:\Data\output。
            var withSeparator = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool HitsCredentialGuard(string fullPath)
    {
        var name = Path.GetFileName(fullPath).ToLowerInvariant();
        if (DeniedFileNames.Contains(name))
            return true;

        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (DeniedExtensions.Contains(extension))
            return true;

        foreach (var segment in fullPath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (DeniedDirectorySegments.Contains(segment.ToLowerInvariant()))
                return true;
        }

        return false;
    }

    private static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root.Trim());
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // 盘根（D:\）被 TrimEnd 掉分隔符后会变成 "D:"，那不再是「D 盘根」的语义。
        return trimmed.Length == 2 && trimmed[1] == ':'
            ? trimmed + Path.DirectorySeparatorChar
            : trimmed;
    }
}

/// <summary>取材判定结果。<see cref="FullPath"/> 仅在允许时有值。</summary>
public readonly record struct FeishuFileSourceDecision(bool Allowed, string? FullPath, string? Reason)
{
    public static FeishuFileSourceDecision Allow(string fullPath) => new(true, fullPath, null);

    public static FeishuFileSourceDecision Deny(string reason) => new(false, null, reason);
}
