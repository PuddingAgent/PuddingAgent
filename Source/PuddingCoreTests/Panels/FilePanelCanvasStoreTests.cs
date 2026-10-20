using PuddingCore.Panels;

namespace PuddingCoreTests.Panels;

/// <summary>
/// <see cref="FilePanelCanvasStore"/> 的行为断言。
///
/// <para>
/// 落盘实现真正的新风险不在「写」而在**重启边界**：能发布不代表读得回来，
/// revision 能递增也不代表跨进程还能单调。所以这里每条用例都会**重建一个 store 实例**
/// 来模拟重启，而不是只在一个实例里调两次 —— 后者对落盘实现等于什么都没验。
/// </para>
/// <para>
/// 一致性靠「先写盘后改内存 + 原子替换」保证，本文件用三条边界钉住它：
/// 坏文件不影响其余、id 形如 <c>..</c> 也跑不出根目录、校验规则与内存实现完全同源。
/// </para>
/// </summary>
[TestClass]
public sealed class FilePanelCanvasStoreTests
{
    [TestMethod]
    public void Content_survives_a_new_store_instance()
    {
        using var dir = new TempDirectory();
        new FilePanelCanvasStore(dir.Path).Publish("main", "<p>汽车</p>", "架构图");

        var reopened = new FilePanelCanvasStore(dir.Path);

        var snapshot = reopened.Get("main");
        Assert.IsNotNull(snapshot);
        Assert.AreEqual("<p>汽车</p>", snapshot!.Content);
        Assert.AreEqual("架构图", snapshot.Title);
        Assert.AreEqual(1L, snapshot.Revision);
        Assert.AreEqual(0, reopened.SkippedFileCount);
    }

    [TestMethod]
    public void Revision_keeps_incrementing_across_a_restart()
    {
        using var dir = new TempDirectory();
        new FilePanelCanvasStore(dir.Path).Publish("main", "v1");

        var reopened = new FilePanelCanvasStore(dir.Path);
        var second = reopened.Publish("main", "v2");

        // 重启后若从 1 重新开始，面板端「revision 变了才重绘」就失去了跨重启的意义。
        Assert.AreEqual(2L, second.Revision);
    }

    [TestMethod]
    public void Remove_deletes_the_backing_file()
    {
        using var dir = new TempDirectory();
        var store = new FilePanelCanvasStore(dir.Path);
        store.Publish("main", "x");

        Assert.IsTrue(store.Remove("MAIN"));
        Assert.AreEqual(0, Directory.GetFiles(dir.Path).Length);

        // 重建后必须仍然不存在：只删内存不删文件是最容易被漏掉的「看起来删了」。
        Assert.IsNull(new FilePanelCanvasStore(dir.Path).Get("main"));
    }

    [TestMethod]
    public void Dot_dot_id_cannot_escape_the_root()
    {
        using var dir = new TempDirectory();
        var store = new FilePanelCanvasStore(dir.Path);

        // id 规则允许 '.'，所以 ".." 是一个**能通过校验**的值。
        // 文件名加固定前缀后它退化成普通文件名（panel-...json），不会指向父目录。
        store.Publish("..", "x");

        Assert.AreEqual(1, Directory.GetFiles(dir.Path, "panel-*.json").Length);
        Assert.AreEqual(1, store.List().Count);
    }

    [TestMethod]
    public void Corrupt_file_is_skipped_without_breaking_the_others()
    {
        using var dir = new TempDirectory();
        new FilePanelCanvasStore(dir.Path).Publish("good", "ok");
        File.WriteAllText(Path.Combine(dir.Path, "panel-broken.json"), "{not json");

        var reopened = new FilePanelCanvasStore(dir.Path);

        // 单个坏文件不该让整块画布不可用；坏文件保留在磁盘上便于排查，不被静默清理。
        Assert.AreEqual("ok", reopened.Get("good")!.Content);
        Assert.IsNull(reopened.Get("broken"));
        Assert.AreEqual(1, reopened.SkippedFileCount);
        Assert.IsTrue(File.Exists(Path.Combine(dir.Path, "panel-broken.json")));
    }

    [TestMethod]
    public void Limits_match_the_in_memory_store()
    {
        using var dir = new TempDirectory();
        var store = new FilePanelCanvasStore(dir.Path);

        // 两个实现必须用同一套校验（共享 PanelCanvasGuard）：
        // 否则「内存能发、落盘拒绝」会变成只在重启后才暴露的缺陷。
        Assert.ThrowsExactly<ArgumentException>(() => store.Publish("main/evil", "x"));
        Assert.ThrowsExactly<ArgumentException>(
            () => store.Publish("main", new string('a', PanelCanvasLimits.MaxContentChars + 1)));
        Assert.ThrowsExactly<ArgumentNullException>(() => store.Publish("main", null!));

        // 被拒绝的发布不该留下任何痕迹（文件与内存都干净）。
        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(0, Directory.GetFiles(dir.Path).Length);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-panel-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响测试结论。
            }
        }
    }
}
