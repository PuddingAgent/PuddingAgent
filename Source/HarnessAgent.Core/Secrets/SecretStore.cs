using System.Text;
using System.Text.RegularExpressions;

namespace HarnessAgent.Core.Secrets;

/// <summary>
/// 密钥保管箱：把散落在各个配置文件里的密钥收进一个受保护目录。
///
/// <para>今天的状态是「密钥到处都有」——邮件授权码、LLM apiKey、Slack token 各存一份，
/// 格式还各不相同（邮件那份一度被存成 hex 文本 + UTF-16，读出来是 32 个乱码字符）。
/// 每一处都是一次独立的出错机会。这个类提供一个统一入口。</para>
///
/// <para>四条硬约束：</para>
/// <list type="number">
/// <item><b>落盘永不含明文</b>：写入前必经 <see cref="ISecretProtector"/>；
/// 加密失败就抛异常，绝不退回明文。</item>
/// <item><b>键名必须安全</b>：键会变成文件名，所以只允许 <c>[a-z0-9._-]</c>，
/// 且拒绝以点开头与任何分隔符——否则 <c>../</c> 就能写到目录外。</item>
/// <item><b>原子写入</b>：先写临时文件再原子替换，避免崩溃留下半个密钥。</item>
/// <item><b>损坏不等于崩溃</b>：读取时密文坏了、魔数不对、解密失败，
/// 一律返回"没找到 + 原因"，让调用方去走重新配置的流程。</item>
/// </list>
/// </summary>
public sealed partial class SecretStore
{
    /// <summary>文件魔数，用来把「这是我们的密钥文件」与「误放进来的任何文件」区分开。</summary>
    private static readonly byte[] Magic = "PDG1"u8.ToArray();

    private readonly string _directory;
    private readonly ISecretProtector _protector;

    public SecretStore(string directory, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(protector);
        _directory = Path.GetFullPath(directory);
        _protector = protector;
    }

    /// <summary>保管目录（绝对路径）。</summary>
    public string Directory => _directory;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex KeyPattern { get; }

    /// <summary>校验键名。合法则返回规范化键，否则返回拒绝原因。</summary>
    public static SecretKeyDecision ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return SecretKeyDecision.Deny("secret key is required");

        var normalized = key.Trim().ToLowerInvariant();

        // 分隔符与点开头是路径穿越的入口，先按字符集挡掉，再做形状校验。
        if (!KeyPattern.IsMatch(normalized))
            return SecretKeyDecision.Deny(
                "secret key may only contain a-z, 0-9, dot, underscore and hyphen, and must start with a letter or digit");

        if (normalized.Contains(".."))
            return SecretKeyDecision.Deny("secret key must not contain '..'");

        return SecretKeyDecision.Allow(normalized);
    }

    /// <summary>写入（或覆盖）一个密钥。加密失败会抛异常，不会落明文。</summary>
    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var validated = ValidateKey(key);
        if (!validated.Allowed)
            throw new ArgumentException(validated.Reason, nameof(key));

        var plaintext = Encoding.UTF8.GetBytes(value);
        byte[] protectedPayload;
        try
        {
            protectedPayload = _protector.Protect(plaintext);
        }
        finally
        {
            // 明文缓冲用完即清，减少它在下一次 GC 前停留在堆里的时间。
            Array.Clear(plaintext);
        }

        if (protectedPayload.Length == 0)
            throw new InvalidOperationException("secret protector returned an empty payload");

        var envelope = new byte[Magic.Length + protectedPayload.Length];
        Magic.CopyTo(envelope, 0);
        protectedPayload.CopyTo(envelope, Magic.Length);

        System.IO.Directory.CreateDirectory(_directory);
        var path = PathOf(validated.NormalizedKey);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temp, envelope, ct);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>读取一个密钥。<see cref="SecretReadResult.Value"/> 仅在成功时有值。</summary>
    public async Task<SecretReadResult> TryGetAsync(string key, CancellationToken ct = default)
    {
        var validated = ValidateKey(key);
        if (!validated.Allowed)
            return SecretReadResult.Deny(validated.Reason);

        var path = PathOf(validated.NormalizedKey);
        if (!File.Exists(path))
            return SecretReadResult.Deny("secret not found");

        byte[] envelope;
        try
        {
            envelope = await File.ReadAllBytesAsync(path, ct);
        }
        catch (IOException ex)
        {
            return SecretReadResult.Deny("secret could not be read: " + ex.Message);
        }

        if (envelope.Length <= Magic.Length
            || !envelope.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            return SecretReadResult.Deny("secret file is not in the expected format");
        }

        try
        {
            var plaintext = _protector.Unprotect(envelope.AsSpan(Magic.Length));
            return SecretReadResult.Allow(Encoding.UTF8.GetString(plaintext));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 换机器、换用户、密文被改都会走到这里。对外只说"解不开"，不泄漏内部细节。
            return SecretReadResult.Deny("secret could not be decrypted: " + ex.GetType().Name);
        }
    }

    /// <summary>删除一个密钥。不存在时返回 false。</summary>
    public bool Remove(string key)
    {
        var validated = ValidateKey(key);
        if (!validated.Allowed)
            return false;

        var path = PathOf(validated.NormalizedKey);
        if (!File.Exists(path))
            return false;

        File.Delete(path);
        return true;
    }

    /// <summary>列出已保管的键（不含临时文件）。</summary>
    public IReadOnlyList<string> ListKeys()
    {
        if (!System.IO.Directory.Exists(_directory))
            return [];

        var keys = new List<string>();
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.secret"))
        {
            var name = Path.GetFileName(file);
            if (name.Contains(".tmp-", StringComparison.Ordinal))
                continue;
            keys.Add(name[..^".secret".Length]);
        }

        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    private string PathOf(string normalizedKey) =>
        Path.Combine(_directory, normalizedKey + ".secret");
}

/// <summary>键名校验结果。</summary>
public readonly record struct SecretKeyDecision(
    bool Allowed,
    string? NormalizedKey,
    string? Reason)
{
    public static SecretKeyDecision Allow(string key) => new(true, key, null);

    public static SecretKeyDecision Deny(string reason) => new(false, null, reason);
}

/// <summary>读取结果。<see cref="Value"/> 只在 <see cref="Found"/> 为 true 时有值。</summary>
public readonly record struct SecretReadResult(bool Found, string? Value, string? Reason)
{
    public static SecretReadResult Allow(string value) => new(true, value, null);

    public static SecretReadResult Deny(string reason) => new(false, null, reason);
}
