using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 回归锁：**通道关闭必须晚于输出排空**。
/// <para>
/// 背景（2026-09-29，实测上报）：<c>System.Threading.Channels.ChannelClosedException</c>
/// 抛自 <c>TerminalProcessManager</c> stderr 处理器里的 <c>channel.Writer.WriteAsync</c>。
/// 成因是 Exited 处理器**立刻** <c>TryComplete()</c>，而 .NET 明确「输出重定向时 Exited 可能在
/// stdout/stderr 投递完成之前触发」⇒ 迟到行写进已关闭的通道。危害不止崩溃：异常发生在写日志
/// **之前**，所以那些尾部行连 <c>data/terminal/{pid}.log</c> 都进不去 —— 恰好是排空修复要保住的东西；
/// 而快照因为 <c>AppendOutput</c> 在写通道之前调用，看起来"正常"，因此这个问题此前不易察觉。
/// </para>
/// <para>
/// 可观测判据是**日志文件**：尾部 stderr 行必须出现。旧行为下它随竞态间歇性丢失（写通道抛异常
/// ⇒ 跳过写日志），修复后恒成立。
/// </para>
/// </summary>
[TestClass]
public sealed class TerminalProcessManagerChannelCloseTests
{
    private const string TailMarker = "PUDDING_TAIL_STDERR_AFTER_EXIT_OK";
    private const int FillerLines = 500;
    private const int TailWindow = 50;

    [TestMethod]
    public async Task TrailingStderrLineSurvivesProcessExit_AndReachesTheLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-terminal-channel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dataPaths = PuddingDataPaths.FromRoot(root);
        using var manager = new TerminalProcessManager(NullLogger<TerminalProcessManager>.Instance, dataPaths);
        try
        {
            // 先产生足量 stdout（让投递落后于 Exited），**最后**一行走 stderr —— 正是竞态窗口。
            var command = OperatingSystem.IsWindows()
                ? $"(for /L %i in (1,1,{FillerLines}) do @echo filler-%i) & echo {TailMarker} 1>&2"
                : $"seq 1 {FillerLines}; echo {TailMarker} 1>&2";

            var info = await manager.StartAsync("session-channel-close", command, root);
            var logPath = Path.Combine(dataPaths.TerminalLogsRoot, $"{info.ProcessId}.log");

            var deadline = DateTime.UtcNow.AddSeconds(60);
            var logged = false;
            var snapshotLines = string.Empty;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(logPath))
                {
                    try
                    {
                        using var stream = new FileStream(
                            logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(stream);
                        logged = (await reader.ReadToEndAsync())
                            .Contains($"[stderr] {TailMarker}", StringComparison.Ordinal);
                    }
                    catch (IOException) { /* 处理器正在写或关闭：下一轮再试 */ }
                }

                // 读法与受控检查一致：先用 (offset=0, maxLines=1) 探 TotalLines，再读尾部窗口
                // （offset=0 + maxLines=null 取的是首段，取不到尾部行）。
                var probe = await manager.ReadOutputAsync(info.ProcessId, 0, 1, null, CancellationToken.None);
                if (probe is not null)
                {
                    var tailSnapshot = await manager.ReadOutputAsync(
                        info.ProcessId,
                        Math.Max(0, probe.TotalLines - TailWindow),
                        TailWindow,
                        200_000,
                        CancellationToken.None);
                    snapshotLines = tailSnapshot is null ? string.Empty : string.Join("\n", tailSnapshot.Lines);
                }
                if (logged) break;

                await Task.Delay(100);
            }

            Assert.IsTrue(
                snapshotLines.Contains(TailMarker, StringComparison.Ordinal),
                "尾部 stderr 行必须进快照（旧行为靠 AppendOutput 的调用顺序恰好保住了这一条）");
            Assert.IsTrue(
                logged,
                $"尾部 stderr 行必须进日志 {logPath} —— 旧行为在写通道抛 ChannelClosedException 后跳过了写日志（回归锁）");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 进程可能仍持有句柄 */ }
        }
    }
}
