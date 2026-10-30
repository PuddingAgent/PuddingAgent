using System.Text;
using System.Text.Json;

namespace PuddingRetrieval.Contracts;

/// <summary>
/// 符号标识的**可重定位性**判定（symbol_ref 提案 §6）。
/// <para>
/// 实测依据：<c>RoslynSymbolId.GetId</c> 优先用 Roslyn documentation comment id
/// （声明式，跨行移动稳定）；回退路径 <c>BuildFallbackId</c> 生成
/// <c>Kind:(容器)Name@SourceSpan.Start</c> —— 把字符偏移编进了 id，
/// 文件前面多一个字符 id 就变，**不可重定位**。
/// </para>
/// <para>
/// 因此重定位方案必须按 id 形态分流，不能假装所有符号都能重新定位。
/// </para>
/// </summary>
public enum SymbolIdStability
{
    /// <summary>DocId 形态（<c>T:</c>/<c>M:</c>/<c>P:</c>/<c>F:</c>/<c>E:</c>/<c>N:</c>）：声明式，可重定位。</summary>
    Declarative,

    /// <summary>含位置偏移（<c>@span</c>）的回退形态：每次编辑都可能变，**不可重定位**。</summary>
    PositionDependent,

    /// <summary>无法判定（如 Python/TS 索引器的自有格式）：只能当不可重定位处理。</summary>
    Unknown,
}

/// <summary>
/// 跨请求的符号引用 v1（`workspace_open` 的 <c>ref</c> 参数、`workspace_search` 的关系意图入参）。
/// <para>
/// 规格依据：设计文档 `Docs/12_features/增强索引统一检索与代码地图设计方案-2026-10-18.md` §8.3 ——
/// <b>ref 绑定身份、源版本、scope 和 provider generation，本身不授予权限</b>；
/// 源码变化后不得沿旧行号打开当前代码。
/// </para>
/// <para>
/// 与 <see cref="SymbolIdentity"/> 的**分工**（提案 §7）：后者是单次查询内的去重键，刻意不含版本；
/// 本类型是跨请求引用，<b>必须</b>含版本。合并会让 ADR-089 §2.4 的跨 scope 去重失效。
/// </para>
/// <para>
/// 载体是**不透明 token**（<c>pmsym1.&lt;base64url(紧凑 JSON)&gt;</c>）而非可读串：
/// docId 天然含 <c>: . ( ) ,</c>、路径含 <c>\</c> 与 <c>:</c>，任何分隔符都会被真实数据击穿，
/// 而击穿的表现是"能解析但解析错"，比解析失败更难发现。
/// </para>
/// <para>
/// <b>不签名</b>（§8.3：ref 不授予权限）⇒ 解析结果一律当**不可信输入**逐项校验，
/// 校验与创建共用同一套不变量，伪造的 ref 与非法参数走同一条拒绝路径。
/// </para>
/// </summary>
public sealed record SymbolRefV1
{
    /// <summary>token 前缀；同时承担 schema 版本标识。</summary>
    public const string TokenPrefix = "pmsym1.";

    /// <summary>当前 schema 值（紧凑 JSON 的 <c>s</c> 字段）。</summary>
    public const string CurrentSchema = "1";

    /// <summary>token 长度硬上限（提案 §3：签发时即可界，避免路径长度不可控）。</summary>
    public const int MaxTokenLength = 512;

    private SymbolRefV1(
        string projectId,
        string filePath,
        string symbolId,
        int startLine,
        int endLine,
        string contentHash,
        string providerGeneration)
    {
        ProjectId = projectId;
        FilePath = filePath;
        SymbolId = symbolId;
        StartLine = startLine;
        EndLine = endLine;
        ContentHash = contentHash;
        ProviderGeneration = providerGeneration;
    }

    /// <summary>所属项目标识（scope 绑定）。</summary>
    public string ProjectId { get; }

    /// <summary>项目内相对路径，已归一化（<c>\</c> → <c>/</c>、折叠分隔符、小写）。</summary>
    public string FilePath { get; }

    /// <summary>符号标识，已归一化（折叠空白）。</summary>
    public string SymbolId { get; }

    /// <summary>1 基起始行。<b>仅作提示</b>，不参与"打开哪一段"的决策（决策见三态协议）。</summary>
    public int StartLine { get; }

    /// <summary>1 基结束行。<b>仅作提示</b>。</summary>
    public int EndLine { get; }

