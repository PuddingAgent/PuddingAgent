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
