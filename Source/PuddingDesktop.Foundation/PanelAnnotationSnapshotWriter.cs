using System.Text.Json;

namespace PuddingDesktop.Foundation;

/// <summary>
/// 把画布上的标注写成一份**交接文件**，供 Core 侧（<c>read_panel</c>）读取。
///
/// <para>
/// <b>为什么是文件而不是新通道</b>：标注在 WebView2 页里，只随用户的操作出现，没有
/// 任何「Core 主动取一次」的路径；反过来，Core 的 HTTP 面刻意只读（匿名写入口在
/// <c>0.0.0.0</c> 绑定下等于把画布交给局域网），能力通道又只有 Core→Desktop 一个方向。
/// 双方本来就共用 <c>{DataRoot}</c>（制品库、标记图都在那儿），一个文件是这段链上
/// 最小的桥：Desktop 写、Core 读，各自边界不变。
/// </para>
/// <para>
/// <b>写什么</b>：<c>puddingAnnotation.take()</c> 返回的原文，逐字放进 <c>payload</c>，
/// 只在外层加上 <c>schemaVersion</c> / <c>panelId</c> / <c>updatedAt</c>。
/// 本类<b>不解释</b> items 的语义 —— 坐标是页面显示像素，归一化的约定属于标注层
/// （<see cref="PanelAnnotation.Normalize"/>），在这里复制一份只会多出第二种真源。
/// </para>
/// <para>
/// <b>一致性</b>：先写 <c>.tmp</c> 再原子 rename ⇒ 读方要么读到上一版、要么读到这一版，
/// 不会读到写了一半的 JSON。失败一律返回 <c>false</c> 并给出原因，不抛异常 ——
/// 标注写不出来不该打断用户画下一笔。
/// </para>
/// </summary>
public static class PanelAnnotationSnapshotWriter
{
    /// <summary>格式版本。与 Core 侧 <c>PanelAnnotationFileName.SchemaVersion</c> 必须一致。</summary>
    public const int SchemaVersion = 1;

    /// <summary>文件名后缀：与画布内容文件同目录同前缀，便于一起备份/清理与人工辨认。</summary>
    public const string FileSuffix = ".annotations.json";

    private const string TempSuffix = ".tmp";

    /// <summary>
    /// 文件名公式。与 Core 侧 <c>PanelAnnotationFileName.For</c> 逐字一致，
    /// 由两侧的「文件名锁定」测试共同钉住 —— 改公式必须同时改两侧。
    /// </summary>
    public static string For(string panelId) => $"panel-{RequirePanelId(panelId)}{FileSuffix}";

    /// <summary>整条路径。<paramref name="panelsRoot"/> 即 Core 的画布目录（<c>{DataRoot}/panels</c>）。</summary>
    public static string PathFor(string panelsRoot, string panelId)
        => Path.Combine(panelsRoot, For(panelId));

    /// <summary>
    /// 覆盖写一份快照。
    /// </summary>
    /// <param name="panelsRoot">画布目录（不存在会被创建）。</param>
    /// <param name="panelId">画布 id；含路径分隔符等非法字符时直接失败（不做「清洗后照写」）。</param>
    /// <param name="payloadJson">画布页 <c>take()</c> 的原文；必须是 JSON 对象。</param>
    /// <param name="now">写入时间，进 <c>updatedAt</c>（便于判断快照有多旧）。</param>
    /// <param name="error">失败原因；成功时为 <c>null</c>。</param>
    /// <returns>是否真的写成了。</returns>
    public static bool TryPublish(
        string panelsRoot,
        string panelId,
        string? payloadJson,
        DateTimeOffset now,
        out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(panelsRoot))
        {
            error = "panelsRoot is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            // 空载荷意味着「页里没有 puddingAnnotation」或页面还没同步好：
            // 那不是「用户清空了标注」，不能拿它覆盖已有快照。
            error = "payload is empty";
            return false;
        }

        string path;
        try
        {
            path = PathFor(panelsRoot, panelId);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }

        JsonDocument payload;
        try
        {
            payload = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException ex)
        {
            error = $"payload is not valid json: {ex.Message}";
            return false;
        }

        using (payload)
        {
            if (payload.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "payload root is not an object";
                return false;
            }

            var json = BuildEnvelope(RequirePanelId(panelId), payload.RootElement, now);

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temp = path + TempSuffix;
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }
    }

    /// <summary>信封的 JSON 选项：字段名是小写驼峰，与 Core 侧的解析逐字对应。</summary>
    private static readonly JsonSerializerOptions EnvelopeJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string BuildEnvelope(string panelId, JsonElement payload, DateTimeOffset now)
        => JsonSerializer.Serialize(
            new AnnotationEnvelope(
                SchemaVersion,
                panelId,
                now.ToUniversalTime().ToString("O"),
                payload),
            EnvelopeJson);

    /// <summary>
    /// 画布 id 只允许字母、数字与 <c>. _ -</c>（与 <c>render_panel</c> 的约定一致）。
    ///
    /// <para>
    /// 这里**不做清洗**：把 <c>../x</c> 洗成 <c>x</c> 会让一次越界写悄悄变成一次正常写。
    /// 校验不过就直接失败，由调用方（状态栏/日志）把原因说出来。
    /// </para>
    /// </summary>
    private static string RequirePanelId(string? panelId)
    {
        var id = panelId?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > 64)
        {
            throw new ArgumentException("panelId must be 1-64 characters.", nameof(panelId));
        }

        foreach (var ch in id)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '_' or '-'))
            {
                throw new ArgumentException(
                    $"panelId contains an unsupported character '{ch}'.", nameof(panelId));
            }
        }

        return id;
    }

    /// <summary>外层信封。字段名是与 Core 侧的契约，改名等于改协议。</summary>
    private sealed record AnnotationEnvelope(
        int schemaVersion,
        string panelId,
        string updatedAt,
        JsonElement payload);
}
