using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuddingCode.Services;

/// <summary>
/// Reasoning 紧凑归档编解码器（P1-3 T1）。
/// 将 thinking 数据从旧格式（JSON 数组，表示开销约 13.6x）压缩为新格式
/// （SchemaVersion=2，目标 ≤5x）：完整 text 只存一份，chunks 降级为
/// （UTF-8 字节偏移 o + 相对 timestamp delta t）紧凑索引。
/// 纯静态工具类：无依赖注入、无状态、纯函数。
/// </summary>
public static class ReasoningCompactCodec
{
    /// <summary>新格式 schema 版本号。</summary>
    public const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        // 不转义非 ASCII（中文原样输出），保证紧凑体积；仅用于 JSON 数据存储场景。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// 将完整 reasoning 文本与 chunk 列表编码为紧凑 JSON（v2）。
    /// </summary>
    /// <param name="text">完整 reasoning 原文，UTF-8 字节为偏移基准。</param>
    /// <param name="chunks">thinking chunk 列表，其 Text 按顺序 join 必须等于 <paramref name="text"/>（codec 做防御性校验）。</param>
    /// <returns>紧凑 JSON 字符串。</returns>
    /// <exception cref="ArgumentNullException">text 或 chunks 为 null。</exception>
    /// <exception cref="ArgumentException">chunks 文本与 text 不一致，或存在空 chunk（会破坏 round-trip）。</exception>
    public static string Encode(string text, IReadOnlyList<ThinkingChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(chunks);

        var joined = string.Concat(chunks.Select(c => c.Text));
        if (!string.Equals(joined, text, StringComparison.Ordinal))
            throw new ArgumentException("text 必须等于 chunks 的 Text 按顺序 join 的结果。", nameof(text));
        if (chunks.Any(c => c.Text.Length == 0))
            throw new ArgumentException("chunk 文本不能为空：空 chunk 产生重复偏移，无法 round-trip 还原。", nameof(chunks));

        var offsets = new List<int>(chunks.Count);
        var deltas = new List<long>(chunks.Count);
        var cursor = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            offsets.Add(cursor);
            cursor += Encoding.UTF8.GetByteCount(chunks[i].Text);
            // 首条 t 为绝对基准值，后续为相对上一条的 delta(ms)。
            deltas.Add(i == 0 ? chunks[i].Timestamp : chunks[i].Timestamp - chunks[i - 1].Timestamp);
        }

        var payload = new CompactPayload
        {
            V = SchemaVersion,
            Text = text,
            Hash = ComputeSha256Hex(text),
        };
        for (var i = 0; i < chunks.Count; i++)
            payload.Chunks.Add(new CompactChunk { O = offsets[i], T = deltas[i] });

