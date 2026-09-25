using System.Globalization;
using Newtonsoft.Json;

namespace PuddingFullTextIndex.Infrastructure.Maintenance;

/// <summary>
/// 读取 checkpoint 文本的结论（fail-closed：除 <see cref="Ok"/> 之外**一律**按「陈旧 / 需补偿」处理，
/// 绝不按「无需维护」处理）。
/// </summary>
public enum MaintenanceCheckpointReadStatus
{
    /// <summary>可读且可用。</summary>
    Ok = 0,

    /// <summary>没有内容 / 文件不存在（首次建立维护状态，需保守起点或全范围校准）。</summary>
    Missing = 1,

    /// <summary>JSON 损坏、必填字段缺失、字段类型不符。</summary>
    Malformed = 2,

    /// <summary>版本号不是本实现支持的版本（不得按「没问题」继续）。</summary>
    UnsupportedVersion = 3,
}

/// <summary>
/// 状态目录里一个文件的归类（用于「崩溃残留的临时文件不得被当作已提交 checkpoint」）。
/// </summary>
public enum MaintenanceCheckpointFileKind
{
    /// <summary>正式 checkpoint 文件（可被当作已提交状态）。</summary>
    Committed = 0,

    /// <summary>写盘中断留下的临时文件 ⇒ **绝不可**当作已提交状态（忽略，不参与水位线判定）。</summary>
    TemporaryResidue = 1,

    /// <summary>与本 checkpoint 无关的文件。</summary>
    Foreign = 2,
}

/// <summary>
/// 局部维护的 **checkpoint 模型 + 路径解析**（方案 §2.1）：本切片**零 IO** ——
/// 不读盘、不写盘、不建目录、不做原子替换（真实写盘属 S3），只提供纯计算与纯文本解析。
/// <para>
/// <b>位置</b>（唯一真源推导，方案 §2.1）：<c>&lt;IndexRoot&gt;/.maintenance/&lt;live-index-directory-name&gt;/checkpoint.v1.json</c>，
/// 其中 <c>&lt;live-index-directory-name&gt;</c> **只能**由
/// <c>FullTextIndexPaths.ResolveIndexDirectory(indexRoot, corpusRoot)</c> 的返回值取目录名得到。
/// 本类<b>不含</b>任何 SHA256：命名哈希的实现只允许存在一处（<c>FullTextIndexPaths</c>）。
/// </para>
/// <para>
/// <b>为什么不落在 corpus 里</b>（方案 §2.1）：会污染用户仓库、触发 watcher，
/// 并形成「维护状态写入 → 被当作源文件变更 → 再次维护」的自反馈环。
/// </para>
/// </summary>
public sealed record MaintenanceCheckpoint
{
    /// <summary>本实现支持的 checkpoint 版本号（同时决定文件名里的 <c>v1</c>）。</summary>
    public const int CurrentVersion = 1;

    /// <summary>索引根下的维护状态目录名（固定为 <c>.maintenance</c>）。</summary>
    public const string StateDirectoryName = ".maintenance";

    /// <summary>checkpoint 文件名前缀（后接版本号）。</summary>
    public const string FileNamePrefix = "checkpoint.v";

    /// <summary>checkpoint 文件名后缀。</summary>
    public const string FileNameExtension = ".json";

    /// <summary>临时文件标记：正式文件名 + 本标记 + token ⇒ 写盘中断的残留，永远不算已提交。</summary>
    public const string TemporaryFileMarker = ".tmp-";

