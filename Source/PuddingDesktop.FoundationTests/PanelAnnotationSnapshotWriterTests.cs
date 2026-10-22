namespace PuddingDesktop.FoundationTests;

using System.Text.Json;
using PuddingDesktop.Foundation;

/// <summary>
/// <see cref="PanelAnnotationSnapshotWriter"/> 的契约。
///
/// <para>
/// 这份文件是 Desktop 唯一一处「为别的进程写状态」，所以三类错误都不报异常、
/// 只表现为「Agent 看到的标注不对」，最难排查：
/// ① 字段名/版本与 Core 侧解析约定漂移（读方静默返回 null）；
/// ② 空载荷覆盖掉用户上一笔（看起来像是白画了）；
/// ③ 残留 <c>.tmp</c> 或非原子写（读方读到半截）。
/// 下面逐条钉住。
/// </para>
/// </summary>
public sealed class PanelAnnotationSnapshotWriterTests
{
    private const string SamplePayload =
        """{"src":"a.png","width":800,"height":600,"naturalWidth":1600,"naturalHeight":1200,"items":[{"kind":"box","x1":10,"y1":20,"x2":110,"y2":220}]}""";

    [Fact]
    public void Writes_a_camel_case_envelope_readable_by_the_core_side()
    {
        using var dir = new TempDirectory();

        var ok = PanelAnnotationSnapshotWriter.TryPublish(
            dir.Path,
            "main",
            SamplePayload,
            DateTimeOffset.UnixEpoch,
            out var error);

        Assert.True(ok, error);
        Assert.Null(error);

        using var document = JsonDocument.Parse(
            File.ReadAllText(PanelAnnotationSnapshotWriter.PathFor(dir.Path, "main")));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("main", root.GetProperty("panelId").GetString());
        // updatedAt 用往返格式（"O"），Core 侧 TryGetDateTimeOffset 才解析得出。
        Assert.Equal("1970-01-01T00:00:00.0000000+00:00", root.GetProperty("updatedAt").GetString());
        // 载荷是**嵌套对象**而不是被转义的字符串：Core 两种都接受，但对象形态人读得懂。
        Assert.Equal(JsonValueKind.Object, root.GetProperty("payload").ValueKind);
        Assert.Equal("box", root.GetProperty("payload").GetProperty("items")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public void Publish_leaves_no_temp_file_behind()
    {
        using var dir = new TempDirectory();

        Assert.True(PanelAnnotationSnapshotWriter.TryPublish(
            dir.Path, "main", SamplePayload, DateTimeOffset.Now, out var error), error);

        // 残留 .tmp 意味着某次写没走完；它会和真实快照一起被下一轮读写看见，必须先于排查清掉。
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Empty_payload_never_overwrites_the_previous_snapshot()
    {
        using var dir = new TempDirectory();
        Assert.True(PanelAnnotationSnapshotWriter.TryPublish(
            dir.Path, "main", SamplePayload, DateTimeOffset.Now, out _));
        var before = File.ReadAllText(PanelAnnotationSnapshotWriter.PathFor(dir.Path, "main"));

        var ok = PanelAnnotationSnapshotWriter.TryPublish(
            dir.Path, "main", "   ", DateTimeOffset.Now, out var error);

        // 空载荷的真实含义是「页里没有 puddingAnnotation」或「页面还没同步」，
        // 把它当「用户清空了」写下去，用户的上一笔就永久丢了。
        Assert.False(ok);
        Assert.Equal("payload is empty", error);
        Assert.Equal(before, File.ReadAllText(PanelAnnotationSnapshotWriter.PathFor(dir.Path, "main")));
    }

    [Fact]
    public void Payload_that_is_not_a_json_object_is_refused()
    {
        using var dir = new TempDirectory();

        Assert.False(PanelAnnotationSnapshotWriter.TryPublish(
            dir.Path, "main", "[1,2,3]", DateTimeOffset.Now, out var error));
        Assert.Equal("payload root is not an object", error);

        Assert.False(PanelAnnotationSnapshotWriter.TryPublish(
            dir.Path, "main", "{not json", DateTimeOffset.Now, out error));
        Assert.StartsWith("payload is not valid json", error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Illegal_panel_id_is_refused_instead_of_being_sanitized()
    {
        using var dir = new TempDirectory();

        // 清洗 "main/evil" 成 "evil" 会把一次越界写伪装成一次正常写。
        Assert.False(PanelAnnotationSnapshotWriter.TryPublish(
            dir.Path, "main/evil", SamplePayload, DateTimeOffset.Now, out var error));
        Assert.NotNull(error);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Missing_directory_is_created()
    {
        using var dir = new TempDirectory();
        var nested = Path.Combine(dir.Path, "panels");

        Assert.True(PanelAnnotationSnapshotWriter.TryPublish(
            nested, "main", SamplePayload, DateTimeOffset.Now, out var error), error);
        Assert.True(File.Exists(PanelAnnotationSnapshotWriter.PathFor(nested, "main")));
    }

    [Fact]
    public void Chinese_text_survives_the_round_trip()
    {
        using var dir = new TempDirectory();
        const string payload = """{"items":[{"kind":"text","label":"把车标改成蓝的","x":30,"y":40}]}""";

        Assert.True(PanelAnnotationSnapshotWriter.TryPublish(
            dir.Path, "main", payload, DateTimeOffset.Now, out var error), error);

        var json = File.ReadAllText(PanelAnnotationSnapshotWriter.PathFor(dir.Path, "main"));
        // 默认 encoder 会把中文转成 \uXXXX —— 仍然可读，但这行断言防的是「被截断/乱码」。
        Assert.Contains("把车标改成蓝的", JsonSerializer.Deserialize<JsonElement>(json)
            .GetProperty("payload").GetProperty("items")[0].GetProperty("label").GetString()!);
    }

    [Fact]
    public void File_name_matches_the_core_side_contract()
    {
        // Core 侧 PanelAnnotationFileName.For 有一模一样的字面量断言：
        // 改公式必须同时改两侧实现与两侧测试，否则一侧写、另一侧读不到。
        Assert.Equal("panel-main.annotations.json", PanelAnnotationSnapshotWriter.For("main"));
        Assert.Equal(
            Path.Combine("root", "panel-a.b-1.annotations.json"),
            PanelAnnotationSnapshotWriter.PathFor("root", "a.b-1"));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-panel-snapshot-" + Guid.NewGuid().ToString("N"));
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
