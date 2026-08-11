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
public sealed class ZhihuGlobalSearchToolTests
{
    [TestMethod]
    public void Descriptor_Is_Low_Risk_ReadOnly_Network_Tool()
    {
        using var temp = new TempDirectory();
        var tool = CreateTool(temp.Paths, new RecordingWebClient(SuccessResponse()));

        Assert.AreEqual("zhihu_global_search", tool.Descriptor.ToolId);
        Assert.AreEqual(ToolPermissionLevel.Low, tool.Descriptor.PermissionLevel);
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly));
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.ConcurrencySafe));
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresNetwork));
        Assert.IsTrue(tool.Descriptor.IsEnabledByDefault);
    }

    [TestMethod]
    public void Registry_Auto_Exposes_Zhihu_Global_Search_Without_Template_Grant()
    {
        using var temp = new TempDirectory();
        var tool = CreateTool(temp.Paths, new RecordingWebClient(SuccessResponse()));
        var registry = new PuddingToolRegistry([tool], new ToolPermissionPolicyService());

        var available = registry.ListAvailable(new CapabilityPolicy()).Select(d => d.ToolId).ToArray();
        var decision = new ToolPermissionPolicyService().Classify(tool.Descriptor);

        CollectionAssert.Contains(available, "zhihu_global_search");
        Assert.AreEqual(ToolPermissionTier.AutoAllowed, decision.Tier);
        Assert.IsFalse(decision.RequiresRuntimeAuthorization);
        Assert.IsTrue(decision.RequiresNetworkAccess);
    }

    [TestMethod]
    public async Task ExecuteAsync_Sends_Get_With_Filter_And_SearchDb()
    {
        using var temp = new TempDirectory();
        await WriteSearchConfigAsync(temp.Paths, """
            {
              "zhihu_search": {
                "enabled": true,
                "baseUrl": "https://zhihu.example.local/api/v1/content/global_search",
                "apiKey": "test-key"
              }
            }
            """);
        var webClient = new RecordingWebClient(SuccessResponse());
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """
            {
              "query": "net core",
              "count": 3,
              "filter": "host==\"zhihu.com\"",
              "search_db": "realtime"
            }
            """);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(webClient.LastRequest);
        Assert.AreEqual("GET", webClient.LastRequest!.Method);
        var url = webClient.LastRequest.Url;
        Assert.IsTrue(url.StartsWith("https://zhihu.example.local/api/v1/content/global_search?"), url);
        StringAssert.Contains(url, "Query=net%20core");
        StringAssert.Contains(url, "Count=3");
        StringAssert.Contains(url, "Filter=host%3D%3D%22zhihu.com%22");
        StringAssert.Contains(url, "SearchDB=realtime");
        Assert.AreEqual("Bearer test-key", webClient.LastRequest.Headers["Authorization"]);
        Assert.AreEqual("application/json", webClient.LastRequest.ContentType);

        var timestampHeader = webClient.LastRequest.Headers["X-Request-Timestamp"];
        Assert.IsTrue(long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp));
        Assert.IsTrue(timestamp > 0);

        StringAssert.Contains(result.Output, "Zhihu global search results for: \"net core\"");
        StringAssert.Contains(result.Output, "| # | Title | Type | Author | Votes | Comments | Edited | URL |");
        StringAssert.Contains(
            result.Output,
            "| 1 | What is .NET 9? | article | 李四 (知乎专栏) | 12 | 1 | 2023-11-14 22:13 | https://zhuanlan.zhihu.com/p/7788 |");
        StringAssert.Contains(result.Output, "1. .NET 9 brings new features to the runtime.");
    }

    [TestMethod]
    public async Task ExecuteAsync_Clamps_Count_To_Max_20()
    {
        using var temp = new TempDirectory();
        await WriteSearchConfigAsync(temp.Paths, """
            {
              "zhihu_search": { "enabled": true, "apiKey": "test-key" }
            }
            """);
        var webClient = new RecordingWebClient(SuccessResponse());
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """{"query":"hello","count":100}""");

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(webClient.LastRequest!.Url, "Count=20");
    }

    [TestMethod]
    public async Task ExecuteAsync_Rejects_Invalid_SearchDb_Before_Transport()
    {
        using var temp = new TempDirectory();
        await WriteSearchConfigAsync(temp.Paths, """
            {
              "zhihu_search": { "enabled": true, "apiKey": "test-key" }
            }
            """);
        var webClient = new RecordingWebClient(SuccessResponse());
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """{"query":"hello","search_db":"nope"}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "SearchDB must be one of: all, realtime, static.");
        Assert.IsNull(webClient.LastRequest);
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

        var result = await ExecuteAsync(tool, """{"query":""}""");

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
            Body = """{ "Code": 40002, "Message": "Invalid filter expression." }""",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            FinalUrl = ZhihuGlobalSearchTool.DefaultEndpoint,
        });
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """{"query":"hello"}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "code=40002");
        StringAssert.Contains(result.Error, "Invalid filter expression.");
    }

    private static ZhihuGlobalSearchTool CreateTool(PuddingDataPaths paths, RecordingWebClient webClient) =>
        new(webClient, paths, NullLogger<ZhihuGlobalSearchTool>.Instance);

    private static Task<ToolExecutionResult> ExecuteAsync(ZhihuGlobalSearchTool tool, string argumentsJson) =>
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
                   "HasMore": false,
                   "Items": [
                     {
                       "Title": "What is .NET 9?",
                       "ContentType": "article",
                       "ContentID": "7788",
                       "ContentText": ".NET 9 brings new features to the <em>runtime</em>.",
                       "Url": "https://zhuanlan.zhihu.com/p/7788",
                       "CommentCount": 1,
                       "VoteUpCount": 12,
                       "AuthorName": "李四",
                       "AuthorBadgeText": "知乎专栏",
                       "EditTime": 1700000000,
                       "AuthorityLevel": "normal"
                     }
                   ]
                 }
               }
               """,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        FinalUrl = ZhihuGlobalSearchTool.DefaultEndpoint,
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
            "pudding-zhihu-global-tests",
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
