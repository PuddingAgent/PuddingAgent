using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndexTests;

/// <summary>
/// 真实语料清点实现的 R2 证据：口径必须与索引引擎一致
/// （扩展名白名单 / 排除清单（派生自 PathNoiseRules 单一真源）/ 空文件与超限文件），且**只读**。
/// </summary>
[TestClass]
public sealed class FileSystemSupplyInventoryTests
{
    [TestMethod]
    public async Task Inventory_Mirrors_The_Engine_File_Filter()
    {
        using var fixture = new TempSupplyFixture();
        var cs = fixture.Write("a.cs", "class Alpha { }");
        var md = fixture.Write("b.md", "# Doc");
        fixture.Write("image.png", "not whitelisted");
        fixture.Write(Path.Combine("bin", "noise.cs"), "class Noise { }");
        fixture.Write("empty.cs", string.Empty);

        var inventory = new FileSystemSupplyInventory(fixture.Options);

        var result = await inventory.MeasureAsync(fixture.Corpus);

        Assert.AreEqual(2, result.FileCount, "a.cs + b.md；png 非白名单、bin 被剪枝、空文件被跳过");
        Assert.AreEqual(new FileInfo(cs).Length + new FileInfo(md).Length, result.TotalBytes);
        Assert.IsTrue(result.BytesByExtension.ContainsKey(".cs"));
        Assert.IsTrue(result.BytesByExtension.ContainsKey(".md"));
        Assert.IsFalse(result.BytesByExtension.ContainsKey(".png"), "非白名单扩展名不得出现在统计里");
    }

    [TestMethod]
    public async Task Inventory_Respects_MaxFileSize_And_Excluded_File_Names()
    {
        using var fixture = new TempSupplyFixture();
        var ok = fixture.Write("ok.cs", "class Ok { }");
        fixture.Write("huge.cs", new string('x', 4096));
        fixture.Write("package-lock.json", "{ \"noise\": true }");   // PathNoiseRules.FileNames 命中，且扩展名在白名单里

        var inventory = new FileSystemSupplyInventory(fixture.Options with { MaxFileSizeBytes = 1024 });

        var result = await inventory.MeasureAsync(fixture.Corpus);

        Assert.AreEqual(1, result.FileCount, "超限文件与被排除文件名都不得计入");
        Assert.AreEqual(new FileInfo(ok).Length, result.TotalBytes);
    }

    [TestMethod]
    public async Task Inventory_Skips_Noise_Directories_And_Counts_Nested_Files()
    {
        using var fixture = new TempSupplyFixture();
        fixture.Write(Path.Combine("sub", "deeper", "c.cs"), "class Gamma { }");
        fixture.Write(Path.Combine("node_modules", "pkg", "index.js"), "// noise");
        fixture.Write(Path.Combine(".pudding", "cache", "note.md"), "noise");
        fixture.Write(Path.Combine("obj", "gen.cs"), "class Gen { }");

        var result = await new FileSystemSupplyInventory(fixture.Options).MeasureAsync(fixture.Corpus);

        Assert.AreEqual(1, result.FileCount, "只有 sub/deeper/c.cs 应当被计入");
        Assert.AreEqual(1, result.BytesByExtension.Count);
    }

    [TestMethod]
    public async Task Inventory_Keys_Extensions_Case_Insensitively()
    {
        using var fixture = new TempSupplyFixture();
        fixture.Write("A.CS", "class Upper { }");

        var result = await new FileSystemSupplyInventory(fixture.Options).MeasureAsync(fixture.Corpus);

        Assert.AreEqual(1, result.FileCount);
        // 字典的比较器是大小写不敏感的，因此「键是否小写归一」必须查实际键值，不能用 ContainsKey。
        CollectionAssert.AreEqual(new[] { ".cs" }, result.BytesByExtension.Keys.ToArray(), "扩展名统计键必须小写归一");
    }

    [TestMethod]
    public async Task Inventory_Throws_For_A_Missing_Directory()
    {
        using var fixture = new TempSupplyFixture();
        var missing = Path.Combine(fixture.Root, "missing");

        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(
            () => new FileSystemSupplyInventory(fixture.Options).MeasureAsync(missing));
    }

    [TestMethod]
    public async Task Inventory_Is_Read_Only_And_Creates_Nothing()
    {
        using var fixture = new TempSupplyFixture();
        fixture.Write("a.cs", "class Alpha { }");
        var before = SupplyTestHelpers.CaptureEntries(fixture.Corpus);

        await new FileSystemSupplyInventory(fixture.Options).MeasureAsync(fixture.Corpus);

        CollectionAssert.AreEqual(before.ToArray(), SupplyTestHelpers.CaptureEntries(fixture.Corpus).ToArray());
        Assert.IsFalse(Directory.Exists(fixture.IndexRoot), "清点不得创建索引根");
    }
}
