namespace PuddingDesktop.Foundation.Design;

/// <summary>
/// 一个字号档位。<see cref="Name"/> 就是 XAML 静态资源的 key（<c>{StaticResource FontSizeBody}</c>）。
/// </summary>
/// <param name="Name">资源 key，也是唯一的语义标识。</param>
/// <param name="Size">磅值。</param>
/// <param name="Usage">允许使用的场合——写清用途才挡得住"随手加一档"。</param>
public sealed record FontSizeToken(string Name, double Size, string Usage);

/// <summary>一个圆角档位，同样以资源 key 作为唯一标识。</summary>
/// <param name="Name">资源 key。</param>
/// <param name="Radius">圆角半径（像素）。</param>
/// <param name="Usage">允许使用的场合。</param>
public sealed record CornerToken(string Name, double Radius, string Usage);

/// <summary>
/// 界面视觉的唯一字号/圆角真源。
///
/// 设计约束（2026-10-22 裁定）：
/// 1. 档位由「角色」决定，不由「这个位置现在是多少」决定——所以是 6 档而不是现状的 13 档。
/// 2. XAML 侧只允许 <c>{StaticResource ...}</c> 引用，裸数字由 <see cref="XamlLiteralLint"/> 拒绝。
/// 3. 本类只存数值，不引用 WinUI 类型：令牌可以在任何进程里被测试。
/// </summary>
public static class DesignTokens
{
    public const string FontSizeMicroName = "FontSizeMicro";
    public const string FontSizeCaptionName = "FontSizeCaption";
    public const string FontSizeBodyName = "FontSizeBody";
    public const string FontSizeSubtitleName = "FontSizeSubtitle";
    public const string FontSizeTitleName = "FontSizeTitle";
    public const string FontSizeDisplayName = "FontSizeDisplay";

    public const string RadiusControlName = "RadiusControl";
    public const string RadiusCardName = "RadiusCard";

    /// <summary>
    /// 字号阶梯。11 是中文在 100% 缩放下仍不糊的最小档，也是现状 8/9/10/11 的收口点；
    /// 12/13 合并为 13（中文小字读不动的主因）；15/16 合并为 15；18/19/21 合并为 18。
    /// </summary>
    public static readonly IReadOnlyList<FontSizeToken> FontSizes =
    [
        new(FontSizeMicroName, 11, "状态栏、角标、版本号、极次要元信息"),
        new(FontSizeCaptionName, 13, "chip、列表第二行、辅助标签、步骤计数"),
        new(FontSizeBodyName, 15, "正文、输入框、按钮文字"),
        new(FontSizeSubtitleName, 18, "段落小标题、面板分区标题"),
        new(FontSizeTitleName, 24, "面板标题、页面标题"),
        new(FontSizeDisplayName, 28, "启动面板与空态的主标题")
    ];

    /// <summary>
    /// 圆角只有两档：控件（按钮、chip、输入框）与卡片（消息卡、面板）。
    /// 第三档开始就是随手值，视觉上分辨不出来只会让边缘收口变松。
    /// </summary>
    public static readonly IReadOnlyList<CornerToken> CornerRadii =
    [
        new(RadiusControlName, 8, "按钮、chip、输入框、图标按钮"),
        new(RadiusCardName, 12, "消息卡、面板、弹层")
    ];

    /// <summary>
    /// 把一个历史裸字号收口到档位。迁移期用它生成替换表，迁移完成后它只用于校验历史文件。
    /// 边界取两档中点，避免 13.5 这类值落空。
    /// </summary>
    public static string MapFontSize(double literal) => literal switch
    {
        <= 11.5 => FontSizeMicroName,
        <= 13.5 => FontSizeCaptionName,
        <= 16.5 => FontSizeBodyName,
        <= 21.5 => FontSizeSubtitleName,
        <= 24.5 => FontSizeTitleName,
        _ => FontSizeDisplayName
    };

    /// <summary>把一个历史裸圆角收口到档位。</summary>
    public static string MapCornerRadius(double literal) =>
        literal <= 8.5 ? RadiusControlName : RadiusCardName;

    /// <summary>按资源 key 找字号档位；找不到返回 <c>null</c>（调用方必须显式处理而不是猜）。</summary>
    public static FontSizeToken? FindFontSize(string name) =>
        FontSizes.FirstOrDefault(token => string.Equals(token.Name, name, StringComparison.Ordinal));

    /// <summary>按磅值找字号档位；用于校验"迁移表覆盖了文件里出现的每一个值"。</summary>
    public static FontSizeToken? FindFontSizeBySize(double size) =>
        FontSizes.FirstOrDefault(token => Math.Abs(token.Size - size) < 0.001);
}
