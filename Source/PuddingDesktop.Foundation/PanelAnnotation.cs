namespace PuddingDesktop.Foundation;

/// <summary>标注形状。与 Seedream 交互编辑协议一一对应。</summary>
public enum PanelAnnotationKind
{
    /// <summary>框选：<c>&lt;bbox&gt;x1 y1 x2 y2&lt;/bbox&gt;</c>，精确控制编辑区域。</summary>
    Box = 0,

    /// <summary>点选：<c>&lt;point&gt;x y&lt;/point&gt;</c>，由模型判断影响范围。</summary>
    Point,
}

/// <summary>
/// 一次画布交互的完整载荷：**标注对象**（哪张图）+ 标注本身。
///
/// <para>
/// 保留 <see cref="SourceUri"/> 原始字符串而不在这里转成路径：形状解析与
/// “这个路径能不能读”是两件事，后者需要白名单（属于 Shell 策略）。
/// </para>
/// </summary>
public sealed record PanelCanvasPayload(
    string? SourceUri, double Width, double Height, IReadOnlyList<PanelAnnotation> Items,
    // 图片原始像素。缺失或非法时为 0，调用方需自行回退到 Width/Height。
    // 不设为必填：旧画布与手工构造的载荷没有这两个字段，但它们仍应可用。
    double NaturalWidth = 0, double NaturalHeight = 0);

/// <summary>
/// 画布上的一个用户标注，坐标一律是**归一化 0–999 整数**。
///
/// <para>
/// 归一化协议来自官方《Doubao Seedream 5.0 pro / flash 实现交互编辑指南》：
/// 图片宽高等分 1000 份，左上角 (0,0)、右下角 (999,999)，
/// 换算 <c>x = round(x_px / width * 1000)</c>。同一套 0–999 约定正好也是本平台
/// <c>generate_image(mode=precision)</c> 接受的坐标，所以这里**不做任何格式转换**，
/// 只负责「像素 → 归一化」和「结构化 → prompt 片段」。
/// </para>
///
/// <para>
/// 坐标由页面里的 JS 算出，属于**不可信输入**：Agent 生成的 HTML 可以塞任意数字。
/// 因此解析路径逐项校验（整数、范围内、数量上限），越界一律拒绝而不是夹取 ——
/// 夹取会把「明显算错了」悄悄变成「看起来正常的坐标」。
/// </para>
/// </summary>
public sealed record PanelAnnotation
{
    /// <summary>归一化坐标上限（含）。</summary>
    public const int MaxCoordinate = 999;

    /// <summary>一次最多接受的标注数量。超过通常是误用或恶意构造。</summary>
    public const int MaxItems = 10;

    private PanelAnnotation(PanelAnnotationKind kind, int x1, int y1, int x2, int y2)
    {
        Kind = kind;
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
    }

    public PanelAnnotationKind Kind { get; }

    public int X1 { get; }

    public int Y1 { get; }

    /// <summary>仅框选有意义；点选时等于 <see cref="X1"/>。</summary>
    public int X2 { get; }

    /// <summary>仅框选有意义；点选时等于 <see cref="Y1"/>。</summary>
    public int Y2 { get; }

    /// <summary>
    /// 像素坐标 → 归一化坐标。
    ///
    /// <para>
    /// 用 <see cref="MidpointRounding.AwayFromZero"/> 而不是默认的银行家舍入：
    /// 默认舍入会把 0.5 抹成 0，同一次框选在边界上反复出现 1 像素抖动，
    /// 而用户看不出原因。
    /// </para>
    /// </summary>
    public static int Normalize(double pixel, double extent)
    {
        if (double.IsNaN(pixel) || double.IsInfinity(pixel))
        {
            throw new ArgumentException("Pixel coordinate must be finite.", nameof(pixel));
        }

        if (double.IsNaN(extent) || extent <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(extent), "Extent must be a positive finite number.");
        }

        var scaled = Math.Round(pixel / extent * 1000, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(scaled, 0, MaxCoordinate);
    }

    /// <summary>由两个像素角点建立框选标注（自动排序，所以反向拖拽也成立）。</summary>
    public static PanelAnnotation FromPixels(
        double left, double top, double right, double bottom, double width, double height)
    {
        var x1 = Normalize(Math.Min(left, right), width);
        var x2 = Normalize(Math.Max(left, right), width);
        var y1 = Normalize(Math.Min(top, bottom), height);
        var y2 = Normalize(Math.Max(top, bottom), height);

        return new PanelAnnotation(PanelAnnotationKind.Box, x1, y1, x2, y2);
    }

