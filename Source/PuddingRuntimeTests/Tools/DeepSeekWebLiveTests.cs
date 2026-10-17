using System.Text.Json;
using System.Text.Json.Nodes;
using Flurl.Http.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingDeepSeekWeb;
using PuddingRuntime.Services.DeepSeekWeb;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <b>真实联网</b>测试（可选）：走完整生产链路「data 配置 → DeepSeekWebSettingsProvider →
/// WebClientDeepSeekTransport(IWebClient/Flurl) → 组件 DeepSeekWebService → 工具体系 ExecuteAsync」，
/// 验证 deepseek_search 与 deepseek_fetch 在接入后真的可用。
/// <para>
/// 凭据<b>不硬编码、不打印</b>：由 <see cref="DeepSeekWebSettingsProvider"/> 从
/// <c>PUDDING_DATA_ROOT</c> / <c>D:\data</c> / 仓库回溯 <c>data/config</c> 中的
/// <c>llm.providers.json</c>（providerId=deepseek）或 <c>search.providers.json</c> 解析。
/// 只有显式设置 <c>PUDDING_DEEPSEEK_LIVE=1</c> 才真正发真实请求，否则 Inconclusive ——
/// 避免常规 <c>--filter DeepSeek</c> 验收意外产生真实网络费用/抖动。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class DeepSeekWebLiveTests
{
    private const string LiveOptInVariable = "PUDDING_DEEPSEEK_LIVE";

    [TestMethod]
    public async Task SearchAndFetch_ThroughToolSystem_RealApi_ReturnUsableOutput()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(LiveOptInVariable), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive($"设置 {LiveOptInVariable}=1 才运行真实 DeepSeek 联网测试。");
            return;
        }

        var settingsProvider = ResolveLiveSettingsProvider();
        if (settingsProvider is null)
        {
            Assert.Inconclusive("未找到可解析 DeepSeek 凭据的 data 配置，跳过联网测试。");
            return;
        }

        Assert.IsTrue(
            settingsProvider.TryResolve(out var settings, out var resolveError),
            resolveError);
        Assert.IsFalse(string.IsNullOrWhiteSpace(settings.Connection.ApiKey));
        Console.WriteLine(
            $"[DeepSeek live] source={settings.Source} baseUrl={settings.Connection.BaseUrl} "
            + $"timeout={settings.Connection.TimeoutSeconds}s"); // 只打印非敏感字段。

        var webClient = new FlurlWebClient(new FlurlClientCache());
        var service = new DeepSeekWebService(
            new WebClientDeepSeekTransport(webClient),
            maxRetryAttempts: 0); // Live 测试不做重试，避免偶发抖动叠加延迟。

        var searchTool = new DeepSeekSearchTool(
            service, settingsProvider, NullLogger<DeepSeekSearchTool>.Instance);
        var fetchTool = new DeepSeekFetchTool(
            service, settingsProvider, NullLogger<DeepSeekFetchTool>.Instance);

        // ── ① 真实 Search：经工具体系（ExecuteAsync → args 反序列化 → 组件 → Flurl → DeepSeek） ──
        var searchResult = await DeepSeekWebTestTools.ExecuteAsync(searchTool, """
            {
              "query": "DeepSeek API tools search fetch documentation",
              "count": 3
            }
            """);

        Assert.IsTrue(searchResult.Success, searchResult.Error);
        StringAssert.Contains(searchResult.Output, "DeepSeek Search results for:");
        StringAssert.Contains(searchResult.Output, "URL: http");
        Console.WriteLine($"[DeepSeek live] search success, outputChars={searchResult.Output.Length}");

        var firstUrl = ExtractFirstUrl(searchResult.Output);
        Assert.IsNotNull(firstUrl, "搜索结果中应至少包含一个可抓取的 URL。");

        // ── ② 真实 Fetch：抓取第一条结果，验证 title + Markdown 正文渲染 ──
        var fetchResult = await DeepSeekWebTestTools.ExecuteAsync(
            fetchTool, JsonSerializer.Serialize(new { url = firstUrl }));

        Assert.IsTrue(fetchResult.Success, fetchResult.Error);
        StringAssert.Contains(fetchResult.Output, "DeepSeek Fetch:");
        StringAssert.Contains(fetchResult.Output, $"URL: {firstUrl}");
        Console.WriteLine(
            $"[DeepSeek live] fetch success, host={new Uri(firstUrl!).Host}, "
            + $"outputChars={fetchResult.Output.Length}");
    }

    /// <summary>按优先级遍历候选 data 根，返回第一个能解析出 DeepSeek 凭据的 provider（不打印 Key）。</summary>
    private static DeepSeekWebSettingsProvider? ResolveLiveSettingsProvider()
    {
        foreach (var root in CandidateDataRoots())
        {
            if (!Directory.Exists(root))
                continue;

            PuddingDataPaths paths;
            try
            {
                paths = PuddingDataPaths.FromRoot(root);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var provider = new DeepSeekWebSettingsProvider(
                paths, NullLogger<DeepSeekWebSettingsProvider>.Instance);
            if (provider.TryResolve(out _, out _))
                return provider;
        }

        return null;
    }

    private static IEnumerable<string> CandidateDataRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var explicitRoot = Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot) && seen.Add(explicitRoot))
            yield return explicitRoot;

        // 本机部署默认位置。
        if (seen.Add(@"D:\data"))
            yield return @"D:\data";

        // 从测试程序集目录回溯仓库根下的 data/config。
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "data");
            if (seen.Add(candidate))
                yield return candidate;
        }
    }

    /// <summary>从渲染结果中取出第一条 <c>URL: ...</c>（不解析/不回显凭据）。</summary>
    private static string? ExtractFirstUrl(string output)
    {
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            const string prefix = "URL: ";
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var candidate = line[prefix.Length..].Trim();
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return candidate;
            }
        }

        return null;
    }
}
