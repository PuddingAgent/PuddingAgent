using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingDeepSeekWeb;
using PuddingRuntime.Services.Tools;
using PuddingRuntimeTests.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <c>deepseek_search</c> 的契约测试：Descriptor/权限分类、配置来源（search.providers.json 优先与
/// llm.providers.json 回退）→ 线上请求形状、参数 clamp/校验、渲染与有界截断、错误映射。
/// </summary>
[TestClass]
public sealed class DeepSeekSearchToolTests
{
    private const string SearchResponseJson = """
        {
          "results": [
            {
              "title": "DeepSeek API Documentation",
              "url": "https://api-docs.deepseek.com/",
              "snippet": "Official DeepSeek API documentation.",
              "summary": "Long summary of the official documentation page.",
              "content": "# DeepSeek API\n\nIntro text.",
              "site_name": "DeepSeek",
              "date": "2026-10-01T00:00:00Z",
              "authority": 3,
              "rerank_score": 1.75
            }
          ]
        }
        """;

    [TestMethod]
    public void Descriptor_Is_Low_Risk_ReadOnly_Network_Tool()
    {
        using var temp = new DeepSeekTempDirectory();
        var tool = DeepSeekWebTestTools.CreateSearchTool(temp, new StubDeepSeekWebService());

        Assert.AreEqual("deepseek_search", tool.Descriptor.ToolId);
        Assert.AreEqual("DeepSeek Search", tool.Descriptor.Name);
        Assert.AreEqual(ToolCategory.Network, tool.Descriptor.Category);
        Assert.AreEqual(ToolPermissionLevel.Low, tool.Descriptor.PermissionLevel);
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly));
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.ConcurrencySafe));
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresNetwork));
        Assert.IsTrue(tool.Descriptor.IsEnabledByDefault);
        Assert.AreEqual(56, tool.Descriptor.SortOrder);
    }

    [TestMethod]
    public void Descriptor_Exposes_Snake_Case_Parameters_With_Required_Query()
    {
        using var temp = new DeepSeekTempDirectory();
        var tool = DeepSeekWebTestTools.CreateSearchTool(temp, new StubDeepSeekWebService());

        var names = tool.Descriptor.Parameters.Properties.Select(p => p.Name).ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "query", "count", "need_content", "ori_question", "localization" },
            names);
        CollectionAssert.Contains(tool.Descriptor.Parameters.Required.ToList(), "query");
        Assert.IsFalse(tool.Descriptor.Parameters.Required.Contains("count"));
    }

    [TestMethod]
    public void Registry_Auto_Exposes_DeepSeek_Search_Without_Template_Grant()
    {
        using var temp = new DeepSeekTempDirectory();
        var tool = DeepSeekWebTestTools.CreateSearchTool(temp, new StubDeepSeekWebService());
        var policy = new ToolPermissionPolicyService();
        var registry = new PuddingToolRegistry([tool], policy);

        var available = registry.ListAvailable(new CapabilityPolicy()).Select(d => d.ToolId).ToArray();
        var decision = policy.Classify(tool.Descriptor);

        CollectionAssert.Contains(available, "deepseek_search");
        Assert.AreEqual(ToolPermissionTier.AutoAllowed, decision.Tier);
        Assert.IsFalse(decision.RequiresRuntimeAuthorization);
        Assert.IsTrue(decision.RequiresNetworkAccess);
    }

    [TestMethod]
    public async Task ExecuteAsync_Reads_SearchProvidersConfig_AndPosts_WireRequest()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig(
            apiKey: "test-key",
            baseUrl: "https://custom.deepseek.local",
            timeoutSeconds: 12));
        var webClient = new RecordingWebClient(
            DeepSeekWebTestTools.JsonResponse(200, SearchResponseJson));
        var tool = DeepSeekWebTestTools.CreateSearchTool(
            temp, DeepSeekWebTestTools.CreateRealService(webClient));

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """
            {
              "query": "  DeepSeek API docs  ",
              "count": 5,
              "need_content": true,
              "ori_question": "what is deepseek",
              "localization": "1.2.3.4"
            }
            """);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, webClient.Requests.Count);
        var request = webClient.LastRequest!;
        Assert.AreEqual("https://custom.deepseek.local/v1/tools/search", request.Url);
        Assert.AreEqual("POST", request.Method);
        Assert.AreEqual("Bearer test-key", request.Headers["Authorization"]);
        Assert.AreEqual("application/json", request.ContentType);
        Assert.AreEqual(12, request.TimeoutSeconds);

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.AreEqual("DeepSeek API docs", root.GetProperty("query").GetString());
        Assert.AreEqual(5, root.GetProperty("count").GetInt32());
        Assert.IsTrue(root.GetProperty("need_content").GetBoolean());
        Assert.AreEqual("what is deepseek", root.GetProperty("ori_question").GetString());
        Assert.AreEqual("1.2.3.4", root.GetProperty("localization").GetString());

        StringAssert.Contains(result.Output, "DeepSeek Search results for: \"DeepSeek API docs\" (1 results)");
        StringAssert.Contains(result.Output, "1. DeepSeek API Documentation");
        StringAssert.Contains(result.Output, "URL: https://api-docs.deepseek.com/");
        StringAssert.Contains(result.Output, "Site: DeepSeek");
        StringAssert.Contains(result.Output, "Authority: 3");
        StringAssert.Contains(result.Output, "Rerank: 1.75");
        StringAssert.Contains(result.Output, "Summary: Long summary");
        StringAssert.Contains(result.Output, "Content: # DeepSeek API");
    }

    [TestMethod]
    public async Task ExecuteAsync_FallsBack_To_LlmProvidersConfig()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteLlmProviders(DeepSeekWebTestTools.LlmConfig(
            "llm-config-key", baseUrl: "https://llm.deepseek.local"));
        var webClient = new RecordingWebClient(
            DeepSeekWebTestTools.JsonResponse(200, SearchResponseJson));
        var tool = DeepSeekWebTestTools.CreateSearchTool(
            temp, DeepSeekWebTestTools.CreateRealService(webClient));

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"deepseek"}""");

        Assert.IsTrue(result.Success, result.Error);
        var request = webClient.LastRequest!;
        Assert.AreEqual("https://llm.deepseek.local/v1/tools/search", request.Url);
        Assert.AreEqual("Bearer llm-config-key", request.Headers["Authorization"]);
    }

    [TestMethod]
    public async Task ExecuteAsync_Rejects_Blank_Query_Before_Service_Call()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService();
        var tool = DeepSeekWebTestTools.CreateSearchTool(temp, service);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"   "}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Query is required.");
        Assert.AreEqual(0, service.SearchCalls.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_Rejects_Too_Long_Query()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService();
        var tool = DeepSeekWebTestTools.CreateSearchTool(temp, service);

        var result = await DeepSeekWebTestTools.ExecuteAsync(
            tool, JsonSerializer.Serialize(new { query = new string('a', 129) }));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "at most 128 characters");
        Assert.AreEqual(0, service.SearchCalls.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_Clamps_Count_And_Normalizes_Optional_Arguments()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService();
        var tool = DeepSeekWebTestTools.CreateSearchTool(temp, service);

        var clampedHigh = await DeepSeekWebTestTools.ExecuteAsync(tool, """
            {
              "query": "hello",
              "count": 999,
              "need_content": true,
              "ori_question": "   ",
              "localization": " 1.2.3.4 "
            }
            """);

        Assert.IsTrue(clampedHigh.Success, clampedHigh.Error);
        Assert.AreEqual(50, service.LastSearchRequest!.Count);
        Assert.IsTrue(service.LastSearchRequest.NeedContent);
        Assert.IsNull(service.LastSearchRequest.OriQuestion, "空白 ori_question 应归一为 null。");
        Assert.AreEqual("1.2.3.4", service.LastSearchRequest.Localization);

        var clampedLow = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"hello","count":0}""");
        Assert.IsTrue(clampedLow.Success, clampedLow.Error);
        Assert.AreEqual(1, service.LastSearchRequest!.Count);

        var defaulted = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"hello"}""");
        Assert.IsTrue(defaulted.Success, defaulted.Error);
        Assert.AreEqual(10, service.LastSearchRequest!.Count);
        Assert.IsFalse(service.LastSearchRequest.NeedContent);
    }

    [TestMethod]
    public async Task ExecuteAsync_Renders_Optional_Fields_And_Handles_Missing_Values()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService
        {
            SearchHandler = _ => DeepSeekWebResult<DeepSeekSearchResponse>.Ok(new DeepSeekSearchResponse
            {
                Results =
                [
                    new DeepSeekSearchResult
                    {
                        Title = "First result",
                        Url = "https://first.example/",
                        Date = "2026-10-01",
                        SiteName = "First Site",
                        Authority = 4,
                        RerankScore = 2.5,
                        Snippet = "first snippet",
                        Summary = "first summary",
                        Content = "first content",
                    },
                    new DeepSeekSearchResult
                    {
                        Title = "",
                        Url = "https://second.example/",
                        Authority = -1,
                        RerankScore = 0,
                    },
                ],
            }),
        };
        var tool = DeepSeekWebTestTools.CreateSearchTool(temp, service);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"hello"}""");

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "1. First result");
        StringAssert.Contains(result.Output, "URL: https://first.example/");
        StringAssert.Contains(result.Output, "Date: 2026-10-01");
        StringAssert.Contains(result.Output, "Site: First Site");
        StringAssert.Contains(result.Output, "Authority: 4");
        StringAssert.Contains(result.Output, "Rerank: 2.5");
        StringAssert.Contains(result.Output, "Snippet: first snippet");
        StringAssert.Contains(result.Output, "Summary: first summary");
        StringAssert.Contains(result.Output, "Content: first content");
        StringAssert.Contains(result.Output, "2. (untitled)");
        StringAssert.Contains(result.Output, "URL: https://second.example/");
    }

    [TestMethod]
    public async Task ExecuteAsync_Renders_No_Results_Marker()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var tool = DeepSeekWebTestTools.CreateSearchTool(temp, new StubDeepSeekWebService());

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"nothing"}""");

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "DeepSeek Search results for: \"nothing\" (0 results)");
        StringAssert.Contains(result.Output, "(no results)");
    }

    [TestMethod]
    public async Task ExecuteAsync_Truncates_Output_At_Bounded_Limit()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var hugeContent = new string('x', 20_000);
        var service = new StubDeepSeekWebService
        {
            SearchHandler = _ => DeepSeekWebResult<DeepSeekSearchResponse>.Ok(new DeepSeekSearchResponse
            {
                Results = Enumerable.Range(1, 10)
                    .Select(i => new DeepSeekSearchResult
                    {
                        Title = $"result {i}",
                        Url = $"https://example.com/{i}",
                        Content = hugeContent,
                    })
                    .ToArray(),
            }),
        };
        var tool = DeepSeekWebTestTools.CreateSearchTool(temp, service);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"big","need_content":true}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsTrue(
            result.Output.Length <= 60_000,
            $"输出应被有界截断到 60000 字符以内，实际 {result.Output.Length}。");
        StringAssert.Contains(result.Output, "truncated at 60000 chars");
        StringAssert.Contains(result.Output, "truncated at 8000 chars");
    }

    [TestMethod]
    public async Task ExecuteAsync_Maps_Api_Errors_To_Actionable_Failures()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService();
        var tool = DeepSeekWebTestTools.CreateSearchTool(temp, service);

        service.SearchHandler = _ => DeepSeekWebResult<DeepSeekSearchResponse>.Fail(
            new DeepSeekWebError
            {
                Kind = DeepSeekWebErrorKind.Unauthorized,
                Message = "Unauthorized upstream.",
                HttpStatusCode = 401,
            });
        var unauthorized = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"hello"}""");
        Assert.IsFalse(unauthorized.Success);
        StringAssert.Contains(unauthorized.Error, "HTTP 401");
        StringAssert.Contains(unauthorized.Error, "API key");
        Assert.AreEqual(401, unauthorized.ExitCode, "HTTP 状态码必须透出。");

        service.SearchHandler = _ => DeepSeekWebResult<DeepSeekSearchResponse>.Fail(
            new DeepSeekWebError
            {
                Kind = DeepSeekWebErrorKind.InsufficientBalance,
                Message = "Insufficient balance.",
                HttpStatusCode = 402,
            });
        var balance = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"hello"}""");
        Assert.IsFalse(balance.Success);
        StringAssert.Contains(balance.Error, "HTTP 402");
        StringAssert.Contains(balance.Error, "balance");
        Assert.AreEqual(402, balance.ExitCode);

        service.SearchHandler = _ => DeepSeekWebResult<DeepSeekSearchResponse>.Fail(
            new DeepSeekWebError
            {
                Kind = DeepSeekWebErrorKind.RateLimited,
                Message = "Rate limited.",
                HttpStatusCode = 429,
            });
        var rateLimited = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"hello"}""");
        Assert.IsFalse(rateLimited.Success);
        StringAssert.Contains(rateLimited.Error, "HTTP 429");
        StringAssert.Contains(rateLimited.Error, "rate limit");
        Assert.AreEqual(429, rateLimited.ExitCode);
    }

    [TestMethod]
    public async Task ExecuteAsync_Fails_When_Credentials_Are_Not_Configured()
    {
        using var temp = new DeepSeekTempDirectory();
        var service = new StubDeepSeekWebService();
        var tool = DeepSeekWebTestTools.CreateSearchTool(temp, service);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, """{"query":"hello"}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, temp.SearchProvidersPath);
        StringAssert.Contains(result.Error, temp.LlmProvidersPath);
        Assert.AreEqual(0, service.SearchCalls.Count);
    }
}
