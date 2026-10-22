using PuddingDesktop.Foundation.Design;

namespace PuddingDesktop.FoundationTests;

/// <summary>
/// 主题字典结构门禁的契约测试。
///
/// 三件事各有一组"必须报"用例（喂违规样本，保证审计器不会退化成永远返回空），
/// 外加两组针对真实 <c>MainWindow.xaml</c> 的断言：
/// <list type="number">
/// <item><c>Default</c> 与 <c>Light</c> 的画刷**键集**必须一致（取值按主题不同才是对的）；</item>
/// <item>每个 <c>{ThemeResource}</c> 都能在本文件 ∪ App.xaml ∪ 平台白名单里找到定义。</item>
/// </list>
/// 第 2 条是补一个真实的口子：WinUI 在**运行期**才解析 ThemeResource，拼错名字编译通过、启动才崩。
/// </summary>
public sealed class ThemeResourceAuditTests
{
    private const string SyntheticXaml = """
        <ResourceDictionary
            xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <ResourceDictionary.ThemeDictionaries>
                <ResourceDictionary x:Key="Default">
                    <SolidColorBrush x:Key="SharedBrush" Color="#111111" />
                    <SolidColorBrush x:Key="OnlyDefaultBrush" Color="#222222" />
                </ResourceDictionary>
                <ResourceDictionary x:Key="Light">
                    <SolidColorBrush x:Key="SharedBrush" Color="#EEEEEE" />
                    <SolidColorBrush x:Key="OnlyLightBrush" Color="#DDDDDD" />
                </ResourceDictionary>
                <ResourceDictionary x:Key="HighContrast">
                    <SolidColorBrush x:Key="SharedBrush" Color="#000000" />
                </ResourceDictionary>
            </ResourceDictionary.ThemeDictionaries>
            <TextBlock Foreground="{ThemeResource SharedBrush}" />
            <TextBlock Foreground="{ThemeResource CardBackgroundFillColorDefaultBrush}" />
            <TextBlock Foreground="{ThemeResource ExternalBrush}" />
            <TextBlock Foreground="{ThemeResource MissingBrush}" />
        </ResourceDictionary>
        """;

    [Fact]
    public void FindBrushKeySetDrift_ReportsKeysMissingOnOneSideOnly()
    {
        var colors = ThemeResourceAudit.ReadThemeBrushColors(SyntheticXaml);

        var problems = ThemeResourceAudit.FindBrushKeySetDrift(colors);

        Assert.Contains(problems, problem => problem.StartsWith("OnlyDefaultBrush:", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.StartsWith("OnlyLightBrush:", StringComparison.Ordinal));
    }

    [Fact]
    public void FindBrushKeySetDrift_AllowsDifferentValuesForTheSameKey()
    {
        // 同一令牌在 Default（深色）与 Light（浅色）下取值不同是**正常**的，不算漂移。
        // 这条用例锁住语义：审计器只比键集，不能退化成比值。
        var colors = ThemeResourceAudit.ReadThemeBrushColors(SyntheticXaml);

        var problems = ThemeResourceAudit.FindBrushKeySetDrift(colors);

        Assert.DoesNotContain(problems, problem => problem.StartsWith("SharedBrush:", StringComparison.Ordinal));
    }

    [Fact]
    public void FindBrushKeySetDrift_RequiresDefaultAndLight()
    {
        var problems = ThemeResourceAudit.FindBrushKeySetDrift(
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["Default"] = new Dictionary<string, string>(StringComparer.Ordinal),
            });

        Assert.Single(problems);
        Assert.Contains("Default 与 Light", problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void FindUnresolvedReferences_AcceptsFileKeysPlatformKeysAndExternalKeys()
    {
        var unresolved = ThemeResourceAudit.FindUnresolvedReferences(SyntheticXaml, ["ExternalBrush"]);

        var missing = Assert.Single(unresolved);
        Assert.Equal("MissingBrush", missing.Key);
        Assert.True(missing.Line > 0, "引用必须带 1 基行号，否则红了的报告定位不到位置");
    }

    [Fact]
    public void FindUnresolvedReferences_AcceptsRegisteredPlatformKey()
    {
        // 反证平台白名单真的在起作用：样本里那个平台键必须被当作已定义。
        const string platformKey = "CardBackgroundFillColorDefaultBrush";
        Assert.Contains(platformKey, ThemeResourceAudit.PlatformResources);

        var unresolved = ThemeResourceAudit.FindUnresolvedReferences(SyntheticXaml, ["ExternalBrush"]);
        Assert.DoesNotContain(unresolved, reference => reference.Key == platformKey);
    }

    [Fact]
    public void ReadReferences_IgnoresMixedExpressions()
    {
        const string xaml = """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <TextBlock Text="{x:Bind ViewModel.Title}" Foreground="{ThemeResource KnownBrush}" />
            </Grid>
            """;

        var references = ThemeResourceAudit.ReadReferences(xaml);

        var reference = Assert.Single(references);
        Assert.Equal("KnownBrush", reference.Key);
    }

    [Fact]
    public void MainWindowXaml_DefaultAndLightThemeBrushKeySetsMatch()
    {
        var colors = ThemeResourceAudit.ReadThemeBrushColors(ReadShellFile("MainWindow.xaml"));

        var problems = ThemeResourceAudit.FindBrushKeySetDrift(colors);

        Assert.Empty(problems);
    }

    [Fact]
    public void MainWindowXaml_EveryThemeResourceReferenceResolves()
    {
        var mainWindow = ReadShellFile("MainWindow.xaml");
        var appKeys = ThemeResourceAudit.ReadDeclaredKeys(ReadShellFile("App.xaml"));

        var unresolved = ThemeResourceAudit.FindUnresolvedReferences(mainWindow, appKeys);

        Assert.NotEmpty(ThemeResourceAudit.ReadReferences(mainWindow));
        Assert.Empty(unresolved.Select(reference => $"{reference.Key}@{reference.Line}"));
    }

    [Fact]
    public void AppXaml_DefaultAndLightThemeBrushKeySetsMatch()
    {
        var colors = ThemeResourceAudit.ReadThemeBrushColors(ReadShellFile("App.xaml"));

        var problems = ThemeResourceAudit.FindBrushKeySetDrift(colors);

        Assert.Empty(problems);
    }

    private static string ReadShellFile(string fileName)
    {
        var root = FindRepositoryRoot();
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root, "Source", "PuddingDesktop", fileName));
    }

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
