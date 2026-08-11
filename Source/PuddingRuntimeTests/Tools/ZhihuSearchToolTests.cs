using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class ZhihuSearchToolTests
{
    [TestMethod]
    public void Descriptor_Is_Low_Risk_ReadOnly_Network_Tool()
    {
        using var temp = new TempDirectory();
        var tool = CreateTool(temp.Paths, new RecordingWebClient(SuccessResponse()));

        Assert.AreEqual("zhihu_search", tool.Descriptor.ToolId);
        Assert.AreEqual(ToolPermissionLevel.Low, tool.Descriptor.PermissionLevel);
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly));
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.ConcurrencySafe));
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresNetwork));
        Assert.IsTrue(tool.Descriptor.IsEnabledByDefault);
    }

    [TestMethod]
    public void Registry_Auto_Exposes_Zhihu_Search_Without_Template_Grant()
    {
        using var temp = new TempDirectory();
        var tool = CreateTool(temp.Paths, new RecordingWebClient(SuccessResponse()));
        var registry = new PuddingToolRegistry([tool], new ToolPermissionPolicyService());

        var available = registry.ListAvailable(new CapabilityPolicy()).Select(d => d.ToolId).ToArray();
        var decision = new ToolPermissionPolicyService().Classify(tool.Descriptor);

        CollectionAssert.Contains(available, "zhihu_search");
        Assert.AreEqual(ToolPermissionTier.AutoAllowed, decision.Tier);
        Assert.IsFalse(decision.RequiresRuntimeAuthorization);
        Assert.IsTrue(decision.RequiresNetworkAccess);
    }

    [TestMethod]
    public async Task ExecuteAsync_Sends_Get_To_Developer_Api_With_Bearer_And_Timestamp()
    {
        using var temp = new TempDirectory();
        await WriteSearchConfigAsync(temp.Paths, """
            {
              "zhihu_search": {
                "enabled": true,
                "baseUrl": "https://zhihu.example.local/api/v1/content/zhihu_search",
                "apiKey": "test-key"
              }
            }
            """);
        var webClient = new RecordingWebClient(SuccessResponse());
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """{"query":"async C#","count":5}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(webClient.LastRequest);
        Assert.AreEqual("GET", webClient.LastRequest!.Method);
        Assert.AreEqual(
            "https://zhihu.example.local/api/v1/content/zhihu_search?Query=async%20C%23&Count=5",
            webClient.LastRequest.Url);
        Assert.AreEqual("Bearer test-key", webClient.LastRequest.Headers["Authorization"]);
        Assert.AreEqual("application/json", webClient.LastRequest.ContentType);

        var timestampHeader = webClient.LastRequest.Headers["X-Request-Timestamp"];
        Assert.IsTrue(long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp));
        Assert.IsTrue(timestamp > 0);

        StringAssert.Contains(result.Output, "Zhihu search results for: \"async C#\"");
        StringAssert.Contains(result.Output, "has_more=true");
        StringAssert.Contains(result.Output, "search_hash_id=hash_1");
        StringAssert.Contains(result.Output, "| # | Title | Type | Author | Votes | Comments | Edited | Ranking | URL |");
        StringAssert.Contains(
            result.Output,
            "| 1 | How does async work in C#? | answer | 张三 (优秀答主) | 42 | 3 | 2023-11-14 22:13 | 0.87 | https://www.zhihu.com/question/98765/answer/123456 |");
        StringAssert.Contains(result.Output, "Snippets:");
        StringAssert.Contains(result.Output, "1. Async is about non-blocking concurrency.");
    }

    [TestMethod]
    public async Task ExecuteAsync_Clamps_Count_To_Max_10()
    {
        using var temp = new TempDirectory();
        await WriteSearchConfigAsync(temp.Paths, """
            {
              "zhihu_search": { "enabled": true, "apiKey": "test-key" }
            }
            """);
        var webClient = new RecordingWebClient(SuccessResponse());
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """{"query":"hello","count":99}""");

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(webClient.LastRequest!.Url, "Count=10");
    }

    [TestMethod]
    public async Task ExecuteAsync_Returns_No_Results_Message()
    {
        using var temp = new TempDirectory();
        await WriteSearchConfigAsync(temp.Paths, """
            {
              "zhihu_search": { "enabled": true, "apiKey": "test-key" }
            }
            """);
        var webClient = new RecordingWebClient(EmptyResponse());
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """{"query":"nothing"}""");

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "Zhihu search results for: \"nothing\"");
        StringAssert.Contains(result.Output, "(no results)");
    }

    [TestMethod]
    public async Task ExecuteAsync_Fails_When_ApiKey_Is_Not_Configured()
    {
        using var temp = new TempDirectory();
        var webClient = new RecordingWebClient(SuccessResponse());
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """{"query":"hello"}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Zhihu API key is not configured.");
        StringAssert.Contains(result.Error, temp.Paths.SystemConfigFile("search.providers.json"));
        Assert.IsNull(webClient.LastRequest);
    }

    [TestMethod]
    public async Task ExecuteAsync_Fails_When_Provider_Is_Disabled()
    {
        using var temp = new TempDirectory();
        await WriteSearchConfigAsync(temp.Paths, """
            {
              "zhihu_search": { "enabled": false, "apiKey": "test-key" }
            }
            """);
        var webClient = new RecordingWebClient(SuccessResponse());
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """{"query":"hello"}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "disabled");
        Assert.IsNull(webClient.LastRequest);
    }

    [TestMethod]
    public async Task ExecuteAsync_Rejects_Empty_Query_Before_Transport()
    {
        using var temp = new TempDirectory();
        var webClient = new RecordingWebClient(SuccessResponse());
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """{"query":"   "}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Query is required.");
        Assert.IsNull(webClient.LastRequest);
    }

    [TestMethod]
    public async Task ExecuteAsync_Maps_Provider_Error_Code_To_Tool_Failure()
    {
        using var temp = new TempDirectory();
        await WriteSearchConfigAsync(temp.Paths, """
            {
              "zhihu_search": { "enabled": true, "apiKey": "test-key" }
            }
            """);
        var webClient = new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            ContentType = "application/json",
            Body = """{ "Code": 40001, "Message": "Bad query." }""",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            FinalUrl = ZhihuSearchTool.DefaultEndpoint,
        });
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """{"query":"hello"}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "code=40001");
        StringAssert.Contains(result.Error, "Bad query.");
    }

    [TestMethod]
    public async Task ExecuteAsync_Maps_Non_Json_Error_Response_To_Tool_Failure()
    {
        using var temp = new TempDirectory();
        await WriteSearchConfigAsync(temp.Paths, """
            {
              "zhihu_search": { "enabled": true, "apiKey": "test-key" }
            }
            """);
        var webClient = new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 500,
            ReasonPhrase = "Internal Server Error",
            ContentType = "text/plain",
            Body = "upstream exploded",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            FinalUrl = ZhihuSearchTool.DefaultEndpoint,
        });
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """{"query":"hello"}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "HTTP 500 Internal Server Error");
        StringAssert.Contains(result.Error, "upstream exploded");
    }

    private static ZhihuSearchTool CreateTool(PuddingDataPaths paths, RecordingWebClient webClient) =>
        new(webClient, paths, NullLogger<ZhihuSearchTool>.Instance);

    private static Task<ToolExecutionResult> ExecuteAsync(ZhihuSearchTool tool, string argumentsJson) =>
        tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = argumentsJson,
            Context = new ToolExecutionContext
            {
                WorkspaceId = "workspace-1",
                SessionId = "session-1",
                AgentInstanceId = "agent-1",
            },
        });

    private static async Task WriteSearchConfigAsync(PuddingDataPaths paths, string content)
    {
        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(paths.SystemConfigFile("search.providers.json"), content);
    }

    private static WebClientResponse SuccessResponse() => new()
    {
        StatusCode = 200,
        ReasonPhrase = "OK",
        ContentType = "application/json",
        Body = """
               {
                 "Code": 0,
                 "Message": "success",
                 "Data": {
                   "HasMore": true,
                   "SearchHashId": "hash_1",
                   "Items": [
                     {
                       "Title": "How does async work in C#?",
                       "ContentType": "answer",
                       "ContentID": "123456",
                       "ContentText": "<em>Async</em> is about non-blocking concurrency.",
                       "Url": "https://www.zhihu.com/question/98765/answer/123456",
                       "CommentCount": 3,
                       "VoteUpCount": 42,
                       "AuthorName": "张三",
                       "AuthorBadgeText": "优秀答主",
                       "EditTime": 1700000000,
                       "AuthorityLevel": "expert",
                       "RankingScore": 0.87
                     }
                   ]
                 }
               }
               """,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        FinalUrl = ZhihuSearchTool.DefaultEndpoint,
    };

    private static WebClientResponse EmptyResponse() => new()
    {
        StatusCode = 200,
        ReasonPhrase = "OK",
        ContentType = "application/json",
        Body = """{ "Code": 0, "Message": "success", "Data": { "HasMore": false, "Items": [] } }""",
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        FinalUrl = ZhihuSearchTool.DefaultEndpoint,
    };

    private sealed class RecordingWebClient(WebClientResponse response) : IWebClient
    {
        public WebClientRequest? LastRequest { get; private set; }

        public Task<WebClientResponse> SendAsync(WebClientRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pudding-zhihu-tests",
            Guid.NewGuid().ToString("N"));

        public PuddingDataPaths Paths { get; }

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
            Paths = PuddingDataPaths.FromRoot(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
