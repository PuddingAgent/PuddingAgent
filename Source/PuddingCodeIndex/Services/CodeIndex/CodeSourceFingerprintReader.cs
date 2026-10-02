using System.Security.Cryptography;
using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>一次稳定读的结果。</summary>
/// <param name="Fingerprint">指纹（stat + 内容 hash），仅在稳定时非空。</param>
/// <param name="Stable">
/// 读取期间该文件是否稳定：stat（长度/修改时间）在读前读后一致，且 hash 来自同一次读取的内容。
/// 不稳定时调用方必须<b>弃用本轮结果并留待重试</b>，绝不能用旧内容配上新 stat 提交。
/// </param>
/// <param name="Reason">不稳定时的原因（诊断 + 退避依据）。</param>
public sealed record CodeSourceReadResult(
    SourceFingerprint? Fingerprint,
    bool Stable,
    string? Reason);

/// <summary>
/// **源文件稳定读**（D2/D4，2026-10-02）：读取文件内容求 hash，并要求读前读后的 stat 一致。
/// <para>
/// 为什么必须这样：指纹里的 hash 必须是**真正参与提取的那份内容**的 hash。如果先 stat、再读、
/// 中途文件被改写，就会把「新 stat + 旧内容的 hash」写进 manifest，于是这份文件永远不再被核验。
/// 所以规则是「读前 stat → 读内容 → 读后 stat → 两者一致才认」，否则返回不稳定。
/// </para>
/// <para>
/// 读不到（不存在/无权限/共享冲突）不算不稳定，而是返回带原因的失败，由调用方按「本轮不处理该路径」处理。
/// </para>
/// </summary>
public class CodeSourceFingerprintReader
{
    /// <summary>默认的单文件读取上限（超出即拒绝，避免把超大文件读进内存）。</summary>
    public const long DefaultMaxFileBytes = 64L * 1024 * 1024;

    private readonly long _maxFileBytes;

    /// <summary>创建读取器。</summary>
    /// <param name="maxFileBytes">单文件读取上限（字节）。</param>
    public CodeSourceFingerprintReader(long maxFileBytes = DefaultMaxFileBytes)
    {
        if (maxFileBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes), maxFileBytes, "Limit must be positive.");

        _maxFileBytes = maxFileBytes;
    }

    /// <summary>
    /// 稳定读取一个文件并算出指纹。
    /// <para>
    /// <c>virtual</c> 是为了让调用方（协调器）的「不稳定/读失败 ⇒ 弃用本轮结果」分支可以被确定性地验证：
    /// 真实的读写竞争无法在测试里可靠复现，而这个分支恰恰是最不能出错的地方。
    /// </para>
    /// </summary>
    /// <param name="filePath">绝对路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public virtual async Task<CodeSourceReadResult> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        FileInfo before;

        try
        {
            before = new FileInfo(filePath);
            if (!before.Exists)
                return new CodeSourceReadResult(null, false, $"file disappeared: {filePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new CodeSourceReadResult(null, false, $"file could not be inspected: {ex.Message}");
        }

        if (before.Length > _maxFileBytes)
        {
            return new CodeSourceReadResult(
                null, false, $"file exceeds the fingerprint read limit ({before.Length} > {_maxFileBytes} bytes)");
        }

        byte[] content;

        try
        {
            content = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CodeSourceReadResult(null, false, $"file could not be read: {ex.Message}");
        }

        FileInfo after;
        try
        {
            after = new FileInfo(filePath);
            if (!after.Exists)
                return new CodeSourceReadResult(null, false, $"file disappeared while being read: {filePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new CodeSourceReadResult(null, false, $"file could not be re-inspected: {ex.Message}");
        }

        if (after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc)
        {
            return new CodeSourceReadResult(
                null, false, "file changed while it was being read (stat differs before/after)");
        }

        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        // 内容长度与 stat 不一致同样说明读的时候文件在变：宁可下一轮重来。
        if (content.LongLength != after.Length)
            return new CodeSourceReadResult(null, false, "content length does not match the observed file length");

        return new CodeSourceReadResult(
            new SourceFingerprint(
                new DateTimeOffset(after.LastWriteTimeUtc, TimeSpan.Zero),
                after.Length,
                hash),
            true,
            null);
    }
}
