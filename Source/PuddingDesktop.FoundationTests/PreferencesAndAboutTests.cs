using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-01: real language list, real build version, and an external help entry that stays labelled.</summary>
public sealed class PreferencesAndAboutTests
{
    [Fact]
    public void OnlyShippedLanguagesAreOffered()
    {
        var language = Assert.Single(DesktopLanguages.Supported);
        Assert.Equal("zh-CN", language.Tag);
        Assert.Equal("简体中文", language.DisplayName);
        Assert.Equal("zh-CN", DesktopLanguages.Default);
        Assert.True(DesktopLanguages.RequiresRestart);
    }

    [Theory]
    [InlineData("zh-CN", true)]
    [InlineData("ZH-cn", true)]
    [InlineData("en-US", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void UnsupportedLanguageIsReportedAndNormalized(string? tag, bool supported)
    {
        Assert.Equal(supported, DesktopLanguages.IsSupported(tag));
        Assert.Equal("zh-CN", DesktopLanguages.Normalize(tag));
        Assert.Equal("zh-CN", DesktopLanguages.Describe(tag!).Tag);
    }

    [Fact]
    public void PreferencesNormalizeLanguageAppearanceTogether()
    {
        var normalized = new DesktopPreferences(new ShellLayout(), "Dark", "Acrylic", "en-US").Normalize();
        Assert.Equal("zh-CN", normalized.Language);
        Assert.Equal("Dark", normalized.Theme);
        Assert.Equal("Acrylic", normalized.Material);
        Assert.Equal("Light", new DesktopPreferences(new ShellLayout(), "Neon", "Marble", "zh-CN").Normalize().Theme);
        Assert.Equal("Mica", DesktopPreferences.Default.Material);
    }

    [Fact]
    public async Task LanguageSurvivesReloadLikeTheOtherAppearancePreferences()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-preferences-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DesktopPreferencesStore(root);
            await store.SaveAsync(new(new(), "Dark", "MicaAlt", "zh-CN"));
            var reloaded = await store.LoadAsync();
            Assert.Null(reloaded.Warning);
            Assert.Equal("zh-CN", reloaded.Preferences.Language);
            Assert.Equal("Dark", reloaded.Preferences.Theme);
            Assert.EndsWith("desktop.preferences.json", store.FilePath, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void BuildVersionIsReadFromTheAssembly_NotHardCoded()
    {
        Assert.Equal("1.2.3", DesktopProductInfo.NormalizeVersion("1.2.3+9f2c1a", "1.2.0.0"));
        Assert.Equal("1.2.0.0", DesktopProductInfo.NormalizeVersion(null, "1.2.0.0"));
        Assert.Equal("1.2.0.0", DesktopProductInfo.NormalizeVersion("   ", "1.2.0.0"));
        Assert.Equal(DesktopProductInfo.UnknownVersion, DesktopProductInfo.NormalizeVersion(null, null));
        Assert.DoesNotContain(DesktopProductInfo.UnknownVersion, DesktopProductInfo.NormalizeVersion("1.2.3", null));
    }

    [Fact]
    public void HelpEntryIsAnAbsoluteExternalLink()
    {
        Assert.True(DesktopProductInfo.IsExternalLink(DesktopProductInfo.HelpUrl));
        Assert.StartsWith("https://", DesktopProductInfo.HelpUrl, StringComparison.Ordinal);
        Assert.False(DesktopProductInfo.IsExternalLink("Docs/help.md"));
        Assert.False(DesktopProductInfo.IsExternalLink(null));
    }

    [Fact]
    public void AboutLocationsAreReadOnlyDescriptions()
    {
        var text = DesktopProductInfo.DescribeLocations(@"C:\state", @"D:\data");
        Assert.Contains("desktop.preferences.json", text, StringComparison.Ordinal);
        Assert.Contains("desktop.kernel.json", text, StringComparison.Ordinal);
        Assert.Contains(@"D:\data", text, StringComparison.Ordinal);
        Assert.Contains("未配置", DesktopProductInfo.DescribeLocations(@"C:\state", null), StringComparison.Ordinal);
    }
}
