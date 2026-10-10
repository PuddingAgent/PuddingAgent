using PuddingCode.Configuration;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// 桌面同步上下文下的配置加载。
///
/// **本用例的判据曾经是墙钟**（`thread.Join(5s)` 当作"没死锁"的证据）—— 那在套件并行、
/// 机器繁忙时会假失败，是登记在 `TestScripts/known-flaky.txt` 的时序 flake。
/// 现在改成**确定性判据**：加载完成后断言该上下文**从未被投递**（`PostCount == 0`）。
/// 捕获了桌面上下文才会投递；投递到不泵消息的线程上就会永久排队 —— 也就是死锁本身。
/// 线程等待只作 **watchdog**（探针）：它只负责在真挂住时给出可诊断的失败，不再承担验收职责。
/// </summary>
public sealed class DesktopConfigurationLoadingTests
{
    /// <summary>watchdog 上限：只用于诊断"真的挂住了"，不用于判定"够不够快"。</summary>
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(60);

    [Fact]
    public void LlmConfigurationLoad_DoesNotDeadlockOnDesktopSynchronizationContext()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "pudding-host-tests",
            Guid.NewGuid().ToString("N"));
        var paths = PuddingDataPaths.FromRoot(tempRoot);
        Directory.CreateDirectory(paths.ConfigRoot);
        File.WriteAllText(
            paths.SystemConfigFile("llm.providers.json"),
            """
            {
              "providers": [
                {
                  "providerId": "deepseek",
                  "name": "DeepSeek",
                  "baseUrl": "https://api.deepseek.com/v1",
                  "models": [
                    { "modelId": "deepseek-chat", "name": "DeepSeek Chat", "protocol": "openai" }
                  ]
                }
              ]
            }
            """);

        var context = new NonPumpingSynchronizationContext();
        Exception? failure = null;
        ConfigLoadResult<PuddingLlmProvidersConfig>? result = null;
        using var done = new ManualResetEventSlim(initialState: false);

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                result = new PuddingFileConfigLoader(paths)
                    .LoadLlmProvidersAsync()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                // 完成信号：这是"做完了"的唯一依据（不是墙钟）。
                done.Set();
            }
        })
        {
            IsBackground = true,
        };

        try
        {
            thread.Start();

            // watchdog：仅当真正挂住（死锁）时才失败，并明确说明它是探针而非速度判据。
            Assert.True(
                done.Wait(Watchdog),
                $"Configuration loading did not complete within the {Watchdog.TotalSeconds:0}s watchdog; "
                + "this indicates a real hang (deadlock), not slowness.");

            Assert.Null(failure);
            Assert.NotNull(result);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));

            // **确定性判据**：一个不捕获桌面上下文的加载过程永远不会投递到它。
            // （投递了 ⇒ 续体被排到一个不泵消息的线程上 ⇒ 就是死锁。）
            Assert.Equal(0, context.PostCount);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    /// <summary>临时目录删除可能遇到尚未释放的句柄：重试几次，仍失败也不让清理失败污染用例结论。</summary>
    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * (attempt + 1)));
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * (attempt + 1)));
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
        }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        /// <summary>
        /// 该上下文被**投递过**几次。判据用它而不是墙钟：配置加载**不得**捕获桌面上下文，
        /// 因此正确实现下这里恒为 0（捕获了才会投递，投递了就会在阻塞线程上永久排队 = 死锁）。
        /// </summary>
        public int PostCount => Volatile.Read(ref _postCount);

        private int _postCount;

        public override void Post(SendOrPostCallback d, object? state)
        {
            // A WPF startup thread cannot pump continuations while it is blocked
            // in the synchronous composition-root call.
            Interlocked.Increment(ref _postCount);
        }
    }
}
