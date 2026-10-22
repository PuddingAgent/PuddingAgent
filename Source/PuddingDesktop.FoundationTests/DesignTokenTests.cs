using System.Globalization;
using System.Xml.Linq;
using PuddingDesktop.Foundation.Design;

namespace PuddingDesktop.FoundationTests;

/// <summary>
/// 令牌层与 lint 的契约测试。
/// 这里的每个"必须报"用例都是刻意喂违规样本——如果 lint 退化成永远返回空，它们会立刻红。
/// </summary>
public sealed class DesignTokenTests
{
    [Fact]
    public void FontSizes_AreUniqueNamedAndAscending()
    {
        Assert.NotEmpty(DesignTokens.FontSizes);
        Assert.Equal(DesignTokens.FontSizes.Count,
            DesignTokens.FontSizes.Select(token => token.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(DesignTokens.FontSizes.OrderBy(token => token.Size).Select(token => token.Name),
            DesignTokens.FontSizes.Select(token => token.Name));
        Assert.All(DesignTokens.FontSizes, token => Assert.False(string.IsNullOrWhiteSpace(token.Usage)));
    }

    [Fact]
    public void CornerRadii_AreUniqueNamedAndAscending()
    {
        Assert.NotEmpty(DesignTokens.CornerRadii);
        Assert.Equal(DesignTokens.CornerRadii.OrderBy(token => token.Radius).Select(token => token.Name),
            DesignTokens.CornerRadii.Select(token => token.Name));
        Assert.All(DesignTokens.CornerRadii, token => Assert.False(string.IsNullOrWhiteSpace(token.Usage)));
    }

    /// <summary>
    /// 迁移完备性：MainWindow.xaml 里实际出现过的 13 档字号必须全部有归宿。
    /// 少一档，迁移脚本就会留下裸字面量。
    /// </summary>
    [Theory]
    [InlineData(8, DesignTokens.FontSizeMicroName)]
    [InlineData(9, DesignTokens.FontSizeMicroName)]
    [InlineData(10, DesignTokens.FontSizeMicroName)]
    [InlineData(11, DesignTokens.FontSizeMicroName)]
    [InlineData(12, DesignTokens.FontSizeCaptionName)]
    [InlineData(13, DesignTokens.FontSizeCaptionName)]
    [InlineData(15, DesignTokens.FontSizeBodyName)]
    [InlineData(16, DesignTokens.FontSizeBodyName)]
    [InlineData(18, DesignTokens.FontSizeSubtitleName)]
    [InlineData(19, DesignTokens.FontSizeSubtitleName)]
    [InlineData(21, DesignTokens.FontSizeSubtitleName)]
    [InlineData(24, DesignTokens.FontSizeTitleName)]
    [InlineData(28, DesignTokens.FontSizeDisplayName)]
    public void MapFontSize_CoversEveryLiteralObservedInMainWindow(double literal, string expected)
    {
        var mapped = DesignTokens.MapFontSize(literal);
        Assert.Equal(expected, mapped);
        Assert.NotNull(DesignTokens.FindFontSize(mapped));
    }

    [Fact]
    public void MapFontSize_AlwaysLandsOnADefinedTier()
    {
        for (var literal = 6.0; literal <= 40.0; literal += 0.5)
        {
            Assert.NotNull(DesignTokens.FindFontSize(DesignTokens.MapFontSize(literal)));
        }
    }

    [Fact]
    public void MapCornerRadius_CoversEveryLiteralObservedInMainWindow()
    {
        Assert.Equal(DesignTokens.RadiusControlName, DesignTokens.MapCornerRadius(8));
        Assert.Equal(DesignTokens.RadiusCardName, DesignTokens.MapCornerRadius(10));
        Assert.Equal(DesignTokens.RadiusCardName, DesignTokens.MapCornerRadius(15));
        Assert.Equal(DesignTokens.RadiusCardName, DesignTokens.MapCornerRadius(16));
    }

    [Fact]
    public void Lint_FlagsRawFontSize()
    {
        var findings = XamlLiteralLint.Scan("""<TextBlock FontSize="13" Text="x" />""");

        var finding = Assert.Single(findings);
        Assert.Equal(XamlLiteralLint.RawFontSizeRule, finding.Rule);
        Assert.Equal("FontSize", finding.Attribute);
        Assert.Equal("13", finding.Value);
        Assert.Contains(DesignTokens.FontSizeCaptionName, finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lint_AcceptsStaticAndThemeResourceFontSize()
    {
        Assert.Empty(XamlLiteralLint.Scan("""<TextBlock FontSize="{StaticResource FontSizeBody}" />"""));
        Assert.Empty(XamlLiteralLint.Scan("""<TextBlock FontSize="{ThemeResource BodyTextBlockFontSize}" />"""));
    }

    [Fact]
    public void Lint_FlagsRawCornerRadius_IncludingMultiCornerForm()
    {
        var findings = XamlLiteralLint.Scan("""
            <Border CornerRadius="8" />
            <Border CornerRadius="0,12,0,12" />
            """);

        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding => Assert.Equal(XamlLiteralLint.RawCornerRadiusRule, finding.Rule));
        Assert.Contains(DesignTokens.RadiusControlName, findings[0].Message, StringComparison.Ordinal);
        Assert.Contains(DesignTokens.RadiusCardName, findings[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lint_FlagsRawColorOnUsageAttributes()
    {
        var findings = XamlLiteralLint.Scan("""<Border Background="#FF00AA" />""");

        var finding = Assert.Single(findings);
        Assert.Equal(XamlLiteralLint.RawColorRule, finding.Rule);
        Assert.Equal("Background", finding.Attribute);
    }

    /// <summary>
    /// SolidColorBrush 的 Color 是画刷的定义处，允许写颜色；否则令牌文件自己都过不了 lint。
    /// </summary>
    [Fact]
    public void Lint_AllowsColorInsideBrushDefinition()
    {
        Assert.Empty(XamlLiteralLint.Scan("""<SolidColorBrush x:Key="CardBackground" Color="#BFFFFFFF" />"""));
    }

    [Fact]
    public void Lint_IgnoresAutoStarAndTransparent()
    {
        Assert.Empty(XamlLiteralLint.Scan("""
            <ColumnDefinition Width="Auto" />
            <RowDefinition Height="*" />
            <Grid Background="Transparent" />
            """));
    }

    [Fact]
    public void Lint_ReportsOneBasedLineNumbers()
    {
        var findings = XamlLiteralLint.Scan("""
            <Grid>
              <TextBlock />
              <TextBlock FontSize="12" />
            </Grid>
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(3, finding.Line);
    }

    /// <summary>
    /// WinUI 里绝大多数视觉值住在样式 Setter 里：<c>&lt;Setter Property="FontSize" Value="13"/&gt;</c>。
    /// 只扫元素属性会漏掉它们——实测 MainWindow.xaml 就漏了 13 处字号、13 处圆角，门禁等于有一半是洞。
    /// </summary>
    [Fact]
    public void Lint_FlagsRawFontSizeInsideAStyleSetter()
    {
        var findings = XamlLiteralLint.Scan("""
            <Style x:Key="ToolRoundButtonStyle" TargetType="Button">
             <Setter Property="Width" Value="34"/>
             <Setter Property="FontSize" Value="13"/>
            </Style>
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(XamlLiteralLint.RawFontSizeRule, finding.Rule);
        Assert.Equal("FontSize", finding.Attribute);
        Assert.Equal("13", finding.Value);
        Assert.Equal(3, finding.Line);
        Assert.Contains(DesignTokens.FontSizeCaptionName, finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lint_FlagsRawCornerRadiusInsideAStyleSetter_RegardlessOfAttributeOrder()
    {
        var findings = XamlLiteralLint.Scan("""
            <Style x:Key="Card" TargetType="Border">
             <Setter Value="12" Property="CornerRadius"/>
            </Style>
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(XamlLiteralLint.RawCornerRadiusRule, finding.Rule);
        Assert.Equal("CornerRadius", finding.Attribute);
        Assert.Equal("12", finding.Value);
    }

    [Fact]
    public void Lint_AcceptsTokenReferenceInsideAStyleSetter()
    {
        Assert.Empty(XamlLiteralLint.Scan("""
            <Style x:Key="Card" TargetType="Border">
             <Setter Property="CornerRadius" Value="{StaticResource RadiusCard}"/>
             <Setter Property="Background" Value="{ThemeResource CardBackgroundFillColorDefaultBrush}"/>
            </Style>
            """));
    }

    [Fact]
    public void Lint_IgnoresValuesThatAreNotVisualTokens()
    {
        Assert.Empty(XamlLiteralLint.Scan("""
            <Border Width="11" MinWidth="12" Height="8" Margin="12" Padding="8" Opacity="0.8"/>
            """));
    }
}

/// <summary>
/// 现状基线（characterization test）：把主壳层的裸字面量数量钉死。
///
/// 每个数字都必须随迁移**单调下调**，全部归零后这三个断言改为 0，门禁即成立。
/// 如果有人新写一个 <c>FontSize="14"</c>，数量上升，这个测试立刻红——
/// 也就是说它不仅记录历史，还从今天起挡住了新的散值。
/// </summary>
public sealed class DesktopXamlBaselineTests
{
    [Fact]
    public void MainWindowXaml_RawVisualLiterals_MatchRecordedBaseline()
    {
        var root = FindRepositoryRoot();
        Assert.NotNull(root);

        var findings = XamlLiteralLint.ScanFile(
            Path.Combine(root, "Source", "PuddingDesktop", "MainWindow.xaml"));

        Assert.Equal(21, findings.Count(finding => finding.Rule == XamlLiteralLint.RawFontSizeRule));
        Assert.Equal(16, findings.Count(finding => finding.Rule == XamlLiteralLint.RawCornerRadiusRule));
        Assert.Equal(9, findings.Count(finding => finding.Rule == XamlLiteralLint.RawColorRule));
    }

    [Fact]
    public void AppXaml_RawVisualLiterals_MatchRecordedBaseline()
    {
        var root = FindRepositoryRoot();
        Assert.NotNull(root);

        var findings = XamlLiteralLint.ScanFile(
            Path.Combine(root, "Source", "PuddingDesktop", "App.xaml"));

        // App.xaml 只允许有这一个裸值（ContentDialog 的 CornerRadius 样式 Setter）。
        // 它是 Stage 3 把令牌提升到 App 级资源字典时要迁掉的最后一处；在此之前不许再新增。
        Assert.Equal(0, findings.Count(finding => finding.Rule == XamlLiteralLint.RawFontSizeRule));
        Assert.Equal(1, findings.Count(finding => finding.Rule == XamlLiteralLint.RawCornerRadiusRule));
        Assert.Equal(0, findings.Count(finding => finding.Rule == XamlLiteralLint.RawColorRule));
    }

    /// <summary>
    /// 令牌数值的真源在 Foundation（<see cref="DesignTokens"/>），XAML 里的那份是拷贝。
    /// 这个测试就是拷贝的对齐线：侧改了数值而另一侧没跟上，立刻红。
    /// </summary>
    [Fact]
    public void MainWindowXaml_DeclaresEveryDesignTokenWithMatchingValue()
    {
        var text = ReadMainWindow();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var declared = XDocument.Parse(text).Descendants()
            .Select(element => (Key: element.Attribute(x + "Key")?.Value, element.Value))
            .Where(entry => entry.Key is not null
                && (entry.Key.StartsWith("FontSize", StringComparison.Ordinal)
                    || entry.Key.StartsWith("Radius", StringComparison.Ordinal)))
            .ToDictionary(entry => entry.Key!, entry => entry.Value, StringComparer.Ordinal);

        var expectedNames = DesignTokens.FontSizes.Select(token => token.Name)
            .Concat(DesignTokens.CornerRadii.Select(token => token.Name))
            .OrderBy(name => name, StringComparer.Ordinal);
        Assert.Equal(expectedNames, declared.Keys.OrderBy(key => key, StringComparer.Ordinal));

        foreach (var token in DesignTokens.FontSizes)
        {
            Assert.True(declared.ContainsKey(token.Name), $"MainWindow.xaml 没有声明字号令牌 {token.Name}");
            Assert.Equal(token.Size, ParseInvariant(declared[token.Name]), 3);
        }

        foreach (var token in DesignTokens.CornerRadii)
        {
            Assert.True(declared.ContainsKey(token.Name), $"MainWindow.xaml 没有声明圆角令牌 {token.Name}");
            Assert.Equal(token.Radius, ParseInvariant(declared[token.Name]), 3);
        }
    }

    /// <summary>
    /// 剩余裸值是 Stage 3（重定值）的待办清单：机械替换只消化了正好等于令牌值的那批，
    /// 剩下的必须逐个决定「向上/向下收口」而不是继续漂。这里把清单钉死，
    /// 多一个裸值、少一处残留、或有人把某一档静默改小，都会红。
    /// </summary>
    [Fact]
    public void MainWindowXaml_ResidualLiterals_MatchDocumentedDelta()
    {
        var findings = XamlLiteralLint.Scan(ReadMainWindow());

        var residualFontSizes = findings
            .Where(finding => finding.Rule == XamlLiteralLint.RawFontSizeRule)
            .Select(finding => ParseInvariant(finding.Value))
            .OrderBy(value => value)
            .ToArray();
        Assert.Equal(
            new[]
            {
                8d, 8d, 9d, 9d, 9d, 10d, 10d, 10d, 13d, 13d, 13d, 13d, 13d, 13d, 13d, 13d, 14d, 14d, 16d, 19d, 21d
            },
            residualFontSizes);

        var residualRadii = findings
            .Where(finding => finding.Rule == XamlLiteralLint.RawCornerRadiusRule)
            .Select(finding => ParseInvariant(finding.Value))
            .OrderBy(value => value)
            .ToArray();
        Assert.Equal(
            new[] { 4.5, 4.5, 6, 9, 9, 9, 10, 10, 10, 10, 15, 16, 17, 17, 17, 17 },
            residualRadii);
    }

    /// <summary>
    /// lint 是纯文本扫描，它看不见 XML 是否还被结构地解析得动：令牌引用写错名字
    /// （<c>{StaticResource FontSizeBodyy}</c>）或少了右花括号，lint 依然全绿，但应用会在启动时崩。
    /// 这里补上两道：文件必须能被 XML 解析，且每个令牌引用都指向已声明的 key。
    /// </summary>
    [Fact]
    public void MainWindowXaml_ParsesAndEveryTokenReferenceResolves()
    {
        var text = ReadMainWindow();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Parse(text);
        var declaredKeys = document.Descendants()
            .Select(element => element.Attribute(x + "Key")?.Value)
            .Where(key => key is not null)
            .ToHashSet(StringComparer.Ordinal);

        var references = document.Descendants()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName is "FontSize" or "CornerRadius")
            .Select(attribute => attribute.Value)
            .Where(value => value.StartsWith("{StaticResource ", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(references);
        foreach (var reference in references)
        {
            var key = reference["{StaticResource ".Length..].TrimEnd('}').Trim();
            Assert.True(declaredKeys.Contains(key), $"未声明的令牌引用：{reference}");
        }
    }

    private static string ReadMainWindow()
    {
        var root = FindRepositoryRoot();
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root, "Source", "PuddingDesktop", "MainWindow.xaml"));
    }

    private static double ParseInvariant(string value) =>
        double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);


    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Source", "PuddingDesktop", "MainWindow.xaml")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
