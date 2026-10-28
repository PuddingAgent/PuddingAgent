using Microsoft.VisualStudio.TestTools.UnitTesting;
using Serilog.Events;
using Serilog.Parsing;
using PuddingCode.Observability;

namespace PuddingCoreTests.Observability;

/// <summary>
/// 门禁（2026-10-28）：日志清理必须按「日期 + 当日序号」保留最新，不能只比序号。
/// <para>
/// 现场事故：D:\Data\logs\system 下当天的 pudding-20261028_001.._020 全部消失，
/// 而 pudding-20261009_043.._060 全在 —— 当天排障要用的证据被自己的清理删掉了。
/// 原因是序号**按日重置**，只按序号降序会把今天的 _020 判成比上个月的 _060 更旧。
/// </para>
/// <para>
/// 本用例刻意把旧日期文件的序号造得更高（058/059/060 vs 001/002/003）：
/// 只有真正把日期纳入排序键的实现才能通过。
/// </para>
/// </summary>
[TestClass]
public sealed class SizeRollingFileSinkRetentionTests
{
    [TestMethod]
    public void RetentionKeepsTodaysFilesEvenWhenTheirSequenceNumbersAreLower()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pudding-log-retention-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            foreach (var seq in new[] { 58, 59, 60 })
                File.WriteAllText(Path.Combine(dir, $"pudding-20261009_{seq:D3}.log"), "old");
            foreach (var seq in new[] { 1, 2, 3 })
                File.WriteAllText(Path.Combine(dir, $"pudding-20261028_{seq:D3}.log"), "new");

            using (var sink = new SizeRollingFileSink(
                       dir, "pudding", maxFileSizeBytes: 1_048_576, retainedFileCountLimit: 4))
            {
                // 写一条 ⇒ 新建 pudding-20261028_004.log，并在切换后触发清理（保留 4 个）。
                sink.Emit(new LogEvent(
                    DateTimeOffset.Parse("2026-10-28T12:00:00+08:00"),
                    LogEventLevel.Information,
                    null,
                    new MessageTemplateParser().Parse("hello"),
                    Array.Empty<LogEventProperty>()));
            }

            var remaining = Directory.GetFiles(dir, "pudding-*.log")
                .Select(Path.GetFileName)
                .ToArray();

            Assert.AreEqual(4, remaining.Length, "应只保留 4 个文件");
            CollectionAssert.Contains(remaining, "pudding-20261028_004.log", "当前写入的文件必须保留");
            CollectionAssert.Contains(
                remaining,
                "pudding-20261028_003.log",
                "今天的文件不得因为序号小就被判成最旧（日期必须参与排序）");
            CollectionAssert.DoesNotContain(
                remaining,
                "pudding-20261009_060.log",
                "跨日时序号更高的旧日期文件才是该被清掉的那一端");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 测试清理失败不影响断言结果
            }
        }
    }
}
