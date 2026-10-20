using System;
using System.IO;
using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>
/// 烧录图的保留策略。这个目录每次「添加到对话」都会多一张图，
/// 没有上限就会无限增长；但清理**不能**碰用户自己放进去的文件。
/// </summary>
public sealed class PanelMarkRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"panel-marks-{Guid.NewGuid():N}");

    public PanelMarkRetentionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, DateTime utc)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, utc);
        return path;
    }

    [Fact]
    public void KeepsTheNewestFilesAndDeletesTheRest()
    {
        var baseTime = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
        {
            Write($"panel-marked-{i}.jpg", baseTime.AddMinutes(i));
        }

        var removed = PanelMarkRetention.Prune(_root, keep: 2);

        Assert.Equal(3, removed);
        var remaining = Directory.GetFiles(_root, "panel-marked-*.jpg");
        Assert.Equal(2, remaining.Length);
        // 保留的是最新两张（分钟数最大）。
        Assert.Contains(remaining, path => path.EndsWith("panel-marked-4.jpg", StringComparison.Ordinal));
        Assert.Contains(remaining, path => path.EndsWith("panel-marked-3.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNothingWhenUnderTheLimit()
    {
        var path = Write("panel-marked-a.jpg", DateTime.UtcNow);

        Assert.Equal(0, PanelMarkRetention.Prune(_root, keep: 50));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void NeverDeletes_a_mark_that_was_inserted_into_the_conversation()
    {
        // 插入过的图已经写进消息历史，删掉就是永久断链 —— 时间再旧也不动。
        var baseTime = DateTime.UtcNow.AddDays(-30);
        var used = Write("panel-marked-used.jpg", baseTime);
        var junk = Write("panel-marked-junk.jpg", baseTime.AddMinutes(1));

        PanelMarkRetention.MarkUsed(used);
        Assert.True(File.Exists(used + PanelMarkRetention.UsedSuffix));

        var removed = PanelMarkRetention.Prune(_root, keep: 0, minAge: TimeSpan.FromDays(7));

        Assert.Equal(1, removed);
        Assert.True(File.Exists(used));
        Assert.False(File.Exists(junk));
    }

    [Fact]
    public void UsedMarks_do_not_consume_the_keep_budget()
    {
        // keep 是「未引用图」的上限：引用过的图本来就不该占名额，
        // 否则用户改得越多，能留的垃圾越少，行为会随历史随机变化。
        var baseTime = DateTime.UtcNow.AddDays(-30);
        var usedA = Write("panel-marked-u1.jpg", baseTime);
        var usedB = Write("panel-marked-u2.jpg", baseTime.AddMinutes(1));
        PanelMarkRetention.MarkUsed(usedA);
        PanelMarkRetention.MarkUsed(usedB);

        var unusedOld = Write("panel-marked-n1.jpg", baseTime.AddMinutes(2));
        var unusedNew = Write("panel-marked-n2.jpg", baseTime.AddMinutes(3));

        var removed = PanelMarkRetention.Prune(_root, keep: 1);

        Assert.Equal(1, removed);
        Assert.True(File.Exists(usedA));
        Assert.True(File.Exists(usedB));
        Assert.True(File.Exists(unusedNew));
        Assert.False(File.Exists(unusedOld));
    }

    [Fact]
    public void Prune_removes_a_used_marker_whose_image_is_gone()
    {
        // 用户可能直接在资源管理器里删图：图没了，.used 会一直留着。
        var path = Write("panel-marked-orphan.jpg", DateTime.UtcNow);
        PanelMarkRetention.MarkUsed(path);
        File.Delete(path);

        var removed = PanelMarkRetention.Prune(_root, keep: 0);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(path + PanelMarkRetention.UsedSuffix));
    }

    [Fact]
    public void NeverTouchesFilesThatAreNotOurs()
    {
        // 用户可能把这个目录当普通文件夹用。前缀不匹配的一律不动。
        var mine = Write("panel-marked-a.jpg", DateTime.UtcNow);
        var theirs = Write("我的笔记.jpg", DateTime.UtcNow);
        var otherJpg = Write("holiday.jpg", DateTime.UtcNow);

        PanelMarkRetention.Prune(_root, keep: 0);

        Assert.False(File.Exists(mine));
        Assert.True(File.Exists(theirs));
        Assert.True(File.Exists(otherJpg));
    }

    [Fact]
    public void AMissingDirectoryIsNotAnError()
    {
        Assert.Equal(0, PanelMarkRetention.Prune(Path.Combine(_root, "nope"), keep: 1));
        Assert.Equal(0, PanelMarkRetention.Prune(null, keep: 1));
        Assert.Equal(0, PanelMarkRetention.Prune(string.Empty, keep: 1));
    }

    [Fact]
    public void ANegativeKeepIsRefusedInsteadOfDeletingEverything()
    {
        var path = Write("panel-marked-a.jpg", DateTime.UtcNow);

        // keep < 0 是调用方写错了：宁可什么都不做，也不要"删光"。
        Assert.Equal(0, PanelMarkRetention.Prune(_root, keep: -1));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void FilesNewerThanMinAgeAreKeptEvenBeyondTheCountLimit()
    {
        // 数量与年龄**同时**满足才删：昨天刚插入的图很可能还在对话里被引用。
        var fresh1 = Write("panel-marked-1.jpg", DateTime.UtcNow.AddHours(-2));
        var fresh2 = Write("panel-marked-2.jpg", DateTime.UtcNow.AddDays(-1));
        var stale = Write("panel-marked-3.jpg", DateTime.UtcNow.AddDays(-30));

        var removed = PanelMarkRetention.Prune(_root, keep: 0, minAge: TimeSpan.FromDays(7));

        Assert.Equal(1, removed);
        Assert.True(File.Exists(fresh1));
        Assert.True(File.Exists(fresh2));
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void TheFileJustWrittenSurvivesAClockSkew()
    {
        // 排序依据是 mtime：时钟回拨（或某些文件系统不更新 mtime）时，
        // 刚写的图会被排成"最旧"，当成垃圾删掉。protect 必须兜住它。
        var older = Write("panel-marked-old.jpg", new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc));
        var justWritten = Write("panel-marked-fresh.jpg", new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc));

        var removed = PanelMarkRetention.Prune(_root, keep: 0, protect: justWritten);

        Assert.Equal(1, removed);
        Assert.True(File.Exists(justWritten));
        Assert.False(File.Exists(older));
    }
}
