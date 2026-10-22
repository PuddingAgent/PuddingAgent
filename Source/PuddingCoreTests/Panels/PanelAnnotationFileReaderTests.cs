using PuddingCore.Panels;

namespace PuddingCoreTests.Panels;

/// <summary>
/// <see cref="FilePanelAnnotationFileReader"/> 与 <see cref="PanelAnnotationFileName"/> 的行为断言。
///
/// <para>
/// 这一层真正的新风险不是「读得到」，而是<b>把别人的标注当自己的</b>：
/// 用户可能同时开着几块画布，而写盘与读取是两条独立时序。一份「看起来有效」但属于
/// 另一块画布的标注，比读不到更糟 —— 它会以数据的身份直接进到 Agent 的推理里。
/// 所以下面每条错配用例都要求返回 <c>null</c>，而不是返回一份部分正确的对象。
/// </para>
/// <para>
/// 另一半风险是<b>两侧文件名漂移</b>：Desktop 的 Foundation 只依赖 BCL，无法复用 Core 的
/// 命名实现，只能各存一份。文件名锁定用例把这条约定钉成字面量，改公式时两侧测试会一起红。
/// </para>
/// </summary>
[TestClass]
public sealed class PanelAnnotationFileReaderTests
{
    [TestMethod]
    public void Valid_file_returns_the_payload_verbatim()
    {
        using var dir = new TempDirectory();
        WriteEnvelope(
            dir.Path,
            "main",
            payload: """{"src":"a.png","width":800,"height":600,"items":[{"kind":"arrow","x1":1,"y1":2,"x2":3,"y2":4}]}""");

        var file = new FilePanelAnnotationFileReader(dir.Path).Read("main");

        Assert.IsNotNull(file);
        Assert.AreEqual("main", file!.PanelId);
        // 载荷必须逐字保留：Core 不解释坐标，任何「美化」都会让读方拿到改过的语义。
        StringAssert.Contains(file.PayloadJson, "\"kind\":\"arrow\"");
        StringAssert.Contains(file.PayloadJson, "\"x1\":1");
    }

    [TestMethod]
    public void Updated_at_is_read_when_present()
    {
        using var dir = new TempDirectory();
        WriteEnvelope(dir.Path, "main", """{"items":[]}""", updatedAt: "2026-10-22T05:00:00.0000000+00:00");

        var file = new FilePanelAnnotationFileReader(dir.Path).Read("main");

        Assert.IsNotNull(file);
        Assert.AreEqual(
            new DateTimeOffset(2026, 10, 22, 5, 0, 0, TimeSpan.Zero),
            file!.UpdatedAt);
    }

    [TestMethod]
    public void Empty_items_is_a_valid_snapshot()
    {
        using var dir = new TempDirectory();
        WriteEnvelope(dir.Path, "main", """{"items":[]}""");

        // 用户确实清空了标注：这是「现在没有标注」的有效回答，
        // 不能和「从来没有过渡」混为一谈（后者返回 null）。
        var file = new FilePanelAnnotationFileReader(dir.Path).Read("main");

        Assert.IsNotNull(file);
        StringAssert.Contains(file!.PayloadJson, "\"items\":[]");
    }

    [TestMethod]
    public void Missing_file_returns_null()
    {
        using var dir = new TempDirectory();

        Assert.IsNull(new FilePanelAnnotationFileReader(dir.Path).Read("main"));
    }

    [TestMethod]
    public void Snapshot_of_another_panel_is_rejected()
    {
        using var dir = new TempDirectory();
        // 文件名对上（panel-main.annotations.json），但里面自证是另一块画布。
        WriteEnvelope(dir.Path, fileNamePanelId: "main", panelId: "other", payload: """{"items":[]}""");

        Assert.IsNull(new FilePanelAnnotationFileReader(dir.Path).Read("main"));
    }

    [TestMethod]
    public void Unsupported_schema_version_is_rejected()
    {
        using var dir = new TempDirectory();
        var path = PanelAnnotationFileName.PathFor(dir.Path, "main");
        File.WriteAllText(
            path,
            """{"schemaVersion":999,"panelId":"main","payload":{"items":[]}}""");

        // 不认识的版本一律当「没有快照」：猜旧格式会静默读错字段。
        Assert.IsNull(new FilePanelAnnotationFileReader(dir.Path).Read("main"));
    }

    [TestMethod]
    public void Corrupt_json_is_rejected_without_throwing()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(PanelAnnotationFileName.PathFor(dir.Path, "main"), "{not json");

        // 写方先写 .tmp 再 rename，所以半截内容理论上不会出现；
        // 但磁盘上真出现坏文件时，读画布不能因此失败。
        Assert.IsNull(new FilePanelAnnotationFileReader(dir.Path).Read("main"));
    }

    [TestMethod]
    public void Missing_panel_id_is_rejected()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(
            PanelAnnotationFileName.PathFor(dir.Path, "main"),
            """{"schemaVersion":1,"payload":{"items":[]}}""");

        Assert.IsNull(new FilePanelAnnotationFileReader(dir.Path).Read("main"));
    }

    [TestMethod]
    public void Escaped_string_payload_is_accepted()
    {
        using var dir = new TempDirectory();
        var path = PanelAnnotationFileName.PathFor(dir.Path, "main");
        File.WriteAllText(
            path,
            """{"schemaVersion":1,"panelId":"main","payload":"{\"items\":[{\"kind\":\"point\",\"x\":5,\"y\":6}]}"}""");

        var file = new FilePanelAnnotationFileReader(dir.Path).Read("main");

        // 手工调试脚本写出来的形态（载荷是被转义的字符串）也要读得懂，
        // 否则「我明明写了文件，Agent 说没有」这类排查会白耗一轮。
        Assert.IsNotNull(file);
        StringAssert.Contains(file!.PayloadJson, "\"kind\":\"point\"");
    }

    [TestMethod]
    public void Illegal_panel_id_returns_null_instead_of_throwing()
    {
        using var dir = new TempDirectory();

        // id 里带路径分隔符：既要拒绝，又不能抛给调用方（read_panel 不该因此失败）。
        Assert.IsNull(new FilePanelAnnotationFileReader(dir.Path).Read("main/evil"));
    }

    [TestMethod]
    public void File_name_matches_the_desktop_writer_contract()
    {
        // 两侧各有一份命名实现（Foundation 只依赖 BCL），靠这条字面量把公式钉住。
        // 改公式必须同时改 PanelAnnotationSnapshotWriter 与其对应测试。
        Assert.AreEqual("panel-main.annotations.json", PanelAnnotationFileName.For("main"));
        Assert.AreEqual(
            Path.Combine("root", "panel-a.b-1.annotations.json"),
            PanelAnnotationFileName.PathFor("root", "a.b-1"));
    }

    [TestMethod]
    public void Blank_root_is_rejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new FilePanelAnnotationFileReader(" "));
    }

    private static void WriteEnvelope(
        string root,
        string fileNamePanelId,
        string payload,
        string? panelId = null,
        string? updatedAt = null)
    {
        var stamp = updatedAt is null ? string.Empty : $",\"updatedAt\":\"{updatedAt}\"";
        var json = $$"""
            {"schemaVersion":1,"panelId":"{{panelId ?? fileNamePanelId}}"{{stamp}},"payload":{{payload}}}
            """;
        File.WriteAllText(PanelAnnotationFileName.PathFor(root, fileNamePanelId), json);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-panel-annotations-" + Guid.NewGuid().ToString("N"));
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
