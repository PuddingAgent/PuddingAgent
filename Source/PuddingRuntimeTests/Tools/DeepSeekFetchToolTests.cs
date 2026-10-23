using System.Security.Authentication;
using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingDeepSeekWeb;
using PuddingRuntime.Services.Tools;
using PuddingRuntimeTests.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <c>deepseek_fetch</c> 的契约测试：Descriptor/权限分类、配置 → 线上请求形状、URL 校验、
/// 404/403 错误映射、渲染与有界截断、未配置凭据时不发请求。
/// </summary>
[TestClass]
public sealed class DeepSeekFetchToolTests
{
    private const string ExampleUrl = "https://example.com/article";

    private const string FetchResponseJson = """
        {
          "title": "Example Domain",
          "content": "# Example\n\nThis is the fetched Markdown body."
        }
        """;

    [TestMethod]
    public void Descriptor_Is_Low_Risk_ReadOnly_Network_Tool()
    {
        using var temp = new DeepSeekTempDirectory();
        var tool = DeepSeekWebTestTools.CreateFetchTool(temp, new StubDeepSeekWebService());

        Assert.AreEqual("deepseek_fetch", tool.Descriptor.ToolId);
        Assert.AreEqual("DeepSeek Fetch", tool.Descriptor.Name);
        Assert.AreEqual(ToolCategory.Network, tool.Descriptor.Category);
        Assert.AreEqual(ToolPermissionLevel.Low, tool.Descriptor.PermissionLevel);
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly));
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.ConcurrencySafe));
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresNetwork));
        Assert.IsTrue(tool.Descriptor.IsEnabledByDefault);
        Assert.AreEqual(57, tool.Descriptor.SortOrder);

        CollectionAssert.Contains(tool.Descriptor.Parameters.Required.ToList(), "url");
        Assert.AreEqual(1, tool.Descriptor.Parameters.Properties.Count);
        Assert.AreEqual("url", tool.Descriptor.Parameters.Properties[0].Name);
        // 2026-10-17 实测（上游正文转义 / 站点软 404 仍成功）后写入工具自述的使用约束。
        StringAssert.Contains(tool.Descriptor.Description, "按原文返回");
        StringAssert.Contains(tool.Descriptor.Description, "Page Not Found");
    }

    [TestMethod]
    public void Registry_Auto_Exposes_DeepSeek_Fetch_Without_Template_Grant()
    {
        using var temp = new DeepSeekTempDirectory();
        var tool = DeepSeekWebTestTools.CreateFetchTool(temp, new StubDeepSeekWebService());
        var policy = new ToolPermissionPolicyService();
        var registry = new PuddingToolRegistry([tool], policy);

        var available = registry.ListAvailable(new CapabilityPolicy()).Select(d => d.ToolId).ToArray();
        var decision = policy.Classify(tool.Descriptor);

        CollectionAssert.Contains(available, "deepseek_fetch");
        Assert.AreEqual(ToolPermissionTier.AutoAllowed, decision.Tier);
        Assert.IsFalse(decision.RequiresRuntimeAuthorization);
        Assert.IsTrue(decision.RequiresNetworkAccess);
    }

    [TestMethod]
    public async Task ExecuteAsync_Reads_SearchProvidersConfig_AndPosts_FetchRequest()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig(
            apiKey: "test-key",
            baseUrl: "https://custom.deepseek.local"));
        var webClient = new RecordingWebClient(
            DeepSeekWebTestTools.JsonResponse(200, FetchResponseJson));
        var tool = DeepSeekWebTestTools.CreateFetchTool(
            temp, DeepSeekWebTestTools.CreateRealService(webClient));

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, $"{{\"url\":\"{ExampleUrl}\"}}");

        Assert.IsTrue(result.Success, result.Error);
        var request = webClient.LastRequest!;
        Assert.AreEqual("https://custom.deepseek.local/v1/tools/fetch", request.Url);
        Assert.AreEqual("POST", request.Method);
        Assert.AreEqual("Bearer test-key", request.Headers["Authorization"]);
        Assert.AreEqual("application/json", request.ContentType);

        using var body = JsonDocument.Parse(request.Body!);
        Assert.AreEqual(ExampleUrl, body.RootElement.GetProperty("url").GetString());

        StringAssert.Contains(result.Output, "DeepSeek Fetch: Example Domain");
        StringAssert.Contains(result.Output, $"URL: {ExampleUrl}");
        StringAssert.Contains(result.Output, "# Example");
        StringAssert.Contains(result.Output, "This is the fetched Markdown body.");
    }

    [TestMethod]
    public async Task ExecuteAsync_Rejects_Invalid_Urls_Before_Service_Call()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService();
        var tool = DeepSeekWebTestTools.CreateFetchTool(temp, service);

        var cases = new (string Url, string ExpectedFragment)[]
        {
            ("", "Url is required."),
            ("   ", "Url is required."),
            ("not a url", "absolute http(s) URL"),
            ("ftp://example.com/file", "http or https"),
            ("https://user:pass@example.com/", "username or password"),
            ("https://" + new string('a', 8_200) + ".example/", "at most 8192 characters"),
        };

        foreach (var (url, expectedFragment) in cases)
        {
            var result = await DeepSeekWebTestTools.ExecuteAsync(
                tool, JsonSerializer.Serialize(new { url }));

            Assert.IsFalse(result.Success, $"URL 应该被拒绝：{url}");
            StringAssert.Contains(result.Error, expectedFragment);
        }

        Assert.AreEqual(0, service.FetchCalls.Count, "非法 URL 不得触达服务层。");
    }

    [TestMethod]
    public async Task ExecuteAsync_Maps_404_To_Actionable_Not_Found_Failure()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService
        {
            FetchHandler = _ => DeepSeekWebResult<DeepSeekFetchResponse>.Fail(new DeepSeekWebError
            {
                Kind = DeepSeekWebErrorKind.NotFound,
                Message = "Fetch 未获取到可用正文。",
                HttpStatusCode = 404,
            }),
        };
        var tool = DeepSeekWebTestTools.CreateFetchTool(temp, service);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, $"{{\"url\":\"{ExampleUrl}\"}}");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "HTTP 404");
        StringAssert.Contains(result.Error, "unavailable");
        StringAssert.Contains(result.Error, "http_fetch");
        Assert.AreEqual(404, result.ExitCode);
    }

    [TestMethod]
    public async Task ExecuteAsync_Maps_403_To_Security_Policy_Failure()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService
        {
            FetchHandler = _ => DeepSeekWebResult<DeepSeekFetchResponse>.Fail(new DeepSeekWebError
            {
                Kind = DeepSeekWebErrorKind.Forbidden,
                Message = "Blocked by safety policy.",
                HttpStatusCode = 403,
            }),
        };
        var tool = DeepSeekWebTestTools.CreateFetchTool(temp, service);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, $"{{\"url\":\"{ExampleUrl}\"}}");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "HTTP 403");
        StringAssert.Contains(result.Error, "security policy");
        Assert.AreEqual(403, result.ExitCode);
    }

    [TestMethod]
    public async Task ExecuteAsync_Truncates_Long_Content_At_Bounded_Limit()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService
        {
            FetchHandler = _ => DeepSeekWebResult<DeepSeekFetchResponse>.Ok(new DeepSeekFetchResponse
            {
                Title = "Huge page",
                Content = new string('y', 70_000),
            }),
        };
        var tool = DeepSeekWebTestTools.CreateFetchTool(temp, service);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, $"{{\"url\":\"{ExampleUrl}\"}}");

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsTrue(
            result.Output.Length <= 60_000,
            $"输出应被有界截断到 60000 字符以内，实际 {result.Output.Length}。");
        StringAssert.Contains(result.Output, "truncated at 60000 chars");
        StringAssert.Contains(result.Output, "DeepSeek Fetch: Huge page");
    }

    [TestMethod]
    public async Task ExecuteAsync_Transport_Failure_Explains_The_Next_Move()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService
        {
            FetchHandler = _ => throw new HttpRequestException(
                "The SSL connection could not be established, see inner exception.",
                new AuthenticationException("The remote certificate is invalid.")),
        };
        var tool = DeepSeekWebTestTools.CreateFetchTool(temp, service);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, $"{{\"url\":\"{ExampleUrl}\"}}");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "TLS handshake failed");
        StringAssert.Contains(result.Error, "http_fetch");
        // The suggestion must not send the agent back to the tool that just failed.
        Assert.IsFalse(
            result.Error.Contains("use deepseek_fetch", StringComparison.OrdinalIgnoreCase),
            result.Error);
        // The Flurl placeholder is useless once the chain is walked; the real cause must reach the agent.
        Assert.IsFalse(
            result.Error.Contains("see inner exception", StringComparison.OrdinalIgnoreCase),
            result.Error);
        StringAssert.Contains(result.Error, "remote certificate");
    }

    [TestMethod]
    public async Task ExecuteAsync_Renders_No_Content_Marker()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders(DeepSeekWebTestTools.SearchConfig());
        var service = new StubDeepSeekWebService
        {
            FetchHandler = _ => DeepSeekWebResult<DeepSeekFetchResponse>.Ok(new DeepSeekFetchResponse()),
        };
        var tool = DeepSeekWebTestTools.CreateFetchTool(temp, service);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, $"{{\"url\":\"{ExampleUrl}\"}}");

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "DeepSeek Fetch: (untitled)");
        StringAssert.Contains(result.Output, "(no content returned)");
    }

    [TestMethod]
    public async Task ExecuteAsync_Fails_When_Credentials_Are_Not_Configured()
    {
        using var temp = new DeepSeekTempDirectory();
        var service = new StubDeepSeekWebService();
        var tool = DeepSeekWebTestTools.CreateFetchTool(temp, service);

        var result = await DeepSeekWebTestTools.ExecuteAsync(tool, $"{{\"url\":\"{ExampleUrl}\"}}");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, temp.SearchProvidersPath);
        StringAssert.Contains(result.Error, temp.LlmProvidersPath);
        Assert.AreEqual(0, service.FetchCalls.Count);
    }
}
