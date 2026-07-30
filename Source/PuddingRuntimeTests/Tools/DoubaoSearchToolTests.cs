using System.Text.Json;
using Flurl.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class DoubaoSearchToolTests
{
    [TestMethod]
    public void Descriptor_Is_AutoAllowed_ReadOnly_Network_Tool()
    {
        using var temp = new TempDirectory();
        var tool = CreateTool(temp.Paths, new RecordingWebClient(SuccessResponse()));
        var registry = new PuddingToolRegistry([tool], new ToolPermissionPolicyService());

        var decision = new ToolPermissionPolicyService().Classify(tool.Descriptor);
        var available = registry.ListAvailable(new CapabilityPolicy()).Select(item => item.ToolId).ToArray();
        var schema = new PuddingToolSchemaService(registry).BuildLlmTools(new CapabilityPolicy());

        Assert.AreEqual("doubao_search", tool.Descriptor.ToolId);
        Assert.AreEqual(ToolPermissionLevel.Low, tool.Descriptor.PermissionLevel);
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly));
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.ConcurrencySafe));
        Assert.IsTrue(tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresNetwork));
        Assert.AreEqual(ToolPermissionTier.AutoAllowed, decision.Tier);
        CollectionAssert.Contains(available, "doubao_search");
        Assert.IsTrue(schema.Any(item => item.Name == "doubao_search"));
    }

    [TestMethod]
    public async Task Firewall_Denies_Doubao_Search_When_Network_Access_Is_Disabled()
    {
        var firewall = new AgentFirewall();

        var decision = await firewall.EvaluateAsync(new FirewallContext
        {
            WorkspaceId = "workspace-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-1",
            ToolId = "doubao_search",
            Policy = new CapabilityPolicy { AllowNetworkAccess = false },
            RuntimeMode = RuntimeExecutionMode.Normal,
        }, CancellationToken.None);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual(FirewallGate.Resource, decision.DeniedAtGate);
        StringAssert.Contains(decision.DenyReason, "requires network access");
    }

    [TestMethod]
    public async Task ExecuteAsync_Reads_Config_And_Maps_Global_Search_Request()
    {
        using var temp = new TempDirectory();
        await WriteSearchConfigAsync(temp.Paths, """
            {
              "doubao_search": {
                "enabled": true,
                "apiKey": "test-doubao-key"
              }
            }
            """);
        var webClient = new RecordingWebClient(SuccessResponse());
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """
            {
              "query": "北京周边景点",
              "doc_count": 2,
              "max_snippet_length": 800,
              "max_image_count_per_doc": 4
            }
            """);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(webClient.LastRequest);
        Assert.AreEqual(DoubaoSearchTool.DefaultEndpoint, webClient.LastRequest!.Url);
        Assert.AreEqual("POST", webClient.LastRequest.Method);
        Assert.AreEqual("Bearer test-doubao-key", webClient.LastRequest.Headers["Authorization"]);
        Assert.AreEqual("application/json", webClient.LastRequest.ContentType);

        using var body = JsonDocument.Parse(webClient.LastRequest.Body!);
        var root = body.RootElement;
        Assert.AreEqual("北京周边景点", root.GetProperty("Query").GetString());
        Assert.AreEqual(2, root.GetProperty("DocCount").GetInt32());
        Assert.AreEqual(800, root.GetProperty("MaxSnippetLength").GetInt32());
        Assert.AreEqual(4, root.GetProperty("MaxImageCountPerDoc").GetInt32());
        Assert.IsFalse(root.TryGetProperty("query", out _), "Doubao request fields must remain PascalCase.");

        StringAssert.Contains(result.Output, "天安门");
        StringAssert.Contains(result.Output, "https://example.test/tiananmen");
        StringAssert.Contains(result.Output, "Snippet: 第一段\n第二段");
        StringAssert.Contains(result.Output, "Image: https://example.test/image.jpg (1136x565) alt=\"天安门\"");
        StringAssert.Contains(result.Output, "Source: 示例百科");
        StringAssert.Contains(result.Output, "request_id=req-doubao-1");
        StringAssert.Contains(result.Output, "total_results=20 returned_results=1");
    }

    [TestMethod]
    public async Task ExecuteAsync_Fails_Before_Transport_When_Config_Is_Missing()
    {
        using var temp = new TempDirectory();
        var webClient = new RecordingWebClient(SuccessResponse());
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, """{"query":"hello"}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Doubao Search API key is not configured.");
        StringAssert.Contains(result.Error, temp.Paths.SystemConfigFile("search.providers.json"));
        Assert.IsNull(webClient.LastRequest);
    }

    [TestMethod]
    public async Task ExecuteAsync_Maps_Metadata_Error_Even_When_Http_Is_Successful()
    {
        using var temp = new TempDirectory();
        await WriteEnabledConfigAsync(temp.Paths);
        var tool = CreateTool(temp.Paths, new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            ContentType = "application/json",
            Body = """
                   {
                     "ResponseMetadata": {
                       "RequestId": "req-invalid-key",
                       "Error": {
                         "CodeN": 700901,
                         "Code": "700901",
                         "Message": "APIKey invalid"
                       }
                     },
                     "Result": null
                   }
                   """,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            FinalUrl = DoubaoSearchTool.DefaultEndpoint,
        }));

        var result = await ExecuteAsync(tool, """{"query":"hello"}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Doubao Search error code=700901: APIKey invalid");
        StringAssert.Contains(result.Error, "request_id=req-invalid-key");
    }

    [TestMethod]
    public async Task ExecuteAsync_Maps_Result_Error_And_Request_Id()
    {
        using var temp = new TempDirectory();
        await WriteEnabledConfigAsync(temp.Paths);
        var tool = CreateTool(temp.Paths, new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            ContentType = "application/json",
            Body = """
                   {
                     "ResponseMetadata": { "RequestId": "req-rate-limit" },
                     "Result": {
                       "TotalDocCount": 0,
                       "Documents": [],
                       "ErrorCode": 700429,
                       "ErrorMsg": "request rate exceeded"
                     }
                   }
                   """,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            FinalUrl = DoubaoSearchTool.DefaultEndpoint,
        }));

        var result = await ExecuteAsync(tool, """{"query":"hello"}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Doubao Search error code=700429: request rate exceeded");
        StringAssert.Contains(result.Error, "request_id=req-rate-limit");
    }

    [TestMethod]
    public async Task ExecuteAsync_Preserves_Provider_Error_On_NonSuccess_Http()
    {
        using var temp = new TempDirectory();
        await WriteEnabledConfigAsync(temp.Paths);
        var tool = CreateTool(temp.Paths, new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 403,
            ReasonPhrase = "Forbidden",
            ContentType = "application/json",
            Body = """
                   {
                     "ResponseMetadata": {
                       "RequestId": "req-forbidden",
                       "Error": { "Code": "10403", "Message": "permission denied" }
                     },
                     "Result": null
                   }
                   """,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            FinalUrl = DoubaoSearchTool.DefaultEndpoint,
        }));

        var result = await ExecuteAsync(tool, """{"query":"hello"}""");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(403, result.ExitCode);
        StringAssert.Contains(result.Error, "HTTP 403 Forbidden");
        StringAssert.Contains(result.Error, "Doubao Search error code=10403: permission denied");
        StringAssert.Contains(result.Error, "request_id=req-forbidden");
    }

    [TestMethod]
    public async Task ExecuteAsync_Bounds_Total_Output_Size()
    {
        using var temp = new TempDirectory();
        await WriteEnabledConfigAsync(temp.Paths);
        var documents = Enumerable.Range(0, 20).Select(index => new
        {
            Rank = index,
            Url = $"https://example.test/{index}",
            Title = $"Result {index}",
            Snippet = new[] { new { Type = "text", Text = new string('x', 7_000) } },
        });
        var body = JsonSerializer.Serialize(new
        {
            ResponseMetadata = new { RequestId = "req-large" },
            Result = new
            {
                TotalDocCount = 20,
                Documents = documents,
                ErrorCode = 0,
                ErrorMsg = "",
            },
        });
        var tool = CreateTool(temp.Paths, new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            ContentType = "application/json",
            Body = body,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            FinalUrl = DoubaoSearchTool.DefaultEndpoint,
        }));

        var result = await ExecuteAsync(tool, """{"query":"large response"}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsLessThanOrEqualTo(
            DoubaoSearchTool.MaxRenderedOutputChars + 64,
            result.Output.Length);
        StringAssert.Contains(result.Output, $"truncated at {DoubaoSearchTool.MaxRenderedOutputChars} chars");
    }

    [TestMethod]
    public async Task ExecuteAsync_Reports_Transport_Timeout()
    {
        using var temp = new TempDirectory();
        await WriteEnabledConfigAsync(temp.Paths);
        var tool = new DoubaoSearchTool(
            new ThrowingWebClient(_ => new OperationCanceledException()),
            temp.Paths,
            NullLogger<DoubaoSearchTool>.Instance);

        var result = await ExecuteAsync(tool, """{"query":"timeout"}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Doubao Search request timed out.");
    }

    [TestMethod]
    public async Task ExecuteAsync_Reports_Flurl_Transport_Timeout()
    {
        using var temp = new TempDirectory();
        await WriteEnabledConfigAsync(temp.Paths);
        var tool = new DoubaoSearchTool(
            new ThrowingWebClient(_ => new FlurlHttpTimeoutException(null!, new TimeoutException())),
            temp.Paths,
            NullLogger<DoubaoSearchTool>.Instance);

        var result = await ExecuteAsync(tool, """{"query":"flurl timeout"}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Doubao Search request timed out.");
    }

    [TestMethod]
    public async Task ExecuteAsync_Propagates_Caller_Cancellation()
    {
        using var temp = new TempDirectory();
        await WriteEnabledConfigAsync(temp.Paths);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var tool = new DoubaoSearchTool(
            new ThrowingWebClient(ct => new OperationCanceledException(ct)),
            temp.Paths,
            NullLogger<DoubaoSearchTool>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ExecuteAsync(tool, """{"query":"cancelled"}""", cts.Token));
    }

    [TestMethod]
    [DataRow("{\"query\":\"\"}", "Query is required.")]
    [DataRow("{\"query\":\"hello\",\"doc_count\":21}", "doc_count must be between 1 and 20.")]
    [DataRow("{\"query\":\"hello\",\"max_snippet_length\":3001}", "max_snippet_length must be between 1 and 3000.")]
    [DataRow("{\"query\":\"hello\",\"max_image_count_per_doc\":11}", "max_image_count_per_doc must be between 1 and 10.")]
    public async Task ExecuteAsync_Rejects_Invalid_Arguments_Before_Transport(
        string argumentsJson,
        string expectedError)
    {
        using var temp = new TempDirectory();
        var webClient = new RecordingWebClient(SuccessResponse());
        var tool = CreateTool(temp.Paths, webClient);

        var result = await ExecuteAsync(tool, argumentsJson);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, expectedError);
        Assert.IsNull(webClient.LastRequest);
    }

    private static DoubaoSearchTool CreateTool(PuddingDataPaths paths, RecordingWebClient webClient) =>
        new(webClient, paths, NullLogger<DoubaoSearchTool>.Instance);

    private static Task<ToolExecutionResult> ExecuteAsync(
        DoubaoSearchTool tool,
        string argumentsJson,
        CancellationToken ct = default) =>
        tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-doubao-1",
            ArgumentsJson = argumentsJson,
            Context = new ToolExecutionContext
            {
                WorkspaceId = "workspace-1",
                SessionId = "session-1",
                AgentInstanceId = "agent-1",
            },
        }, ct);

    private static Task WriteEnabledConfigAsync(PuddingDataPaths paths) =>
        WriteSearchConfigAsync(paths, """
            {
              "doubao_search": {
                "enabled": true,
                "apiKey": "test-doubao-key"
              }
            }
            """);

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
                 "ResponseMetadata": {
                   "RequestId": "req-doubao-1",
                   "Action": "",
                   "Version": "",
                   "Service": "",
                   "Region": ""
                 },
                 "Result": {
                   "TotalDocCount": 20,
                   "Documents": [
                     {
                       "Rank": 0,
                       "Url": "https://example.test/tiananmen",
                       "Title": "天安门",
                       "Snippet": [
                         { "Type": "text", "Text": "第一段" },
                         {
                           "Type": "image",
                           "Image": {
                             "Width": 1136,
                             "Height": 565,
                             "ImageUrl": "https://example.test/image.jpg",
                             "Alt": "天安门"
                           }
                         },
                         { "Type": "text", "Text": "第二段" }
                       ],
                       "DocumentInfo": {
                         "ContentCharCount": 1000,
                         "ContentTokenCount": 500,
                         "Filetype": "webpage",
                         "PublishTime": "2026-07-30"
                       },
                       "HostInfo": {
                         "Hostname": "示例百科",
                         "IconUrl": "https://example.test/icon.jpg"
                       },
                       "UnknownFutureField": true
                     }
                   ],
                   "ErrorCode": 0,
                   "ErrorMsg": ""
                 }
               }
               """,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        FinalUrl = DoubaoSearchTool.DefaultEndpoint,
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

    private sealed class ThrowingWebClient(Func<CancellationToken, Exception> exceptionFactory) : IWebClient
    {
        public Task<WebClientResponse> SendAsync(WebClientRequest request, CancellationToken ct) =>
            Task.FromException<WebClientResponse>(exceptionFactory(ct));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pudding-doubao-search-tests",
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
