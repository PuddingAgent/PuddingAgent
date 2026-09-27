using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

public sealed class SettingsCatalogTests
{
    [Fact]
    public void EveryMigrationCardHasStableIdentityAndHandoffSource()
    {
        var categories = SettingsCatalog.Categories;
        Assert.NotEmpty(categories);
        Assert.Equal(categories.Count, categories.Select(c => c.Id).Distinct().Count());
        var cards = categories.SelectMany(c => c.Tabs).SelectMany(t => t.Cards).ToArray();
        Assert.Equal(cards.Length, cards.Select(c => c.Id).Distinct().Count());
        foreach (var category in categories)
        {
            Assert.NotEmpty(category.Tabs);
            Assert.Equal(category.Tabs.Count, category.Tabs.Select(t => t.Id).Distinct().Count());
            foreach (var tab in category.Tabs) Assert.NotEmpty(tab.Cards);
        }
        foreach (var card in cards)
        {
            Assert.NotEmpty(card.Fields);
            Assert.NotEmpty(card.Source);
            Assert.StartsWith("DS-", card.Task);
            Assert.Contains(card.Status, new[] { "待迁移", "已接入", "部分已有", "已有入口" });
        }
    }

    [Fact]
    public void FieldSearchFindsNestedTabAndDoesNotMutateFullCatalog()
    {
        var result = Assert.Single(SettingsCatalog.Search("  MAXINPUTTOKENS  "));
        Assert.Equal("models", result.Id);
        Assert.Equal("model-limits", Assert.Single(Assert.Single(result.Tabs).Cards).Id);
        Assert.Equal(3, SettingsCatalog.Categories.Single(c => c.Id == "models").Tabs.Count);
        Assert.Empty(SettingsCatalog.Search("不存在的设置-xyz"));
        Assert.Same(SettingsCatalog.Categories, SettingsCatalog.Search(" "));
    }

    [Fact]
    public void CategoryAndTaskSearchKeepExpectedScope()
    {
        var voice = Assert.Single(SettingsCatalog.Search("语音模型"));
        Assert.Equal("voice", voice.Id);
        Assert.Equal(3, voice.Tabs.Count);
        Assert.All(SettingsCatalog.Search("DS-04").SelectMany(c => c.Tabs).SelectMany(t => t.Cards),
            card => Assert.Equal("DS-04", card.Task));
    }
}
