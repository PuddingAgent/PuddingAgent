using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingAgent.Services;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// U4-7 A6（+ 两条等价保护）：<see cref="IndexPrebuildService"/> 在**默认配置下立即返回且零索引 I/O**；
/// 只有配置显式开启且校验通过时才在启动路径之外按配置 scope 预建。
/// 引擎一律用替身计数，**不碰真实 Lucene / 真实索引目录**。
/// </summary>
public sealed class IndexPrebuildServiceTests
{
    // ── A6 ────────────────────────────────────────────────────────────────
    /// <summary>
    /// A6：默认配置（<c>Enabled=false</c>，即使 <c>Scopes</c> 写了目录）⇒ <c>StartAsync</c> 立即返回，
    /// 且**引擎一次都没被碰过**（<c>HasIndex</c> / <c>BuildIndexAsync</c> 计数均为 0）。
    /// <para>刻意把 <see cref="IndexPrebuildService.StartupDelay"/> 置零：这样「偷偷跑后台预建」会在几百毫秒内暴露。</para>
    /// </summary>
    [Fact]
    public async Task StartAsync_With_Default_Configuration_Returns_Immediately_And_Never_Touches_The_Engine()
    {
        var scope = NewTempDirectory();
        try
        {
            var engine = new CountingSearchEngine();
            var service = CreateService(
                engine,
                new FullTextIndexSupplyOptions { Scopes = [scope] },
                scope);

            var stopwatch = Stopwatch.StartNew();
            await service.StartAsync(CancellationToken.None);
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"StartAsync 必须立即返回（默认关闭），实测 {stopwatch.ElapsedMilliseconds} ms");

            // 暴露窗口：配置门若被拆掉，这段等待足以让引擎计数变红。
            await Task.Delay(300);

            Assert.Equal(0, engine.HasIndexCalls);
            Assert.Equal(0, engine.BuildCalls);
            Assert.Empty(engine.BuiltScopes);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>
    /// 开启且校验通过 ⇒ 在启动路径之外按**配置里的 scope**建索引（不是 CWD —— 历史缺陷用
    /// <c>Directory.GetCurrentDirectory()</c>，本机等于运行时 bin 目录）。
    /// </summary>
    [Fact]
    public async Task Enabled_Configuration_Builds_Exactly_The_Configured_Scopes()
    {
        var scope = NewTempDirectory();
        try
        {
            var engine = new CountingSearchEngine();
            var service = CreateService(
                engine,
                new FullTextIndexSupplyOptions { Enabled = true, Scopes = [scope] },
                scope);

            await service.StartAsync(CancellationToken.None);

            Assert.True(
                await WaitUntilAsync(() => engine.BuildCalls >= 1, TimeSpan.FromSeconds(10)),
                "开启且校验通过后必须真的建索引");

            var built = Assert.Single(engine.BuiltScopes);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(scope)), built);
            Assert.False(
                string.Equals(Path.GetFullPath(Directory.GetCurrentDirectory()), built, StringComparison.OrdinalIgnoreCase),
                "索引目标必须来自配置，绝不能是进程 CWD（历史缺陷）");
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>
    /// 开启但校验不过（<c>Scopes</c> 为空）⇒ fail-closed：**记 Error**（不是静默空转）且什么都不做。
    /// </summary>
    [Fact]
    public async Task Enabled_But_Rejected_Configuration_Logs_An_Error_And_Builds_Nothing()
    {
        var engine = new CountingSearchEngine();
        var logger = new RecordingLogger();
        var service = new IndexPrebuildService(
            engine,
            Options.Create(new FullTextIndexSupplyOptions { Enabled = true }),
            new FullTextIndexOptions
            {
                IndexRootDirectory = Path.Combine(Path.GetTempPath(), "PuddingAgent", "u4-7-never-created"),
            },
            logger)
        {
            StartupDelay = TimeSpan.Zero,
        };

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        Assert.Equal(0, engine.HasIndexCalls);
        Assert.Equal(0, engine.BuildCalls);
        Assert.Contains(
            logger.Entries,
            static entry => entry.StartsWith("Error:", StringComparison.Ordinal)
                            && entry.Contains("rejected", StringComparison.Ordinal));
    }

    // ── 局部工具 ──────────────────────────────────────────────────────────
    private static IndexPrebuildService CreateService(
        IFullTextSearchEngine engine,
        FullTextIndexSupplyOptions supplyOptions,
        string indexRootParent)
        => new(
            engine,
            Options.Create(supplyOptions),
            new FullTextIndexOptions { IndexRootDirectory = Path.Combine(indexRootParent, "index") },
            NullLogger<IndexPrebuildService>.Instance)
        {
            StartupDelay = TimeSpan.Zero,
        };

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PuddingAgent", $"u4-7-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }

        return condition();
    }

    /// <summary>引擎替身：只计数，不建任何真实索引。</summary>
    private sealed class CountingSearchEngine : IFullTextSearchEngine
    {
        private readonly object _gate = new();
        private readonly List<string> _builtScopes = [];

        public int HasIndexCalls { get; private set; }

        public int BuildCalls { get; private set; }

        public IReadOnlyList<string> BuiltScopes
        {
            get
            {
                lock (_gate)
                    return _builtScopes.ToArray();
            }
        }

        public bool HasIndex(string directoryPath)
        {
            lock (_gate)
                HasIndexCalls++;

            return false;
        }

        public Task<FullTextSearchResult> SearchAsync(
            string query,
            string directoryPath,
            int maxResults = 30,
            string? fileExtensionFilter = null,
            string? subDirectoryFilter = null,
            CancellationToken ct = default,
            FullTextSearchScope? scope = null) =>
            Task.FromResult(new FullTextSearchResult(false, [], "test double", 0, 0));

        public Task<FullTextIndexResult> BuildIndexAsync(
            string directoryPath,
            string? filePatterns = null,
            CancellationToken ct = default)
        {
            lock (_gate)
            {
                BuildCalls++;
                _builtScopes.Add(directoryPath);
            }

            return Task.FromResult(new FullTextIndexResult(true, 1, 1, 1, null));
        }

        public bool RemoveIndex(string directoryPath) => false;
    }

    /// <summary>Logger 替身：把每条日志按 <c>级别: 文本</c> 记下来，供「拒绝必须可见」断言。</summary>
    private sealed class RecordingLogger : ILogger<IndexPrebuildService>
    {
        private readonly object _gate = new();
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_gate)
                    return _entries.ToArray();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
                _entries.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }
}