    /// <summary>索引时的文件内容 hash —— 源版本的载体，决定 FRESH / STALE。</summary>
    public string ContentHash { get; }

    /// <summary>产出该符号的解析/索引代际标识。</summary>
    public string ProviderGeneration { get; }

    /// <summary>
    /// 创建合法 ref。任一项不满足不变量即抛 <see cref="ArgumentException"/>
    /// （异常消息同时用作 <see cref="TryParse"/> 的拒绝原因，两条路径口径一致）。
    /// </summary>
    public static SymbolRefV1 Create(
        string projectId,
        string filePath,
        string symbolId,
        int startLine,
        int endLine,
        string contentHash,
        string providerGeneration)
    {
        var error = Validate(
            projectId, filePath, symbolId, startLine, endLine, contentHash, providerGeneration,
            out var normalizedPath, out var normalizedSymbolId);

        return error is not null
            ? throw new ArgumentException(error, nameof(filePath))
            : new SymbolRefV1(
                projectId.Trim(),
                normalizedPath!,
                normalizedSymbolId!,
                startLine,
                endLine,
                contentHash.Trim(),
                providerGeneration.Trim());
    }

    /// <summary>
    /// 序列化为不透明 token。字段过长导致超出 <see cref="MaxTokenLength"/> 时抛
    /// <see cref="ArgumentException"/> —— 宁可签发失败，也不签发一个下游会用不上的 ref。
    /// </summary>
    public string ToToken()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("s", CurrentSchema);
            writer.WriteString("p", ProjectId);
            writer.WriteString("f", FilePath);
            writer.WriteString("i", SymbolId);
            writer.WriteNumber("a", StartLine);
            writer.WriteNumber("b", EndLine);
            writer.WriteString("h", ContentHash);
            writer.WriteString("g", ProviderGeneration);
            writer.WriteEndObject();
        }

        var token = TokenPrefix + EncodeBase64Url(buffer.ToArray());
        return token.Length > MaxTokenLength
            ? throw new ArgumentException(
                $"ref token 超过 {MaxTokenLength} 字节上限（实际 {token.Length}）。请缩短路径或符号标识。")
            : token;
    }

    /// <summary>解析 token。失败抛 <see cref="FormatException"/>（消息可读，供工具层转诊断）。</summary>
    public static SymbolRefV1 Parse(string token) =>
        TryParse(token, out var parsed, out var error)
            ? parsed!
            : throw new FormatException(error);

    /// <summary>
    /// 尝试解析 token。**所有输入都视为不可信**：前缀、长度、base64url、JSON 结构、
    /// schema 版本、以及全部业务不变量逐项校验；任一项不符即返回 false 并给出可读原因，
    /// 不抛异常、不回落任何默认值。
    /// </summary>
    public static bool TryParse(string? token, out SymbolRefV1? result, out string? error)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            error = "ref 为空。";
            return false;
        }

        if (token.Length > MaxTokenLength)
        {
            error = $"ref 超长（{token.Length} > {MaxTokenLength} 字节上限）。";
            return false;
        }

        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            error = $"ref 前缀不符（应以 {TokenPrefix} 开头）；旧行号/文件名不能冒充 ref。";
            return false;
        }

        byte[] payload;
        try
        {
            payload = DecodeBase64Url(token[TokenPrefix.Length..]);
        }
        catch (FormatException)
        {
            error = "ref 不是合法的 base64url 编码。";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            error = "ref 内容不是合法 JSON。";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "ref 内容不是 JSON 对象。";
                return false;
            }

            if (!TryReadString(root, "s", out var schema, out error))
                return false;

            if (!string.Equals(schema, CurrentSchema, StringComparison.Ordinal))
            {
                error = $"ref schema 不受支持: {schema}（当前 {CurrentSchema}）。";
                return false;
            }

            if (!TryReadString(root, "p", out var projectId, out error) ||
                !TryReadString(root, "f", out var filePath, out error) ||
                !TryReadString(root, "i", out var symbolId, out error) ||
                !TryReadString(root, "h", out var contentHash, out error) ||
                !TryReadString(root, "g", out var providerGeneration, out error) ||
                !TryReadInt(root, "a", out var startLine, out error) ||
                !TryReadInt(root, "b", out var endLine, out error))
            {
                return false;
            }

            var invalid = Validate(
                projectId, filePath, symbolId, startLine, endLine, contentHash, providerGeneration,
                out var normalizedPath, out var normalizedSymbolId);

            if (invalid is not null)
            {
                error = "ref 字段不合法：" + invalid;
                return false;
            }

            error = null;
            result = new SymbolRefV1(
                projectId!.Trim(),
                normalizedPath!,
                normalizedSymbolId!,
                startLine,
                endLine,
                contentHash!.Trim(),
                providerGeneration!.Trim());
            return true;
        }
    }

    /// <summary>
    /// 判定符号标识的可重定位性（提案 §6）。重定位必须按此分流：
    /// <see cref="SymbolIdStability.PositionDependent"/> 只允许报陈旧，不允许猜新位置。
    /// </summary>
    public static SymbolIdStability ClassifySymbolId(string? symbolId)
    {
        if (string.IsNullOrWhiteSpace(symbolId))
            return SymbolIdStability.Unknown;

        var trimmed = symbolId.Trim();

        // Roslyn documentation comment id 前缀（T: 类型、M: 方法、P: 属性、F: 字段、E: 事件、N: 命名空间）。
        foreach (var prefix in new[] { "T:", "M:", "P:", "F:", "E:", "N:" })
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                return SymbolIdStability.Declarative;
        }

        return trimmed.Contains('@', StringComparison.Ordinal)
            ? SymbolIdStability.PositionDependent
            : SymbolIdStability.Unknown;
    }

    /// <summary>
    /// 共用不变量校验（创建与解析同一条路径）。返回 <c>null</c> 表示合法，
    /// 否则返回可读原因；归一化结果通过 out 参数回传，避免两处各写一份归一化规则。
    /// </summary>
    private static string? Validate(
        string? projectId,
        string? filePath,
        string? symbolId,
        int startLine,
        int endLine,
        string? contentHash,
        string? providerGeneration,
        out string? normalizedPath,
        out string? normalizedSymbolId)
    {
        normalizedPath = null;
        normalizedSymbolId = null;

        if (string.IsNullOrWhiteSpace(projectId))
            return "缺少 project_id（ref 必须绑定 scope）。";

        if (string.IsNullOrWhiteSpace(symbolId))
            return "缺少 symbol_id。";

        if (string.IsNullOrWhiteSpace(contentHash))
            return "缺少 content_hash —— 无源版本不得签发 ref，请改用 {project_id,path} 请求。";

        if (string.IsNullOrWhiteSpace(providerGeneration))
            return "缺少 provider_generation。";

        if (string.IsNullOrWhiteSpace(filePath))
            return "缺少 file_path。";

        // 归一化复用 SymbolIdentity 的规则（\ → /、折叠分隔符、小写），避免两处漂移。
        var path = SymbolIdentity.NormalizePath(filePath);

        if (path.Contains(':', StringComparison.Ordinal))
            return "file_path 不得含 ':'（禁绝对路径/盘符/备用数据流）；ref 只接受项目内相对路径。";

        if (path.StartsWith('/'))
            return "file_path 不得是根路径；ref 只接受项目内相对路径。";

        foreach (var segment in path.Split('/'))
        {
            if (segment == "..")
                return "file_path 不得含 '..' 段（路径逃逸）。";
        }

        if (startLine < 1)
            return "start_line 必须 ≥ 1（1 基）。";

        if (endLine < startLine)
            return "end_line 不得小于 start_line。";

        normalizedPath = path;
        normalizedSymbolId = SymbolIdentity.Normalize(symbolId);
        return null;
    }

    private static bool TryReadString(JsonElement root, string name, out string? value, out string? error)
    {
        value = null;

        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            error = $"ref 缺少字符串字段 '{name}'。";
            return false;
        }

        value = element.GetString();
        error = null;
        return true;
    }

    private static bool TryReadInt(JsonElement root, string name, out int value, out string? error)
    {
        value = 0;

        if (!root.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out value))
        {
            error = $"ref 缺少整数字段 '{name}'。";
            return false;
        }

        error = null;
        return true;
    }

    private static string EncodeBase64Url(byte[] payload) =>
        Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DecodeBase64Url(string text)
    {
        var builder = new StringBuilder(text);
        builder.Replace('-', '+').Replace('_', '/');
        switch (builder.Length % 4)
        {
            case 2: builder.Append("=="); break;
            case 3: builder.Append('='); break;
            case 1: throw new FormatException("base64url 长度非法。");
        }

        return Convert.FromBase64String(builder.ToString());
    }
}
