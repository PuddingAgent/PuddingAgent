namespace PuddingDesktop.FoundationTests;

using PuddingDesktop.Foundation;

/// <summary>
/// 制成品存储：落盘、回读、删除。
///
/// 这层的错误有个共同特征——**看起来保存成功了**：JSON 少一个字段、图片没复制过去、
/// 删除只删了记录没删文件。都不会抛异常，只会在几天后"我存的东西怎么打不开"。
/// 所以每条断言都落到磁盘事实上，而不是返回值。
/// </summary>
public sealed class PanelCraftStoreTests : IDisposable
{
    private readonly string _root;
    private readonly PanelCraftStore _store;

    public PanelCraftStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-crafts-" + Guid.NewGuid().ToString("N"));
        _store = new PanelCraftStore(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清不掉不算测试失败。
        }
    }

    private string CreateImage(string name = "source.jpg", string content = "fake-jpeg-bytes")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private PanelCraft SaveBasic(string? parent = null) => _store.Save(
        CreateImage(),
        "金毛幼犬",
        width: 2048,
        height: 2048,
        source: new PanelCraftSource("edit-7ced1b33", "doubao-seedream-5-0", "戴上针织帽",
            new[] { PanelAnnotation.Box(320, 180, 680, 520) }),
        parent,
        note: null,
        now: new DateTimeOffset(2026, 10, 20, 18, 50, 0, TimeSpan.FromHours(8)));

    // ── Save ───────────────────────────────────────────────────

    [Fact]
    public void Save_writes_both_the_image_and_the_record()
    {
        var craft = SaveBasic();

        Assert.True(File.Exists(Path.Combine(_store.CraftsDirectory, craft.FileName)));
        Assert.True(File.Exists(Path.Combine(_store.CraftsDirectory, craft.Id + ".json")));
    }

    [Fact]
    public void Save_records_the_real_size_and_hash_of_the_source_bytes()
    {
        var craft = SaveBasic();

        var stored = new FileInfo(_store.ResolveImagePath(craft));
        Assert.Equal(stored.Length, craft.Bytes);
        Assert.Equal(2048, craft.Width);
        Assert.Equal(2048, craft.Height);
        Assert.Equal(64, craft.Sha256.Length);
    }

    [Fact]
    public void Save_rejects_a_non_image_extension()
    {
        var text = Path.Combine(_root, "notes.txt");
        Directory.CreateDirectory(_root);
        File.WriteAllText(text, "hello");

        Assert.Throws<ArgumentException>(() => _store.Save(
            text, "x", 1, 1, null, null, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Save_rejects_a_missing_source_file()
    {
        Assert.Throws<FileNotFoundException>(() => _store.Save(
            Path.Combine(_root, "nope.jpg"), "x", 1, 1, null, null, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Save_falls_back_to_the_id_when_the_title_is_blank()
    {
        var craft = _store.Save(
            CreateImage(), "   ", 10, 10, null, null, null, DateTimeOffset.UtcNow);

        Assert.Equal(craft.Id, craft.Title);
    }

    // ── 回读 ───────────────────────────────────────────────────

    [Fact]
    public void List_round_trips_every_field()
    {
        var saved = SaveBasic(parent: "craft-01234567");

        var loaded = Assert.Single(_store.List());

        Assert.Equal(saved.Id, loaded.Id);
        Assert.Equal("金毛幼犬", loaded.Title);
        Assert.Equal(saved.Sha256, loaded.Sha256);
        Assert.Equal("craft-01234567", loaded.Parent);
        Assert.NotNull(loaded.Source);
        Assert.Equal("戴上针织帽", loaded.Source!.Prompt);
        Assert.Equal("edit-7ced1b33", loaded.Source.PanelId);
        var box = Assert.Single(loaded.Source.Annotations);
        Assert.Equal("<bbox>320 180 680 520</bbox>", box.ToPromptFragment());
    }

    [Fact]
    public void List_is_ordered_newest_first()
    {
        // 排序写反的话，用户新保存的东西会沉到底部，看起来像没保存成功。
        _store.Save(CreateImage("old.jpg"), "旧的", 1, 1, null, null, null,
            new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero));
        _store.Save(CreateImage("new.jpg", "other"), "新的", 1, 1, null, null, null,
            new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero));

        var titles = _store.List().Select(craft => craft.Title).ToArray();

        Assert.Equal(new[] { "新的", "旧的" }, titles);
    }

    [Fact]
    public void List_skips_a_corrupted_record_instead_of_losing_the_whole_library()
    {
        SaveBasic();
        File.WriteAllText(Path.Combine(_store.CraftsDirectory, "craft-badbad00.json"), "{ broken");

        // 一件坏文件不该让整个制品库消失——那会让用户以为东西全丢了。
        Assert.Single(_store.List());
    }

    [Fact]
    public void List_returns_empty_when_the_directory_does_not_exist_yet()
    {
        Assert.Empty(_store.List());
    }

    // ── 删除 ───────────────────────────────────────────────────

    [Fact]
    public void Delete_moves_the_record_and_the_image_into_trash()
    {
        var craft = SaveBasic();

        Assert.True(_store.Delete(craft.Id, DateTimeOffset.UtcNow));

        Assert.Empty(_store.List());
        Assert.False(File.Exists(Path.Combine(_store.CraftsDirectory, craft.Id + ".json")));
        Assert.False(File.Exists(_store.ResolveImagePath(craft)));

        // 不真删：误删一张改了很久的图，代价远大于占一点磁盘。
        var trash = Path.Combine(_store.CraftsDirectory, PanelCraftStore.TrashDirectoryName);
        Assert.NotEmpty(Directory.GetFiles(trash, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Delete_rejects_a_malformed_id_without_touching_the_disk()
    {
        Assert.False(_store.Delete("../evil", DateTimeOffset.UtcNow));
        Assert.False(_store.Delete(PanelCraft.IdPrefix, DateTimeOffset.UtcNow));
        Assert.False(_store.Delete(null!, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Delete_returns_false_for_an_unknown_id()
    {
        Assert.False(_store.Delete("craft-deadbeef", DateTimeOffset.UtcNow));
    }

    // ── id 形状 ────────────────────────────────────────────────

    [Theory]
    [InlineData("craft-0123abcd", true)]
    [InlineData("craft-0123ABCD", false)]   // 大写会引入大小写不敏感文件系统上的歧义
    [InlineData("craft-0123abc", false)]    // 短一位
    [InlineData("craft-0123abcde", false)]  // 长一位
    [InlineData("vision-0123abcd", false)]  // 前缀不对
    [InlineData("craft-../../x", false)]    // 路径字符
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Id_shape_is_strict_because_it_lands_in_a_file_name(string? id, bool expected)
    {
        Assert.Equal(expected, PanelCraft.IsValidId(id));
    }
}
