using System.Text.Json;

namespace PuddingCore.Panels;

/// <summary>
/// 用户在画布上画的标注，经<b>交接文件</b>从 Desktop 到 Core 的一份快照。
///
/// <para>
/// <b>为什么需要这个文件</b>：标注由用户在 Desktop 的画布页里画出来，只活在 WebView2 里；
/// 而 Agent 侧的 <c>read_panel</c> 读的是 Core。两侧之间原本没有任何取回通道 ——
/// 结果是「用户明明画了箭头和文字，Agent 只能靠截屏猜坐标」。文件是这条链上最窄的一段：
/// Desktop 写（它本来就在 <c>{DataRoot}</c> 下写制品与标记图），Core 读，
/// 不新增 HTTP 写入口（<c>PanelCanvasApiController</c> 刻意只读），也不改能力通道方向。
/// </para>
/// <para>
/// <b>文件里存什么</b>：<see cref="PayloadJson"/> 是画布页 <c>puddingAnnotation.take()</c>
/// 返回的原文，逐字保存、<b>不由 Core 解释</b>。items 的坐标是**页面显示像素**，
/// 归一化（<c>round(px / width * 1000)</c>，夹在 0–999）的语义属于标注层，
/// 不在 Core 里复制第二份实现 —— 读的人按需自己换算。
/// </para>
/// <para>
/// <b>一致性</b>：写入方每次用户改动后整体覆盖（先写 <c>.tmp</c> 再 rename），
/// 所以文件只有「上一版」或「这一版」，不会读到半截。空 <c>items</c> 也是有效内容
/// （用户确实清空了），不要当成「没有标注」来特判。
/// </para>
/// </summary>
/// <param name="PanelId">画布 id（与 <see cref="PanelCanvasSnapshot.PanelId"/> 同一套规则）。</param>
/// <param name="UpdatedAt">写文件的一侧给出的时间；缺失或不可解析时为 null。</param>
/// <param name="PayloadJson">画布页载荷原文（<c>{src,width,height,naturalWidth,naturalHeight,items[]}</c>）。</param>
public sealed record PanelAnnotationFile(
    string PanelId,
    DateTimeOffset? UpdatedAt,
    string PayloadJson);

/// <summary>读交接文件。没有文件、文件损坏、id 对不上都返回 <c>null</c>（不是异常）。</summary>
public interface IPanelAnnotationFileReader
{
    /// <param name="panelId">画布 id。</param>
    /// <returns>快照；没有可用快照时为 <c>null</c>。</returns>
    PanelAnnotationFile? Read(string panelId);
}

/// <summary>
/// 交接文件的**命名与解析**规则（Core 侧真源）。
///
/// <para>
/// 文件名公式与 <c>PuddingDesktop.Foundation.PanelAnnotationSnapshotWriter</c> 里的实现
/// 必须逐字一致 —— 两侧各有一份（Desktop 的 Foundation 只依赖 BCL，不能反向引用 Core），
/// 靠两侧的「文件名锁定」测试把这条约定钉住。改公式必须同时改两侧与两侧测试。
/// </para>
/// </summary>
public static class PanelAnnotationFileName
{
    /// <summary>当前格式版本。读方遇到不认识的版本一律当作「没有快照」，不去猜旧格式。</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// 与画布内容文件（<c>panel-{id}.json</c>）同一目录、同一前缀：一起备份、一起清理，
    /// 文件名一眼能看出它属于哪块画布。
    /// </summary>
    public static string For(string panelId)
        => $"panel-{PanelCanvasGuard.RequirePanelId(panelId)}.annotations.json";

    /// <summary>整条路径。<paramref name="panelsRoot"/> 是 Core 的画布目录（<c>{DataRoot}/panels</c>）。</summary>
    public static string PathFor(string panelsRoot, string panelId)
        => Path.Combine(panelsRoot, For(panelId));
}

/// <summary>读交接文件的落盘实现：每次读盘，不做缓存（文件很小，而「读到昨天的标注」比慢一点更糟）。</summary>
public sealed class FilePanelAnnotationFileReader : IPanelAnnotationFileReader
{
    private readonly string _root;

    /// <param name="panelsRoot">画布目录（与 <see cref="FilePanelCanvasStore"/> 同一个）。</param>
    /// <exception cref="ArgumentException"><paramref name="panelsRoot"/> 为空。</exception>
    public FilePanelAnnotationFileReader(string panelsRoot)
    {
        if (string.IsNullOrWhiteSpace(panelsRoot))
        {
            throw new ArgumentException("panelsRoot is required.", nameof(panelsRoot));
        }

        _root = Path.GetFullPath(panelsRoot);
    }

    public PanelAnnotationFile? Read(string panelId)
    {
        string path;
        try
        {
            path = PanelAnnotationFileName.PathFor(_root, panelId);
        }
        catch (ArgumentException)
        {
            // 非法 id 不是异常状况：调用方问了不存在的画布，答案就是「没有」。
            return null;
        }

        string text;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 正在被覆盖（rename 窗口极短）：这一轮读不到，下一轮会读到。
            return null;
        }

        return TryParse(text, panelId, out var file, out _) ? file : null;
    }

    /// <summary>
    /// 解析交接文件，并**校验它自证属于** <paramref name="expectedPanelId"/>。
    ///
    /// <para>
    /// 为什么要校验：用户可能同时开着几块画布，而轮询、写盘、读取是三条独立的时序。
    /// 不看 id 就采信，等于把 A 画的箭头当成 B 的 —— 比读不到更糟，因为它看起来是有效数据。
    /// </para>
    /// </summary>
    public static bool TryParse(
        string? json,
        string expectedPanelId,
        out PanelAnnotationFile? file,
        out string? error)
    {
        file = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "empty";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"invalid json: {ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "root is not an object";
                return false;
            }

            if (!root.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionValue)
                || versionValue != PanelAnnotationFileName.SchemaVersion)
            {
                error = "unsupported schemaVersion";
                return false;
            }

            var panelId = ReadString(root, "panelId");
            if (string.IsNullOrWhiteSpace(panelId))
            {
                error = "panelId is missing";
                return false;
            }

            if (!string.Equals(panelId, expectedPanelId, StringComparison.OrdinalIgnoreCase))
            {
                error = $"panelId mismatch: file says '{panelId}'";
                return false;
            }

            var payload = ReadPayload(root);
            if (string.IsNullOrWhiteSpace(payload))
            {
                error = "payload is missing";
                return false;
            }

            DateTimeOffset? updatedAt = null;
            if (root.TryGetProperty("updatedAt", out var stamp)
                && stamp.ValueKind == JsonValueKind.String
                && stamp.TryGetDateTimeOffset(out var parsed))
            {
                updatedAt = parsed;
            }

            file = new PanelAnnotationFile(panelId, updatedAt, payload);
            return true;
        }
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// 载荷既接受**嵌套对象**（写方默认，文件更好读），也接受**被转义的字符串**
    /// （手工调试或旧脚本写出来的形态）。两种都当同一份载荷处理，不做二次解析。
    /// </summary>
    private static string? ReadPayload(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload))
        {
            return null;
        }

        return payload.ValueKind switch
        {
            JsonValueKind.Object or JsonValueKind.Array => payload.GetRawText(),
            JsonValueKind.String => payload.GetString(),
            _ => null,
        };
    }
}
