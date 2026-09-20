using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// <b>真实联网</b>测试：走完整生产路径「LLM 资源池 JSON → JevDecisionOptionsProvider →
/// JevDecisionService → https://jevtypesafeai.com/api/v1/decide」，验证 Jev 返回的是
/// <b>结构化</b>答案（choice / score / noul）而非自由文本。
/// <para>
/// 密钥<b>不硬编码</b>：从资源池 <c>llm.providers.json</c> 的 provider <c>jev</c> 解析。
/// 资源池文件缺失、或其中仍是 <c>${...}</c> 占位符且未设 <c>JEV_API_KEY</c> 时，
/// 测试以 <see cref="Assert.Inconclusive"/> 跳过（保证无密钥的环境不会假失败）。
/// </para>
/// <para>
/// 会真实产生费用（文档口径 $0.42/百万 input token）：本测试只发 1 次请求、
/// state 极短、三个提问共用同一次 round trip。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class JevDecisionLiveTests
{
    /// <summary>显式指定资源池文件路径（优先于所有猜测路径）。</summary>
    private const string PoolPathEnvironmentVariable = "JEV_LIVE_POOL_PATH";

    /// <summary>资源池相对 data 根目录的路径。</summary>
    private const string PoolRelativePath = @"config\llm.providers.json";

    private const string ExpectedBaseUrl = "https://jevtypesafeai.com";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly string[] RouteOptions = ["billing", "bug", "account"];

    [TestMethod]
    public async Task DecideAsync_RealResourcePool_LiveCall_ReturnsStructuredAnswers()
    {
        // ── ① 定位资源池文件；缺失则跳过（无密钥环境不假失败） ──
        var poolPath = ResolvePoolPath();
        if (poolPath is null)
        {
            Assert.Inconclusive(
                $"未找到资源池文件（可设 {PoolPathEnvironmentVariable} 指定路径），跳过联网测试。");
            return;
        }

        var json = await File.ReadAllTextAsync(poolPath).ConfigureAwait(false);
        var poolRoot = JsonNode.Parse(json);

        var providerNode = poolRoot?["providers"]?.AsArray()
            .FirstOrDefault(candidate => string.Equals(
                (string?)candidate?["providerId"], "jev", StringComparison.OrdinalIgnoreCase));

        // ② 断言：密钥确实以「真实值」存放在资源池 JSON 里，而不是占位符
        var poolApiKey = (string?)providerNode?["apiKey"];
        var environmentApiKey = Environment.GetEnvironmentVariable("JEV_API_KEY");
        if (string.IsNullOrWhiteSpace(environmentApiKey)
            && (string.IsNullOrWhiteSpace(poolApiKey) || poolApiKey.StartsWith("${", StringComparison.Ordinal)))
        {
            Assert.Inconclusive("资源池中 jev.apiKey 仍是占位符且环境无 JEV_API_KEY，跳过联网测试。");
            return;
        }

        Assert.IsNotNull(providerNode, "资源池中缺少 providerId=\"jev\" 的 provider。");
        Assert.AreEqual(
            ExpectedBaseUrl,
            (string?)providerNode!["baseUrl"],
            "资源池 jev provider 的 baseUrl 应为官方托管端点。");

        // ── ③ 用真实资源池构造 ILlmConfigService，再走生产选项提供者解析连接参数 ──
        var providersConfig = JsonSerializer.Deserialize<PuddingLlmProvidersConfig>(
            json, SerializerOptions);
        Assert.IsNotNull(providersConfig, "资源池 JSON 反序列化为 PuddingLlmProvidersConfig 失败。");

        var llmConfigService = new PuddingFileLlmConfigService(providersConfig);
        var optionsProvider = new JevDecisionOptionsProvider(
            new ConfigurationBuilder().Build(),
            llmConfigService,
            NullLogger<JevDecisionOptionsProvider>.Instance,
            keyVaultService: null);

        var options = await optionsProvider.GetOptionsAsync().ConfigureAwait(false);

        Assert.AreEqual(ExpectedBaseUrl, options.BaseUrl, "端点应取自资源池 provider.baseUrl。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(options.ModelId), "模型应取自资源池。");
        Assert.IsTrue(
            options.ApiKey.StartsWith("jv_", StringComparison.Ordinal),
            "密钥应形如 jv_…（此处不打印密钥本体）。");
        Assert.IsTrue(options.ApiKey.Length > 20, "密钥长度不符预期。");

        // 关键反证：环境变量为空时，密钥只可能来自资源池 JSON —— 证明池内 apiKey 真的被读取。
        if (string.IsNullOrWhiteSpace(environmentApiKey))
        {
            Assert.AreEqual(
                string.IsNullOrWhiteSpace(poolApiKey) ? null : poolApiKey,
                string.IsNullOrWhiteSpace(poolApiKey) ? null : options.ApiKey,
                "环境无 JEV_API_KEY 时，解析结果必须等于资源池中的 apiKey。");
        }

        // ── ④ 真实调用 ──
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var service = new JevDecisionService(
            new StaticHttpClientFactory(httpClient),
            optionsProvider,
            NullLogger<JevDecisionService>.Instance);

        var request = new JevDecisionRequest
        {
            State = JsonValue.Create("客服工单：我昨天被重复扣款两次，三天了还没人回复。"),
            Questions =
            [
                new JevQuestion
                {
                    Name = "route",
                    Type = JevQuestionType.Choice,
                    Instructions = "这个工单应该转给谁？",
                    ChoiceCriteria = new Dictionary<string, string?>
                    {
                        ["billing"] = "付款、退款、发票",
                        ["bug"] = "产品坏了",
                        ["account"] = "登录或访问",
                    },
                },
                new JevQuestion
                {
                    Name = "urgency",
                    Type = JevQuestionType.Score,
                    Instructions = "这条消息有多紧急？",
                    ScoreCriteria = ["常规，不急", "今天应处理", "紧急，客户已不满", "危急，即将流失"],
                },
                new JevQuestion
                {
                    Name = "escalate",
                    Type = JevQuestionType.Noul,
                    Instructions = "是否应立即升级给人工？",
                },
            ],
        };

        var result = await service.DecideAsync(request).ConfigureAwait(false);

        // ── ⑤ 断言结构化答案（不是文本） ──
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Model), "服务端应回填 model。");
        Console.WriteLine(
            $"[Jev live] model={result.Model} inputTokens={result.Usage?.InputTokens} "
            + $"costUsd={result.Usage?.CostUsd} creditsRemaining={result.Usage?.CreditsRemainingUsd}");

        var route = result.GetAnswer("route");
        Assert.IsNotNull(route, "缺少 route 答案。");
        Assert.AreEqual("choice", route!.Type);
        CollectionAssert.Contains(RouteOptions.ToList(), route.Choice);
        Assert.IsNotNull(route.Probabilities, "choice 应返回每个选项的概率。");
        Assert.IsTrue(
            route.Confidence is >= 0 and <= 1, $"confidence 越界：{route.Confidence}");

        var urgency = result.GetAnswer("urgency");
        Assert.IsNotNull(urgency, "缺少 urgency 答案。");
        Assert.AreEqual("score", urgency!.Type);
        Assert.IsTrue(
            urgency.Score is >= 0 and <= 3, $"score 应在 0..3 层级范围内：{urgency.Score}");

        var escalate = result.GetAnswer("escalate");
        Assert.IsNotNull(escalate, "缺少 escalate 答案。");
        Assert.AreEqual("noul", escalate!.Type);
        Assert.IsTrue(escalate.Noul is >= 0 and <= 1, $"noul 应为 0..1 概率：{escalate.Noul}");

        // ── ⑥ 用量：真实计费证据 ──
        Assert.IsNotNull(result.Usage, "响应应带 usage 块。");
        Assert.IsTrue(result.Usage!.InputTokens > 0, "input_tokens 应大于 0。");
    }

    /// <summary>按优先级定位本机资源池文件；全部不存在返回 null。</summary>
    private static string? ResolvePoolPath()
    {
        var candidates = new List<string>();

        var explicitPath = Environment.GetEnvironmentVariable(PoolPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
            candidates.Add(explicitPath);

        var dataRoot = Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(dataRoot))
            candidates.Add(Path.Combine(dataRoot, PoolRelativePath));

        // 本机部署默认位置
        candidates.Add(Path.Combine(@"D:\data", PoolRelativePath));

        // 从测试程序集目录回溯仓库根下的 data/config
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, "data", PoolRelativePath));

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>始终返回同一个 HttpClient 的工厂（命名客户端超时已在组合根设置，此处等价复刻）。</summary>
    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