    /// <summary>正式 checkpoint 文件名（由版本号推导，避免两处各自硬编码）。</summary>
    public static string FileName => $"{FileNamePrefix}{CurrentVersion}{FileNameExtension}";

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.None,
        DateFormatHandling = DateFormatHandling.IsoDateFormat,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        DateParseHandling = DateParseHandling.None,
        NullValueHandling = NullValueHandling.Include,
        Converters = { new UtcDateTimeOffsetConverter() },
    };

    /// <summary>
    /// 时间字段写成**协议口径**：UTC ISO-8601 + 7 位小数 + 字面 <c>Z</c>（方案 §2.1 的示例形状）。
    /// <para>
    /// 不用 Newtonsoft 的默认 DateTimeOffset 形状，是因为它输出 <c>+00:00</c> 而不是 <c>Z</c>；
    /// 协议形状必须由本组件**自己钉死**，而不是依赖第三方库的版本行为。
    /// </para>
    /// <para>
    /// ⚠️ 必须同时设 <c>DateParseHandling.None</c>：否则 JsonTextReader 会先把 ISO 串自动解析成
    /// <see cref="DateTime"/> 再交给转换器（实测：<c>reader.ValueType == DateTime</c>），协议串形状就丢了。
    /// </para>
    /// </summary>
    private sealed class UtcDateTimeOffsetConverter : JsonConverter
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

        public override bool CanConvert(Type objectType)
            => objectType == typeof(DateTimeOffset) || objectType == typeof(DateTimeOffset?);

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value is null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteValue(((DateTimeOffset)value).UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
        }

        public override object? ReadJson(
            JsonReader reader,
            Type objectType,
            object? existingValue,
            JsonSerializer serializer)
        {
            switch (reader.Value)
            {
                case null:
                    return null;

                // 常规路径（DateParseHandling.None ⇒ 时间戳保持字符串）
                case string text when !string.IsNullOrWhiteSpace(text):
                    return DateTimeOffset
                        .Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None)
                        .ToUniversalTime();

                // 容错：若读取器仍把时间戳自动解析成 DateTime，按 UTC 处理（本协议的时间一律 UTC 写入）
                case DateTime dateTime:
                    return new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc));

                case DateTimeOffset dateTimeOffset:
                    return dateTimeOffset.ToUniversalTime();
            }

            throw new JsonSerializationException(
                $"时间字段必须是 ISO-8601 UTC 字符串（实际类型 {reader.ValueType?.Name ?? "null"}）。");
        }
    }

    /// <summary>协议版本。必填：缺失 ⇒ 解析失败（不得默认成当前版本）。</summary>
    [JsonProperty("version", Order = 0, Required = Required.Always)]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>该 checkpoint 所属的语料根（绝对路径）。必填：与 scope 不匹配的 checkpoint 不可信。</summary>
    [JsonProperty("scopeRoot", Order = 1, Required = Required.Always)]
    public string ScopeRoot { get; init; } = string.Empty;

    /// <summary>
    /// 过滤策略指纹（文件 patterns / 扩展名集合 / 噪声规则）。不匹配时必须全范围局部校准或要求手动重建，
    /// <b>不得</b>沿用旧 watermark。
    /// </summary>
    [JsonProperty("policyFingerprint", Order = 2)]
    public string? PolicyFingerprint { get; init; }

    /// <summary>
    /// watermark = 最近一次**成功完成**的补偿扫描的**开始**时刻。
    /// 为 null 表示尚无可用基线（无 checkpoint；尚未从 <c>.last_indexed</c> 取到保守起点）⇒ 需全范围校准。
    /// </summary>
    [JsonProperty("watermarkUtc", Order = 3)]
    public DateTimeOffset? WatermarkUtc { get; init; }

    /// <summary>每次成功推进递增；时钟回拨校准后也递增（用于识别临时文件、旧写覆盖与观察状态变化）。必填。</summary>
    [JsonProperty("generation", Order = 4, Required = Required.Always)]
    public long Generation { get; init; }

    /// <summary>最近一次成功推进的批次标识（诊断与幂等审计用，不作为正确性唯一依据）。</summary>
    [JsonProperty("lastCompletedBatchId", Order = 5)]
    public string? LastCompletedBatchId { get; init; }

    /// <summary>写盘时刻（观察字段，**不参与** mtime 判定）。</summary>
    [JsonProperty("writtenUtc", Order = 6)]
    public DateTimeOffset? WrittenUtc { get; init; }

    /// <summary>序列化为协议 JSON（形状与方案 §2.1 的示例一致：7 个字段全在，时间为 UTC ISO）。</summary>
    public string ToJson() => JsonConvert.SerializeObject(this, JsonSettings);

    /// <summary>
    /// 解析 checkpoint 文本。**纯函数**（不触盘）：调用方负责读文件，本方法只负责「文本 → 可用状态」。
    /// <list type="bullet">
    /// <item><description>null / 空白 ⇒ <see cref="MaintenanceCheckpointReadStatus.Missing"/>。</description></item>
    /// <item><description>JSON 损坏 / 必填字段缺失 / 类型不符 ⇒ <see cref="MaintenanceCheckpointReadStatus.Malformed"/>。</description></item>
    /// <item><description>版本号不受支持 ⇒ <see cref="MaintenanceCheckpointReadStatus.UnsupportedVersion"/>。</description></item>
    /// </list>
    /// </summary>
    /// <param name="json">checkpoint 文件内容。</param>
    /// <param name="checkpoint">仅在返回 <see cref="MaintenanceCheckpointReadStatus.Ok"/> 时非 null。</param>
    public static MaintenanceCheckpointReadStatus TryParse(string? json, out MaintenanceCheckpoint? checkpoint)
    {
        checkpoint = null;

        if (string.IsNullOrWhiteSpace(json))
            return MaintenanceCheckpointReadStatus.Missing;

        MaintenanceCheckpoint? parsed;
        try
        {
            parsed = JsonConvert.DeserializeObject<MaintenanceCheckpoint>(json, JsonSettings);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or OverflowException)
        {
            return MaintenanceCheckpointReadStatus.Malformed;
        }

        if (parsed is null)
            return MaintenanceCheckpointReadStatus.Malformed;

        if (parsed.Version != CurrentVersion)
            return MaintenanceCheckpointReadStatus.UnsupportedVersion;

        if (string.IsNullOrWhiteSpace(parsed.ScopeRoot))
            return MaintenanceCheckpointReadStatus.Malformed;

        checkpoint = parsed;
        return MaintenanceCheckpointReadStatus.Ok;
    }

    /// <summary>该 checkpoint 是否属于给定 scope 语料根（比较口径复用 <c>FullTextIndexPaths.NormalizeCorpusRoot</c>）。</summary>
    /// <param name="checkpoint">已解析的 checkpoint。</param>
    /// <param name="scopeRoot">当前 scope 语料根。</param>
    public static bool MatchesScope(MaintenanceCheckpoint checkpoint, string scopeRoot)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRoot);

        if (string.IsNullOrWhiteSpace(checkpoint.ScopeRoot))
            return false;

        return string.Equals(
            FullTextIndexPaths.NormalizeCorpusRoot(checkpoint.ScopeRoot),
            FullTextIndexPaths.NormalizeCorpusRoot(scopeRoot),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 解析维护状态目录：<c>&lt;indexRoot&gt;/.maintenance/&lt;live-index-directory-name&gt;</c>。
    /// <para>⚠️ 目录名取自 <c>FullTextIndexPaths.ResolveIndexDirectory</c>（命名哈希唯一真源），本类不复刻哈希。</para>
    /// </summary>
    /// <param name="indexRootDirectory">引擎的索引根目录。</param>
    /// <param name="corpusRootPath">scope 语料根（不是索引目录）。</param>
    public static string ResolveStateDirectory(string indexRootDirectory, string corpusRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexRootDirectory);

        var indexDirectory = FullTextIndexPaths.ResolveIndexDirectory(indexRootDirectory, corpusRootPath);
        var liveIndexDirectoryName = Path.GetFileName(indexDirectory);

        if (string.IsNullOrWhiteSpace(liveIndexDirectoryName))
        {
            throw new ArgumentException(
                $"无法从索引目录推导出 live 索引目录名：{indexDirectory}",
                nameof(corpusRootPath));
        }

        return Path.Combine(indexRootDirectory, StateDirectoryName, liveIndexDirectoryName);
    }

    /// <summary>解析正式 checkpoint 全路径（目录可能尚不存在；本方法**不**创建任何目录）。</summary>
    /// <param name="indexRootDirectory">索引根目录。</param>
    /// <param name="corpusRootPath">scope 语料根。</param>
    public static string ResolveCheckpointPath(string indexRootDirectory, string corpusRootPath)
        => Path.Combine(ResolveStateDirectory(indexRootDirectory, corpusRootPath), FileName);

    /// <summary>
    /// 解析「临时文件 + 原子替换」写盘路径：正式文件名 + <see cref="TemporaryFileMarker"/> + token。
    /// <para>
    /// 写盘约定（真实写盘属 S3）：① 先写本临时文件并 flush；② 再用原子替换覆盖正式文件；
    /// ③ 崩溃留下的临时文件（<see cref="MaintenanceCheckpointFileKind.TemporaryResidue"/>）
    /// **绝不**可被当作已提交 checkpoint。
    /// </para>
    /// </summary>
    /// <param name="indexRootDirectory">索引根目录。</param>
    /// <param name="corpusRootPath">scope 语料根。</param>
    /// <param name="token">批次 / generation 派生的唯一片段（纯文件名片段，不得含路径分隔符）。</param>
    public static string ResolveTemporaryCheckpointPath(
        string indexRootDirectory,
        string corpusRootPath,
        string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        if (token.Contains('\\') || token.Contains('/') || token.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                $"临时文件 token 必须是纯文件名片段（不得含路径分隔符或非法字符）：{token}",
                nameof(token));
        }

        return Path.Combine(
            ResolveStateDirectory(indexRootDirectory, corpusRootPath),
            FileName + TemporaryFileMarker + token);
    }

    /// <summary>把状态目录里的一个**文件名**归类（纯字符串判定；入参可含目录部分，只取末段）。</summary>
    /// <param name="fileName">文件名（允许传完整路径，只取末段）。</param>
    public static MaintenanceCheckpointFileKind ClassifyFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return MaintenanceCheckpointFileKind.Foreign;

        var name = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrEmpty(name))
            return MaintenanceCheckpointFileKind.Foreign;

        if (string.Equals(name, FileName, StringComparison.OrdinalIgnoreCase))
            return MaintenanceCheckpointFileKind.Committed;

        if (name.StartsWith(FileName + TemporaryFileMarker, StringComparison.OrdinalIgnoreCase))
            return MaintenanceCheckpointFileKind.TemporaryResidue;

        return MaintenanceCheckpointFileKind.Foreign;
    }

    /// <summary>该文件名是否可被当作**已提交**的 checkpoint（临时残留一律 false）。</summary>
    public static bool IsCommittedCheckpointFile(string? fileName)
        => ClassifyFile(fileName) == MaintenanceCheckpointFileKind.Committed;

    /// <summary>该文件名是否是写盘中断留下的临时残留（必须忽略，不得参与水位线判定）。</summary>
    public static bool IsTemporaryResidue(string? fileName)
        => ClassifyFile(fileName) == MaintenanceCheckpointFileKind.TemporaryResidue;

    /// <summary>
    /// 一次**成功完成**的补偿轮次之后推进 checkpoint（方案 §2.3 + §2.4）。
    /// <list type="bullet">
    /// <item><description>新 watermark = <see cref="MTimeComparison.ComputeNextWatermark"/>（即扫描**开始**时刻）。</description></item>
    /// <item><description><c>generation</c> 单调递增（含时钟回拨校准后的向后重置场景）。</description></item>
    /// <item><description>只有全部提交成功才调用本方法；任一路径失败时不得调用（checkpoint 不推进）。</description></item>
    /// </list>
    /// </summary>
    /// <param name="previous">旧 checkpoint；没有为 null（此时 generation 从 1 开始）。</param>
    /// <param name="scopeRoot">本 scope 语料根。</param>
    /// <param name="policyFingerprint">本次生效的策略指纹。</param>
    /// <param name="scanStartedUtc">本轮扫描开始时刻。</param>
    /// <param name="scanFinishedUtc">本轮扫描结束时刻（不参与 watermark 取值，仅作对照）。</param>
    /// <param name="batchId">本轮批次标识。</param>
    /// <param name="writtenUtc">写盘时刻（观察字段）。</param>
    public static MaintenanceCheckpoint Advance(
        MaintenanceCheckpoint? previous,
        string scopeRoot,
        string? policyFingerprint,
        DateTimeOffset scanStartedUtc,
        DateTimeOffset scanFinishedUtc,
        string batchId,
        DateTimeOffset writtenUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);

        if (previous is not null && !MatchesScope(previous, scopeRoot))
        {
            throw new InvalidOperationException(
                $"旧 checkpoint 属于另一个 scope（checkpoint.ScopeRoot={previous.ScopeRoot}，scopeRoot={scopeRoot}）；" +
                "跨 scope 沿用 watermark 会导致漏处理，必须拒绝。");
        }

        return new MaintenanceCheckpoint
        {
            Version = CurrentVersion,
            ScopeRoot = scopeRoot,
            PolicyFingerprint = policyFingerprint,
            WatermarkUtc = MTimeComparison.ComputeNextWatermark(scanStartedUtc, scanFinishedUtc),
            Generation = (previous?.Generation ?? 0) + 1,
            LastCompletedBatchId = batchId,
            WrittenUtc = writtenUtc,
        };
    }
}
