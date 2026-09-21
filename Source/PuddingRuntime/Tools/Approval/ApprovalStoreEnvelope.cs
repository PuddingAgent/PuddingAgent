using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 审批存储「整文件签章」信封（P0 A 步）。
///
/// 设计依据（现场实测，非推定）：
/// <list type="bullet">
/// <item>四个持久化面（tickets.json / allowlist.json / audit-events.jsonl / tool-authorizations.json）
/// 当前**无签章、无序号、无 hash 链**；</item>
/// <item>写入模型是**整文件重写**（<c>SaveUnlockedAsync</c> ⇒ <c>File.WriteAllTextAsync(全量数组)</c>）
/// ⇒ 因此完整性方案用**整文件签章**，而不是逐条序号；</item>
/// <item><c>audit-events.jsonl</c> 是追加式，另行走前向 hash 链，**不使用本信封**。</item>
/// </list>
///
/// ⚠️ 诚实边界（必须与实现一起被理解）：本信封提供的能力是
/// 「检测**不经本机 shell 通道**的篡改、让篡改**事后可证**、检测**损坏**（截断/半写）」。
/// 它**不是**发现①/③（本机 shell 可读写同一目录）的主防线 —— 那条防线是 C2 的路径校验 / 硬边界前移。
/// 因此**不得**把「已加签章」表述为「发现①/③ 已闭环」。
///
/// 本类是**纯逻辑**：不做 IO、不持有 DI、不读配置。密钥由调用方（KeyVault）注入。
/// </summary>
public static class ApprovalStoreEnvelope
{
    /// <summary>当前信封格式版本。根为数组 = 旧格式（无信封）。</summary>
    public const int SchemaVersion = 2;

    public const string Algorithm = "HMAC-SHA256";

    /// <summary>子密钥派生的域分隔前缀（防跨文件重放）。</summary>
    private const string KeyDomainPrefix = "tool-approval/v1/";

    private static readonly JsonSerializerOptions DefaultOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 按**文件名**派生签章子密钥：<c>HMAC(masterKey, "tool-approval/v1/" + fileName)</c>。
    /// ⇒ 拿 A 文件的 mac 去冒充 B 文件不可行（跨文件重放无效）。
    /// </summary>
    public static byte[] DeriveStoreKey(byte[] masterKey, string fileName)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        if (masterKey.Length == 0)
            throw new ArgumentException("masterKey 不得为空。", nameof(masterKey));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("fileName 不得为空白。", nameof(fileName));

        return HMACSHA256.HashData(masterKey, Encoding.UTF8.GetBytes(KeyDomainPrefix + fileName));
    }

    /// <summary>把载荷包装为带签章的信封 JSON 文本。</summary>
    public static string Wrap<T>(
        T payload,
        long sequence,
        string keyId,
        DateTimeOffset signedAtUtc,
        byte[] storeKey,
        JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(storeKey);
        if (storeKey.Length == 0)
            throw new ArgumentException("storeKey 不得为空。", nameof(storeKey));
        if (string.IsNullOrWhiteSpace(keyId))
            throw new ArgumentException("keyId 不得为空白（无 keyId 则无法轮换密钥）。", nameof(keyId));
        if (sequence <= 0)
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "sequence 必须为正整数。");

        var effective = options ?? DefaultOptions;
        var payloadJson = JsonSerializer.Serialize(payload, effective);
        var mac = ComputeMac(payloadJson, sequence, signedAtUtc, keyId, storeKey);

        var envelope = new Envelope<T>
        {
            SchemaVersion = SchemaVersion,
            Sequence = sequence,
            SignedAtUtc = signedAtUtc,
            Algorithm = Algorithm,
            KeyId = keyId,
            Payload = payload,
            Mac = mac,
        };

        return JsonSerializer.Serialize(envelope, effective);
    }

    /// <summary>
    /// 解包并校验。**不抛异常**：一切失败都以 <see cref="ApprovalStoreIntegrityOutcome"/> 返回，
    /// 由调用方决定 fail-closed 动作（例如拒绝加载并写审计），避免"异常当控制流"。
    /// </summary>
    public static ApprovalStoreUnwrapResult<T> Unwrap<T>(
        string? json,
        byte[] storeKey,
        JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(storeKey);
        var effective = options ?? DefaultOptions;

        if (string.IsNullOrWhiteSpace(json))
            return ApprovalStoreUnwrapResult<T>.Failure(ApprovalStoreIntegrityOutcome.Malformed);

        // 旧格式：根即数组（无信封）⇒ 交由调用方走"首次签章（adopt）"路径，并留审计。
        if (LooksLikeLegacyRootArray(json))
        {
            try
            {
                var legacy = JsonSerializer.Deserialize<T>(json, effective);
                return legacy is null
                    ? ApprovalStoreUnwrapResult<T>.Failure(ApprovalStoreIntegrityOutcome.Malformed)
                    : ApprovalStoreUnwrapResult<T>.Legacy(legacy);
            }
            catch (JsonException)
            {
                return ApprovalStoreUnwrapResult<T>.Failure(ApprovalStoreIntegrityOutcome.Malformed);
            }
        }

        Envelope<T>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope<T>>(json, effective);
        }
        catch (JsonException)
        {
            return ApprovalStoreUnwrapResult<T>.Failure(ApprovalStoreIntegrityOutcome.Malformed);
        }

        if (envelope is null)
            return ApprovalStoreUnwrapResult<T>.Failure(ApprovalStoreIntegrityOutcome.Malformed);

        if (envelope.SchemaVersion != SchemaVersion)
            return ApprovalStoreUnwrapResult<T>.Failure(ApprovalStoreIntegrityOutcome.UnknownSchemaVersion);

        if (string.IsNullOrWhiteSpace(envelope.Mac))
            return ApprovalStoreUnwrapResult<T>.Failure(ApprovalStoreIntegrityOutcome.MissingMac);

        if (!string.Equals(envelope.Algorithm, Algorithm, StringComparison.Ordinal))
            return ApprovalStoreUnwrapResult<T>.Failure(ApprovalStoreIntegrityOutcome.UnknownAlgorithm);

        var payloadJson = JsonSerializer.Serialize(envelope.Payload, effective);
        var expected = ComputeMac(payloadJson, envelope.Sequence, envelope.SignedAtUtc, envelope.KeyId, storeKey);

        // 固定时间比较，避免逐字节比较带来的时序侧信道。
        var actualBytes = Encoding.UTF8.GetBytes(envelope.Mac);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var macOk = actualBytes.Length == expectedBytes.Length
                    && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);

        if (!macOk)
            return ApprovalStoreUnwrapResult<T>.Failure(
                ApprovalStoreIntegrityOutcome.MacMismatch,
                envelope.Sequence,
                envelope.KeyId);

        return ApprovalStoreUnwrapResult<T>.Success(
            envelope.Payload,
            envelope.Sequence,
            envelope.SignedAtUtc,
            envelope.KeyId);
    }

    /// <summary>
    /// 序号单调性判定（防"把旧文件整体搬回来"）。
    /// ⚠️ 其**前提**是调用方持久化了"已见最大序号"（锚点文件）；无锚点则本判据无从生效。
    /// </summary>
    public static bool IsSequenceAcceptable(long incoming, long lastSeen) => incoming > lastSeen;

    private static bool LooksLikeLegacyRootArray(string json)
    {
        foreach (var ch in json)
        {
            if (char.IsWhiteSpace(ch))
                continue;
            return ch == '[';
        }

        return false;
    }

    private static string ComputeMac(
        string payloadJson,
        long sequence,
        DateTimeOffset signedAtUtc,
        string keyId,
        byte[] storeKey)
    {
        // 载荷放在最后，字段均为定格式，避免分隔符歧义。
        var canonical = string.Join(
            '\n',
            SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            signedAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Algorithm,
            keyId,
            payloadJson);

        return Convert.ToBase64String(HMACSHA256.HashData(storeKey, Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record Envelope<T>
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("seq")]
        public long Sequence { get; init; }

        [JsonPropertyName("signedAtUtc")]
        public DateTimeOffset SignedAtUtc { get; init; }

        [JsonPropertyName("alg")]
        public string Algorithm { get; init; } = string.Empty;

        [JsonPropertyName("keyId")]
        public string KeyId { get; init; } = string.Empty;

        [JsonPropertyName("payload")]
        public T Payload { get; init; } = default!;

        [JsonPropertyName("mac")]
        public string Mac { get; init; } = string.Empty;
    }
}

