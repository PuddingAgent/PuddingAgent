using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class AnySearchSearchToolTests
{
    [TestMethod]
    public void Descriptor_Is_Low_Risk_ReadOnly_Network_Tool()
    {
        using var temp = new TempDirectory();
        var tool = CreateTool(temp.Paths, new RecordingWebClient(SuccessResponse()));

        Assert.AreEqual("anysearch_search", tool.Descriptor.ToolId);
        Assert.AreEqual(ToolPermissionLevel.Low, tool.Descriptor.PermissionLevel);
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly));
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.ConcurrencySafe));
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresNetwork));
        Assert.IsTrue(tool.Descriptor.IsEnabledByDefault);
    }

    [TestMethod]
    public void Registry_Auto_Exposes_AnySearch_Search_Without_Template_Grant()
    {
        using var temp = new TempDirectory();
        var tool = CreateTool(temp.Paths, new RecordingWebClient(SuccessResponse()));
        var registry = new PuddingToolRegistry([tool], new ToolPermissionPolicyService());

        var available = registry.ListAvailable(new CapabilityPolicy()).Select(d => d.ToolId).ToArray();
        var decision = new ToolPermissionPolicyService().Classify(tool.Descriptor);

        CollectionAssert.Contains(available, "anysearch_search");
        Assert.AreEqual(ToolPermissionTier.AutoAllowed, decision.Tier);
        Assert.IsFalse(decision.RequiresRuntimeAuthorization);
        Assert.IsTrue(decision.RequiresNetworkAccess);
    }

    [TestMethod]
    public void Schema_Includes_AnySearch_Search_When_Policy_Is_Empty()
    {
        using var temp = new TempDirectory();
        var tool = CreateTool(temp.Paths, new RecordingWebClient(SuccessResponse()));
        var registry = new PuddingToolRegistry([tool], new ToolPermissionPolicyService());
        var schema = new PuddingToolSchemaService(registry);

        var tools = schema.BuildLlmTools(new CapabilityPolicy());

        Assert.IsTrue(tools.Any(t => t.Name == "anysearch_search"));
    }

    [TestMethod]
    public async Task ExecuteAsync_Reads_ApiKey_From_Config_File_And_Posts_Search_Request()
    {
        using var temp = new TempDirectory();
        await WriteSearchConfigAsync(temp.Paths, """
            {
              "anysearch": {
                "enabled": true,
                "baseUrl": "https://custom.anysearch.local",
                "apiKey": "test-key"
              }
            }
            """);
        var webClient = new RecordingWebClient(SuccessResponse());
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """
            {
              "query": "Go 1.22 release notes",
              "max_results": 5,
              "domain": "code",
              "tag": "code.doc",
              "content_types": ["web", "doc"],
              "zone": "intl",
              "language": "en",
              "params": { "ticker": "AAPL" }
            }
            """);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(webClient.LastRequest);
        Assert.AreEqual("https://custom.anysearch.local/v1/search", webClient.LastRequest!.Url);
        Assert.AreEqual("POST", webClient.LastRequest.Method);
        Assert.AreEqual("Bearer test-key", webClient.LastRequest.Headers["Authorization"]);
        Assert.AreEqual("application/json", webClient.LastRequest.ContentType);

        using var body = JsonDocument.Parse(webClient.LastRequest.Body!);
        var root = body.RootElement;
        Assert.AreEqual("Go 1.22 release notes", root.GetProperty("query").GetString());
        Assert.AreEqual(5, root.GetProperty("max_results").GetInt32());
        Assert.AreEqual("code", root.GetProperty("domain").GetString());
        Assert.AreEqual("code.doc", root.GetProperty("tag").GetString());
        Assert.AreEqual("web", root.GetProperty("content_types")[0].GetString());
        Assert.AreEqual("doc", root.GetProperty("content_types")[1].GetString());
        Assert.AreEqual("intl", root.GetProperty("zone").GetString());
        Assert.AreEqual("en", root.GetProperty("language").GetString());
        Assert.AreEqual("AAPL", root.GetProperty("params").GetProperty("ticker").GetString());

        StringAssert.Contains(result.Output, "Go 1.22 Release Notes");
        StringAssert.Contains(result.Output, "https://go.dev/doc/go1.22");
        StringAssert.Contains(result.Output, "request_id=req_abc123");
    }

    [TestMethod]
    public async Task ExecuteAsync_Fails_When_ApiKey_Is_Not_Configured()
    {
        using var temp = new TempDirectory();
        var webClient = new RecordingWebClient(SuccessResponse());
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """{"query":"hello","max_results":1}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "AnySearch API key is not configured.");
        StringAssert.Contains(result.Error, temp.Paths.SystemConfigFile("search.providers.json"));
        Assert.IsNull(webClient.LastRequest);
    }

    [TestMethod]
    public async Task ExecuteAsync_Maps_AnySearch_Error_Response_To_Tool_Failure()
    {
        using var temp = new TempDirectory();
        await WriteSearchConfigAsync(temp.Paths, """
            {
              "anysearch": {
                "enabled": true,
                "baseUrl": "https://api.anysearch.com",
                "apiKey": "test-key"
              }
            }
            """);
        var webClient = new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 429,
            ReasonPhrase = "Too Many Requests",
            ContentType = "application/json",
            Body = """
                   {
                     "code": 42901,
                     "message": "Rate limit exceeded.",
                     "data": {
                       "request_id": "req_rate",
                       "retry_after": 60
                     }
                   }
                   """,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            FinalUrl = "https://api.anysearch.com/v1/search",
        });
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """{"query":"hello"}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "HTTP 429 Too Many Requests");
        StringAssert.Contains(result.Error, "Rate limit exceeded.");
        StringAssert.Contains(result.Error, "request_id=req_rate");
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

    private static AnySearchSearchTool CreateTool(PuddingDataPaths paths, RecordingWebClient webClient) =>
        new(webClient, paths, NullLogger<AnySearchSearchTool>.Instance);

    private static Task<ToolExecutionResult> ExecuteAsync(AnySearchSearchTool tool, string argumentsJson) =>
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
                 "code": 0,
                 "message": "success",
                 "data": {
                   "results": [
                     {
                       "title": "Go 1.22 Release Notes",
                       "url": "https://go.dev/doc/go1.22",
                       "snippet": "Go 1.22 is a major release...",
                       "content": "Detailed content here..."
                     }
                   ],
                   "metadata": {
                     "request_id": "req_abc123",
                     "total_results": 1,
                     "search_time_ms": 342
                   }
                 }
               }
               """,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        FinalUrl = "https://api.anysearch.com/v1/search",
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
            "pudding-anysearch-tests",
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