    /// <summary>由像素点建立点选标注。</summary>
    public static PanelAnnotation PointFromPixels(double x, double y, double width, double height) =>
        new(PanelAnnotationKind.Point, Normalize(x, width), Normalize(y, height), 0, 0);

    /// <summary>直接以归一化坐标建立框选（供解析路径使用，已做过范围校验）。</summary>
    public static PanelAnnotation Box(int x1, int y1, int x2, int y2)
    {
        RequireInRange(x1, nameof(x1));
        RequireInRange(y1, nameof(y1));
        RequireInRange(x2, nameof(x2));
        RequireInRange(y2, nameof(y2));
        if (x1 > x2 || y1 > y2)
        {
            throw new ArgumentException("Box corners must be ordered: x1<=x2 and y1<=y2.");
        }

        return new PanelAnnotation(PanelAnnotationKind.Box, x1, y1, x2, y2);
    }

    /// <summary>直接以归一化坐标建立点选。</summary>
    public static PanelAnnotation Point(int x, int y)
    {
        RequireInRange(x, nameof(x));
        RequireInRange(y, nameof(y));
        return new PanelAnnotation(PanelAnnotationKind.Point, x, y, 0, 0);
    }

    /// <summary>
    /// 转成 Seedream 能读的 prompt 片段。只含整数，因此可以直接拼进 prompt，
    /// 不存在注入面。
    /// </summary>
    public string ToPromptFragment() => Kind == PanelAnnotationKind.Box
        ? $"<bbox>{X1} {Y1} {X2} {Y2}</bbox>"
        : $"<point>{X1} {Y1}</point>";

    /// <summary>
    /// 把多个标注格式化成一段可直接放进输入框的文字。
    /// 每行一个，方便用户手工删改 —— 用户是这条链路的最终编辑者。
    /// </summary>
    public static string DescribeForComposer(IReadOnlyList<PanelAnnotation> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return string.Empty;
        }