/// <summary>校验结论。一切失败都以此为返回，调用方据其决定 fail-closed 动作。</summary>
public enum ApprovalStoreIntegrityOutcome
{
    /// <summary>签章有效。</summary>
    Ok = 0,

    /// <summary>旧格式（根数组、无信封）—— 需走"首次签章 + 审计"路径，**不得**静默当作空。</summary>
    LegacyUnsigned = 1,

    /// <summary>信封版本不认识（向后/向前不兼容）。</summary>
    UnknownSchemaVersion = 2,

    /// <summary>算法不认识。</summary>
    UnknownAlgorithm = 3,

    /// <summary>缺 mac 字段。</summary>
    MissingMac = 4,

    /// <summary>mac 不匹配 —— 内容被改、密钥不对，或跨文件重放。</summary>
    MacMismatch = 5,

    /// <summary>JSON 无法解析（含损坏/截断）。</summary>
    Malformed = 6,
}

/// <summary>解包结果。</summary>
public sealed record ApprovalStoreUnwrapResult<T>
{
    public required ApprovalStoreIntegrityOutcome Outcome { get; init; }

    /// <summary>仅在 <see cref="ApprovalStoreIntegrityOutcome.Ok"/> 与
    /// <see cref="ApprovalStoreIntegrityOutcome.LegacyUnsigned"/> 时非空。</summary>
    public T? Payload { get; init; }

    public long Sequence { get; init; }

    public DateTimeOffset SignedAtUtc { get; init; }

    public string? KeyId { get; init; }

    public bool IsOk => Outcome == ApprovalStoreIntegrityOutcome.Ok;

    public bool IsLegacy => Outcome == ApprovalStoreIntegrityOutcome.LegacyUnsigned;

    internal static ApprovalStoreUnwrapResult<T> Success(
        T payload,
        long sequence,
        DateTimeOffset signedAtUtc,
        string keyId) => new()
    {
        Outcome = ApprovalStoreIntegrityOutcome.Ok,
        Payload = payload,
        Sequence = sequence,
        SignedAtUtc = signedAtUtc,
        KeyId = keyId,
    };

    internal static ApprovalStoreUnwrapResult<T> Legacy(T payload) => new()
    {
        Outcome = ApprovalStoreIntegrityOutcome.LegacyUnsigned,
        Payload = payload,
    };

    internal static ApprovalStoreUnwrapResult<T> Failure(
        ApprovalStoreIntegrityOutcome outcome,
        long sequence = 0,
        string? keyId = null) => new()
    {
        Outcome = outcome,
        Sequence = sequence,
        KeyId = keyId,
    };
}
