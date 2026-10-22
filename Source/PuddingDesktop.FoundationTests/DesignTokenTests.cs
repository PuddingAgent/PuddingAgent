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

    [Fact]
    public void GeneratedDictionary_IsWellFormedAndCoversEveryToken()
    {
        var document = XDocument.Parse(DesignTokenXaml.Write());
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = document.Root!.Elements()
            .Select(element => element.Attribute(x + "Key")?.Value)
            .Where(key => key is not null)
            .ToArray();

        Assert.Equal(
            DesignTokens.FontSizes.Select(token => token.Name)
                .Concat(DesignTokens.CornerRadii.Select(token => token.Name))
                .OrderBy(name => name, StringComparer.Ordinal),
            keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void GeneratedDictionary_IsDeterministicAndPassesLint()
    {
        var first = DesignTokenXaml.Write();
        Assert.Equal(first, DesignTokenXaml.Write());
        Assert.Empty(XamlLiteralLint.Scan(first));
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

        Assert.Equal(65, findings.Count(finding => finding.Rule == XamlLiteralLint.RawFontSizeRule));
        Assert.Equal(20, findings.Count(finding => finding.Rule == XamlLiteralLint.RawCornerRadiusRule));
        Assert.Equal(9, findings.Count(finding => finding.Rule == XamlLiteralLint.RawColorRule));
    }

    [Fact]
    public void AppXaml_HasNoRawUsageLiterals()
    {
        var root = FindRepositoryRoot();
        Assert.NotNull(root);

        var findings = XamlLiteralLint.ScanFile(
            Path.Combine(root, "Source", "PuddingDesktop", "App.xaml"));

        Assert.Empty(findings);
    }

    /// <summary>从测试输出目录向上找含主壳层 XAML 的仓库根；找不到要显式失败，不静默跳过。</summary>
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
