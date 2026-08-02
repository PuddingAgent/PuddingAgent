using PuddingDesktop.Storage;

namespace PuddingDesktop.Tests.Storage;

public sealed class StorageCategoryCatalogTests
{
    private readonly StorageCategoryCatalog _catalog = new();

    [Theory]
    [InlineData("logs/system/pudding.log", StorageCategoryKind.Logs)]
    [InlineData("databases/pudding.db", StorageCategoryKind.DatabaseAndIndex)]
    [InlineData("fulltext-index/default/index.db", StorageCategoryKind.DatabaseAndIndex)]
    [InlineData("sessions/s1/events.jsonl", StorageCategoryKind.ConversationAndMemory)]
    [InlineData("memory/books/index.json", StorageCategoryKind.ConversationAndMemory)]
    [InlineData("browser/downloads/report.pdf", StorageCategoryKind.AssetsAndDownloads)]
    [InlineData("browser/screenshots/page.png", StorageCategoryKind.AssetsAndDownloads)]
    [InlineData("browser/workbench/user-data/state", StorageCategoryKind.Browser)]
    [InlineData("channels/douyin/runtime/webview2/Cookies", StorageCategoryKind.Browser)]
    [InlineData("channels/douyin/manifest.json", StorageCategoryKind.Configuration)]
    [InlineData("build-validation/output.dll", StorageCategoryKind.UnexpectedBuildOutput)]
    [InlineData("tmp/runtime.tmp", StorageCategoryKind.Temporary)]
    [InlineData("unknown/value.bin", StorageCategoryKind.Other)]
    public void Classify_UsesFirstMatchWithoutOverlapping(
        string relativePath,
        StorageCategoryKind expected)
    {
        Assert.Equal(expected, _catalog.Classify(relativePath).Kind);
    }

    [Fact]
    public void Definitions_ContainEachCategoryExactlyOnce()
    {
        Assert.Equal(
            Enum.GetValues<StorageCategoryKind>().Order(),
            _catalog.Definitions.Select(item => item.Kind).Order());
    }
}