        return string.Join('\n', items.Select(item => "图一 " + item.ToPromptFragment()));
    }

    /// <summary>
    /// 带**来源图**的版本：开头一行写明改的是哪个文件，后面每行一个标注。
    ///
    /// <para>
    /// <paramref name="sourceUri"/> 来自页面，不可信。只有能经
    /// <see cref="PanelImageSource.TryResolve"/> 还原成白名单内真实路径时才写进文本；
    /// 否则静默省略 —— 把页面给的任意字符串塞进输入框，等于让 Agent 生成的 HTML
    /// 有机会往用户即将发送的消息里写任意内容。
    /// </para>
    /// </summary>
    public static string DescribeForComposer(
        IReadOnlyList<PanelAnnotation> items,
        string? sourceUri,
        IReadOnlyList<string> allowedImageRoots)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(allowedImageRoots);

        if (items.Count == 0)
        {
            return string.Empty;
        }

        var body = string.Join('\n', items.Select(item => "图一 " + item.ToPromptFragment()));

        if (string.IsNullOrWhiteSpace(sourceUri)
            || !PanelImageSource.TryResolve(sourceUri, allowedImageRoots, out var path, out _))
        {
            return body;
        }

        return $"【编辑 {path}】\n" + body;
    }

    /// <summary>
    /// 解析画布页返回的标注数组。
    ///
    /// <para>
    /// 期望形状：<c>[{"kind":"box","x1":1,"y1":2,"x2":3,"y2":4}]</c>。
    /// 未知 kind、非整数、越界、数量超限、角点反序一律**整体拒绝**（返回 false），
    /// 而不是跳过坏项继续 —— 半份标注比没有标注更容易被误当成完整的编辑意图。
    /// </para>
    /// </summary>
    public static bool TryParse(
        string? json, out IReadOnlyList<PanelAnnotation> items, out string? error)
    {
        items = Array.Empty<PanelAnnotation>();
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "annotation payload was empty.";
            return false;
        }

        System.Text.Json.JsonDocument document;
        try
        {
            document = System.Text.Json.JsonDocument.Parse(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            error = $"annotation payload was not valid JSON: {ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                error = $"expected a JSON array, got {root.ValueKind}.";
                return false;
            }

            if (root.GetArrayLength() > MaxItems)
            {
                error = $"at most {MaxItems} annotations are accepted, got {root.GetArrayLength()}.";
                return false;
            }

            var parsed = new List<PanelAnnotation>(root.GetArrayLength());
            foreach (var element in root.EnumerateArray())
            {
                if (!TryParseOne(element, out var annotation, out error))
                {
                    items = Array.Empty<PanelAnnotation>();
                    return false;
                }

                parsed.Add(annotation);
            }

            items = parsed;
            return true;
        }
    }

    /// <summary>
    /// 解析画布页返回的**像素**标注，并在这里完成归一化。
    ///
    /// <para>
    /// 期望形状：<c>{"width":800,"height":600,"items":[{"kind":"box","x1":..,"y1":..,"x2":..,"y2":..}]}</c>。
    /// 让页面只报“鼠标在图片中的哪里”，换算全部交给 C#：归一化是唯一会“算错却看起来正常”
    /// 的环节，把两份实现（JS + C#）减成一份，漂移就不可能发生。
    /// </para>
    ///
    /// <para>
    /// 像素越界（用户拖到图片外面一点点）是常态，由 <see cref="FromPixels"/> 夹到边上；
    /// 但图片尺寸必须为正有限数，否则换算会变成 NaN。
    /// </para>
    /// </summary>
    public static bool TryParsePixels(
        string? json, out IReadOnlyList<PanelAnnotation> items, out string? error)
    {
        if (!TryParseCanvas(json, out var payload, out error))
        {
            items = Array.Empty<PanelAnnotation>();
            return false;
        }

        items = payload.Items;
        return true;
    }

    /// <summary>
    /// 解析**整份画布载荷**：除了标注，还包括标注对象是哪张图。
    ///
    /// <para>
    /// 为什么要带 <c>src</c>：如果只插「图一 &lt;bbox&gt;…」，模型和你都得靠对话上下文
    /// 猜「图一」是哪张图 —— 会话一长就丢。带上来源后，改动作可以脱离上下文独立成立。
    /// </para>
    ///
    /// <para>
    /// <c>src</c> 可以是 <c>pudding-image://…</c>、<c>data:</c> 或任意字符串（都来自页面，
    /// 不可信），这里只做**形状**解析；能不能变成真实磁盘路径由调用方用
    /// <see cref="PanelImageSource.TryResolve"/> 判定。
    /// </para>
    /// </summary>
    public static bool TryParseCanvas(
        string? json, out PanelCanvasPayload payload, out string? error)
    {
        payload = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "annotation payload was empty.";
            return false;
        }

        System.Text.Json.JsonDocument document;
        try
        {
            document = System.Text.Json.JsonDocument.Parse(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            error = $"annotation payload was not valid JSON: {ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                error = $"expected a JSON object, got {root.ValueKind}.";
                return false;
            }

            if (!TryReadPositiveFinite(root, "width", out var width)
                || !TryReadPositiveFinite(root, "height", out var height))
            {
                error = "payload needs positive finite 'width' and 'height'.";
                return false;
            }

            if (!root.TryGetProperty("items", out var itemsElement)
                || itemsElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                error = "payload needs an 'items' array.";
                return false;
            }

            if (itemsElement.GetArrayLength() > MaxItems)
            {
                error = $"at most {MaxItems} annotations are accepted, got {itemsElement.GetArrayLength()}.";
                return false;
            }

            var parsed = new List<PanelAnnotation>(itemsElement.GetArrayLength());
            foreach (var element in itemsElement.EnumerateArray())
            {
                if (!TryParsePixelsOne(element, width, height, out var annotation, out error))
                {
                    return false;
                }

                parsed.Add(annotation);
            }

            var source = ReadString(root, "src");
            payload = new PanelCanvasPayload(
                string.IsNullOrWhiteSpace(source) ? null : source.Trim(),
                width,
                height,
                parsed,
                ReadOptionalPositiveFinite(root, "naturalWidth"),
                ReadOptionalPositiveFinite(root, "naturalHeight"));
            return true;
        }
    }

    /// <summary>
    /// 读一个**可选**的正有限数；缺失或非法一律返回 0。
    ///
    /// <para>
    /// 与 <c>width</c>/<c>height</c> 的严格校验相反：后者是坐标换算的分母，错了一定要报错；
    /// 而自然尺寸只用于「记录这张图多大」，缺失时回退到显示尺寸即可，
    /// 为一个辅助字段拒绝整份载荷会让标注白白丢掉。
    /// </para>
    /// </summary>
    private static double ReadOptionalPositiveFinite(
        System.Text.Json.JsonElement root, string name)
        => TryReadPositiveFinite(root, name, out var value) ? value : 0;

    private static bool TryParsePixelsOne(
        System.Text.Json.JsonElement element, double width, double height,
        out PanelAnnotation annotation, out string? error)
    {
        annotation = null!;
        error = null;

        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            error = $"annotation must be an object, got {element.ValueKind}.";
            return false;
        }

        var kind = ReadString(element, "kind");
        if (string.Equals(kind, "box", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadFinite(element, "x1", out var x1)
                || !TryReadFinite(element, "y1", out var y1)
                || !TryReadFinite(element, "x2", out var x2)
                || !TryReadFinite(element, "y2", out var y2))
            {
                error = "box annotation needs finite x1,y1,x2,y2.";
                return false;
            }

            annotation = FromPixels(x1, y1, x2, y2, width, height);
            return true;
        }

        if (string.Equals(kind, "point", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadFinite(element, "x", out var x) || !TryReadFinite(element, "y", out var y))
            {
                error = "point annotation needs finite x,y.";
                return false;
            }

            annotation = PointFromPixels(x, y, width, height);
            return true;
        }

        error = $"unknown annotation kind '{kind}'.";
        return false;
    }

    private static bool TryReadPositiveFinite(
        System.Text.Json.JsonElement element, string name, out double value)
    {
        value = 0;
        if (!TryReadFinite(element, name, out var raw) || raw <= 0)
        {
            return false;
        }

        value = raw;
        return true;
    }

    private static bool TryReadFinite(
        System.Text.Json.JsonElement element, string name, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind != System.Text.Json.JsonValueKind.Number
            || !property.TryGetDouble(out var raw)
            || double.IsNaN(raw)
            || double.IsInfinity(raw))
        {
            return false;
        }

        value = raw;
        return true;
    }

    private static bool TryParseOne(
        System.Text.Json.JsonElement element,
        out PanelAnnotation annotation,
        out string? error)
    {
        annotation = null!;
        error = null;

        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            error = $"annotation must be an object, got {element.ValueKind}.";
            return false;
        }

        var kindText = ReadString(element, "kind");
        if (string.IsNullOrEmpty(kindText))
        {
            error = "annotation is missing 'kind'.";
            return false;
        }

        if (string.Equals(kindText, "box", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadCoordinate(element, "x1", out var x1)
                || !TryReadCoordinate(element, "y1", out var y1)
                || !TryReadCoordinate(element, "x2", out var x2)
                || !TryReadCoordinate(element, "y2", out var y2))
            {
                error = "box annotation needs integer x1,y1,x2,y2 within 0..999.";
                return false;
            }

            if (x1 > x2 || y1 > y2)
            {
                error = "box corners must be ordered: x1<=x2 and y1<=y2.";
                return false;
            }

            annotation = new PanelAnnotation(PanelAnnotationKind.Box, x1, y1, x2, y2);
            return true;
        }

        if (string.Equals(kindText, "point", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadCoordinate(element, "x", out var x) || !TryReadCoordinate(element, "y", out var y))
            {
                error = "point annotation needs integer x,y within 0..999.";
                return false;
            }

            annotation = new PanelAnnotation(PanelAnnotationKind.Point, x, y, 0, 0);
            return true;
        }

        error = $"unknown annotation kind '{kindText}'.";
        return false;
    }

    private static bool TryReadCoordinate(System.Text.Json.JsonElement element, string name, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind != System.Text.Json.JsonValueKind.Number
            || !property.TryGetInt32(out var raw))
        {
            return false;
        }

        if (raw < 0 || raw > MaxCoordinate)
        {
            return false;
        }

        value = raw;
        return true;
    }

    private static string? ReadString(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
            && property.ValueKind == System.Text.Json.JsonValueKind.String
                ? property.GetString()
                : null;

    private static void RequireInRange(int value, string parameterName)
    {
        if (value < 0 || value > MaxCoordinate)
        {
            throw new ArgumentOutOfRangeException(
                parameterName, $"Coordinate must be within 0..{MaxCoordinate}, got {value}.");
        }
    }
}
