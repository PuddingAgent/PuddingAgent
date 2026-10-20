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

    // ── _trash 过期清理 ────────────────────────────────────────

    /// <summary>造一个指定删除时刻的 _trash 条目（与 Delete 的命名规则一致）。</summary>
    private string CreateTrashEntry(string id, DateTimeOffset deletedAt)
    {
        // 用 _store.CraftsDirectory 而不是 _root：制品目录多不多一层是 store 的实现细节，
        // 在测试里猜路径层次会让「实现正确但测试写错」看起来像功能坏了。
        var directory = Path.Combine(
            _store.CraftsDirectory, PanelCraftStore.TrashDirectoryName,
            $"{id}-{deletedAt.ToUnixTimeMilliseconds()}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "probe.json"), "{}");
        return directory;
    }

    [Fact]
    public void PruneTrash_removes_entries_past_the_retention_window()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(30);
        var path = CreateTrashEntry("craft-80c9955d", now.AddDays(-10));

        Assert.Equal(1, _store.PruneTrash(now, TimeSpan.FromDays(7)));
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void PruneTrash_keeps_entries_inside_the_retention_window()
    {
        // 用户刚删的东西必须还能捞回来 —— 清理只针对过期条目。
        var now = DateTimeOffset.UnixEpoch.AddDays(30);
        var path = CreateTrashEntry("craft-80c9955d", now.AddDays(-3));

        Assert.Equal(0, _store.PruneTrash(now, TimeSpan.FromDays(7)));
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void PruneTrash_skips_directories_it_did_not_create()
    {
        // 用户可能在 _trash 里自己放了东西。「不认识就删」比「不删」危险得多。
        var now = DateTimeOffset.UnixEpoch.AddDays(30);
        var userDirectory = Path.Combine(
            _store.CraftsDirectory, PanelCraftStore.TrashDirectoryName, "my-own-backup");
        Directory.CreateDirectory(userDirectory);

        Assert.Equal(0, _store.PruneTrash(now, TimeSpan.FromDays(7)));
        Assert.True(Directory.Exists(userDirectory));
    }

    [Fact]
    public void PruneTrash_returns_zero_when_there_is_no_trash_directory()
    {
        // 从未删过任何东西时不应该抛（那时 _trash 根本不存在）。
        Assert.Equal(0, _store.PruneTrash(DateTimeOffset.UtcNow, TimeSpan.FromDays(7)));
    }

    [Fact]
    public void PruneTrash_keeps_a_user_directory_that_only_looks_like_a_trash_entry()
    {
        // P1（审阅发现）：旧实现只校验「最后一个连字符右边是数字」，
        // 所以 my-backup-1699999999 会被当成回收站条目，连内容一起删掉。
        // 现在要求连字符左边是**完整的制品 id**。
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        var userDirectory = Path.Combine(
            _store.CraftsDirectory, PanelCraftStore.TrashDirectoryName, "my-backup-1699999999");
        Directory.CreateDirectory(userDirectory);
        var keepMe = Path.Combine(userDirectory, "keep.txt");
        File.WriteAllText(keepMe, "important");

        Assert.Equal(0, _store.PruneTrash(now, TimeSpan.FromDays(7)));
        Assert.True(Directory.Exists(userDirectory));
        Assert.True(File.Exists(keepMe));
    }

    [Fact]
    public void TryResolveImagePath_refuses_a_file_name_that_escapes_the_store()
    {
        // P1（审阅发现）：fileName 来自可被编辑的 craft.json，
        // 直接拼进路径会让 Delete 把库外文件搬进回收站。
        Assert.False(_store.TryResolveImagePath(SampleCraft("..\\outside.txt"), out _));
        Assert.False(_store.TryResolveImagePath(SampleCraft("../outside.txt"), out _));
        Assert.False(_store.TryResolveImagePath(SampleCraft("C:\\Windows\\win.ini"), out _));
        Assert.False(_store.TryResolveImagePath(SampleCraft(".."), out _));
        Assert.False(_store.TryResolveImagePath(SampleCraft(""), out _));
    }

    [Fact]
    public void TryResolveImagePath_accepts_a_plain_file_name()
    {
        Assert.True(_store.TryResolveImagePath(SampleCraft("craft-80c9955d.jpg"), out var path));
        Assert.StartsWith(
            Path.GetFullPath(_store.CraftsDirectory), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Delete_refuses_an_escaping_fileName_and_leaves_everything_in_place()
    {
        // 比只测 TryResolve 更贴近真实事故：即使走到了 Delete，
        // 库外文件不能动、craft.json 也不能被搬走（否则是半完成状态）。
        var parent = Directory.GetParent(_store.CraftsDirectory)!.FullName;
        Directory.CreateDirectory(parent);
        var outside = Path.Combine(parent, "outside.txt");
        File.WriteAllText(outside, "keep me");

        var craft = SampleCraft("..\\outside.txt");
        Directory.CreateDirectory(_store.CraftsDirectory);
        var jsonPath = Path.Combine(_store.CraftsDirectory, craft.Id + ".json");
        File.WriteAllText(jsonPath, PanelCraftJson.Write(craft));

        Assert.False(_store.Delete(craft.Id, DateTimeOffset.UtcNow));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(jsonPath));
    }

    private static PanelCraft SampleCraft(string fileName) => new(
        "craft-80c9955d",
        "t",
        fileName,
        "image/jpeg",
        1,
        1,
        "sha",
        1,
        1,
        null,
        null,
        null);

    [Fact]
    public void PruneTrash_deletes_only_the_expired_entries_in_a_mixed_trash()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(30);
        var stale = CreateTrashEntry("craft-aaaaaaaa", now.AddDays(-30));
        var fresh = CreateTrashEntry("craft-bbbbbbbb", now.AddDays(-1));
        var foreign = Path.Combine(
            _store.CraftsDirectory, PanelCraftStore.TrashDirectoryName, "keep-me");
        Directory.CreateDirectory(foreign);

        Assert.Equal(1, _store.PruneTrash(now, TimeSpan.FromDays(7)));
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(foreign));
    }

    [Fact]
    public void PruneTrash_reads_the_id_with_hyphens_correctly()
    {
        // id 自带连字符（craft-xxxxxxxx），所以时间戳必须从**最后一个**连字符往右取。
        // 取错位置会让时间戳解析失败，过期条目永远清不掉。
        //
        // 用真实量级的时间戳（2026 年）而不是 UnixEpoch 附近：后者减 100 天就跌到纪元之前，
        // 时间戳变负数、目录名带负号，测的就不再是「连字符解析」而是另一种边界。
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_792_497_000_000);
        var path = CreateTrashEntry("craft-0123abcd", now.AddDays(-100));

        Assert.Equal(1, _store.PruneTrash(now, TimeSpan.FromDays(7)));
        Assert.False(Directory.Exists(path));
    }

    // ── 标注往返（保存恢复闭环） ─────────────────────────────

    [Fact]
    public void List_round_trips_a_scribble_trail()
    {
        // 只存外接框的话，「继续编辑」重开画布时原图上再也画不出用户原本那笔，
        // 而形式 1（任意标记 + 自然语言）正是靠笔迹让模型看懂意图的。
        var trail = new[]
        {
            new PanelInkPoint(100, 120),
            new PanelInkPoint(300, 200),
            new PanelInkPoint(700, 880),
        };

        _store.Save(
            CreateImage(), "涂鸦版", 800, 600,
            new PanelCraftSource(
                "edit-7ced1b33", null, null, new[] { PanelAnnotation.Scribble(trail) }),
            parent: null,
            note: null,
            now: DateTimeOffset.UtcNow);

        var loaded = Assert.Single(_store.List());
        var annotation = Assert.Single(loaded.Source!.Annotations);

        Assert.True(annotation.IsScribble);
        Assert.Equal(trail, annotation.Points!);

        // 外接框必须原样回来：从已抽稀的轨迹重算，会让重新打开后的坐标
        // 与当初发给模型的不一致。
        Assert.Equal("涂鸦 <bbox>100 120 700 880</bbox>", annotation.ToPromptFragment());
    }

    [Fact]
    public void List_round_trips_arrows_and_text_instead_of_dropping_them()
    {
        // 归一化解析曾经只认 box/point：带箭头或文字的制成品读回时整份标注被拒，
        // 表现是「保存成功了，继续编辑却什么都没有」。
        var arrow = PanelAnnotation.Arrow(880, 900, 120, 100);
        var text = PanelAnnotation.Text("左边的人", 300, 400);

        _store.Save(
            CreateImage(), "带箭头", 800, 600,
            new PanelCraftSource("edit-x", null, null, new[] { arrow, text }),
            parent: null,
            note: null,
            now: DateTimeOffset.UtcNow);

        var loaded = Assert.Single(_store.List());
        var annotations = loaded.Source!.Annotations;

        Assert.Equal(2, annotations.Count);

        // 方向靠顺序：排序会把箭头变成没方向的包围盒。
        Assert.Equal(
            "箭头 <point>880 900</point> → <point>120 100</point>",
            annotations[0].ToPromptFragment());
        Assert.Equal("文字「左边的人」 <point>300 400</point>", annotations[1].ToPromptFragment());
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