        return JsonSerializer.Serialize(payload, WriteOptions);
    }

    /// <summary>
    /// 解析 reasoning 数据，兼容新旧两种格式。
    /// </summary>
    /// <param name="json">新格式（v2 对象）或旧格式（[{text,timestamp}] 数组）JSON。</param>
    /// <returns>
    /// 解析成功返回 <see cref="DecodedThinking"/>；非法 JSON、结构不完整、乱序偏移、
    /// 或偏移切在 UTF-8 多字节字符中间时返回 null（不抛未处理异常）。
    /// hash 不匹配不返回 null（fail-open），仅将 <see cref="DecodedThinking.HashValid"/> 置为 false。
    /// </returns>
    public static DecodedThinking? Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 旧格式：顶层数组 [{"text":...,"timestamp":...}, ...]
            if (root.ValueKind == JsonValueKind.Array)
                return DecodeLegacy(root);

            // 新格式：顶层对象且 v == SchemaVersion
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("v", out var vEl) &&
                vEl.ValueKind == JsonValueKind.Number &&
                vEl.GetInt32() == SchemaVersion)
                return DecodeCompact(root);

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static DecodedThinking? DecodeLegacy(JsonElement root)
    {
        var chunks = new List<ThinkingChunk>();
        var sb = new StringBuilder();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("text", out var textEl) ||
                textEl.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("timestamp", out var tsEl) ||
                !TryReadTimestamp(tsEl, out var timestamp))
                return null;

            var text = textEl.GetString() ?? string.Empty;
            chunks.Add(new ThinkingChunk(text, timestamp));
            sb.Append(text);
        }

        // 旧格式：Text = 各 chunk 文本顺序 join；无 hash，恒视为有效。
        return new DecodedThinking(sb.ToString(), chunks, IsCompactFormat: false, Hash: null, HashValid: true);
    }

    private static DecodedThinking? DecodeCompact(JsonElement root)
    {
        if (!root.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
            return null;
        var text = textEl.GetString() ?? string.Empty;

        if (!root.TryGetProperty("chunks", out var chunksEl) || chunksEl.ValueKind != JsonValueKind.Array)
            return null;

        var offsets = new List<int>();
        var deltas = new List<long>();
        foreach (var item in chunksEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("o", out var oEl) ||
                oEl.ValueKind != JsonValueKind.Number ||
                !item.TryGetProperty("t", out var tEl) ||
                tEl.ValueKind != JsonValueKind.Number)
                return null;

            var o = oEl.GetInt64();
            if (o < 0 || o > int.MaxValue)
                return null;
            offsets.Add((int)o);
            deltas.Add(tEl.GetInt64());
        }

        // 乱序/重复偏移（非严格升序）→ 结构无效。
        for (var i = 1; i < offsets.Count; i++)
        {
            if (offsets[i] <= offsets[i - 1])
                return null;
        }

        var utf8 = Encoding.UTF8.GetBytes(text);
        if (offsets.Count > 0 && offsets[^1] > utf8.Length)
            return null;

        var expectedHash = ComputeSha256Hex(text);
        string? providedHash = null;
        if (root.TryGetProperty("hash", out var hashEl) && hashEl.ValueKind == JsonValueKind.String)
            providedHash = hashEl.GetString();
        // 新格式标准要求携带 hash：缺失或与 text 不符均视为校验失败（不抛异常，fail-open）。
        var hashValid = providedHash is not null &&
                        string.Equals(providedHash, expectedHash, StringComparison.OrdinalIgnoreCase);

        // 按 UTF-8 字节偏移切分 chunk 文本；边界落在多字节字符中间 → 结构无效。
        var chunks = new List<ThinkingChunk>(offsets.Count);
        var prevTimestamp = 0L;
        for (var i = 0; i < offsets.Count; i++)
        {
            var start = offsets[i];
            var end = i + 1 < offsets.Count ? offsets[i + 1] : utf8.Length;
            var chunkText = DecodeUtf8Slice(utf8, start, end);
            if (chunkText is null)
                return null;

            // 还原绝对 timestamp：首条 t 即基准值，后续为累加 delta。
            var timestamp = i == 0 ? deltas[0] : prevTimestamp + deltas[i];
            chunks.Add(new ThinkingChunk(chunkText, timestamp));
            prevTimestamp = timestamp;
        }

        return new DecodedThinking(text, chunks, IsCompactFormat: true, providedHash, hashValid);
    }

    private static bool TryReadTimestamp(JsonElement el, out long timestamp)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                try
                {
                    timestamp = el.GetInt64();
                    return true;
                }
                catch (FormatException)
                {
                    timestamp = 0;
                    return false;
                }
            case JsonValueKind.String:
                return long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out timestamp);
            default:
                timestamp = 0;
                return false;
        }
    }

    private static string? DecodeUtf8Slice(byte[] utf8, int start, int end)
    {
        try
        {
            return StrictUtf8.GetString(utf8, start, end - start);
        }
        catch (DecoderFallbackException)
        {
            // 偏移切在 UTF-8 多字节序列中间。
            return null;
        }
    }

    private static string ComputeSha256Hex(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>thinking chunk：text 为 chunk 文本，timestamp 为毫秒时间戳。</summary>
    public sealed record ThinkingChunk(string Text, long Timestamp);

    /// <summary>解码结果：新格式还原完整 text 与 chunk 列表；旧格式 Text = chunks join。</summary>
    public sealed record DecodedThinking(
        string Text,
        IReadOnlyList<ThinkingChunk> Chunks,
        bool IsCompactFormat,
        string? Hash,
        bool HashValid);

    private sealed class CompactPayload
    {
        [JsonPropertyName("v")]
        public int V { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("chunks")]
        public List<CompactChunk> Chunks { get; set; } = [];

        [JsonPropertyName("hash")]
        public string Hash { get; set; } = string.Empty;
    }

    private sealed class CompactChunk
    {
        [JsonPropertyName("o")]
        public int O { get; set; }

        [JsonPropertyName("t")]
        public long T { get; set; }
    }
}
