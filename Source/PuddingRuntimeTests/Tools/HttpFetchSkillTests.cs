using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class HttpFetchSkillTests
{
    [TestMethod]
    public async Task Markdown_Output_Extracts_Readable_Html()
    {
        var html = """
                   <html>
                     <body>
                       <nav>navigation noise</nav>
                       <main>
                         <h1>Article Title</h1>
                         <p>Hello <a href="https://example.com/docs">docs</a>.</p>
                         <script>alert('noise')</script>
                       </main>
                     </body>
                   </html>
                   """;
        var converter = new RecordingHtmlToMarkdownConverter("# Article Title\n\nHello [docs](https://example.com/docs).");
        var webClient = new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            ContentType = "text/html; charset=utf-8",
            Body = html,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            FinalUrl = "https://example.com/article",
        });
        var tool = new HttpFetchSkill(
            webClient,
            new HttpFetchContentFormatter(converter),
            NullLogger<HttpFetchSkill>.Instance);

        var result = await ExecuteHttpFetchAsync(tool, """
            {"url":"https://example.com/article","output_format":"markdown"}
            """);

        Assert.IsTrue(result.Success);
        StringAssert.Contains(result.Output, "HTTP 200 OK");
        StringAssert.Contains(result.Output, "# Article Title");
        Assert.IsNotNull(converter.LastHtml);
        StringAssert.Contains(converter.LastHtml!, "Article Title");
        Assert.IsFalse(converter.LastHtml!.Contains("<nav>", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(converter.LastHtml!.Contains("<script", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Text_Output_Removes_Html_Noise()
    {
        var webClient = new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            ContentType = "text/html",
            Body = "<html><body><header>skip</header><article><h1>Title</h1><p>First paragraph.</p></article></body></html>",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            FinalUrl = "https://example.com/text",
        });
        var tool = CreateHttpFetchSkill(webClient);

        var result = await ExecuteHttpFetchAsync(tool, """
            {"url":"https://example.com/text","output_format":"text"}
            """);

        Assert.IsTrue(result.Success);
        StringAssert.Contains(result.Output, "Title");
        StringAssert.Contains(result.Output, "First paragraph.");
        Assert.IsFalse(result.Output!.Contains("skip", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Output.Contains("<article>", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Raw_Output_Preserves_Response_Body()
    {
        var body = "<html><body><script>keep raw</script><p>Raw body</p></body></html>";
        var webClient = new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            ContentType = "text/html",
            Body = body,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            FinalUrl = "https://example.com/raw",
        });
        var tool = CreateHttpFetchSkill(webClient);

        var result = await ExecuteHttpFetchAsync(tool, """
            {"url":"https://example.com/raw","output_format":"raw"}
            """);

        Assert.IsTrue(result.Success);
        StringAssert.Contains(result.Output, body);
    }

    [TestMethod]
    public async Task Json_Output_Includes_Metadata_Headers_And_Truncation()
    {
        var webClient = new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 201,
            ReasonPhrase = "Created",
            ContentType = "application/json",
            Body = """{"message":"abcdef"}""",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-request-id"] = "req-1",
            },
            FinalUrl = "https://api.example.com/items",
        });
        var tool = CreateHttpFetchSkill(webClient);

        var result = await ExecuteHttpFetchAsync(tool, """
            {"url":"https://api.example.com/items","method":"POST","headers":{"Authorization":"Bearer token"},"body":"{}","content_type":"application/json","timeout_seconds":12,"output_format":"json","max_response_chars":8,"include_headers":true,"cookie_scope":"session"}
            """);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(webClient.LastRequest);
        Assert.AreEqual("POST", webClient.LastRequest!.Method);
        Assert.AreEqual("Bearer token", webClient.LastRequest.Headers["Authorization"]);
        Assert.AreEqual(12, webClient.LastRequest.TimeoutSeconds);
        Assert.AreEqual("session", webClient.LastRequest.CookieScope);
        Assert.AreEqual("workspace-1:session-1:agent-1", webClient.LastRequest.CookieKey);

        using var doc = JsonDocument.Parse(result.Output!);
        var root = doc.RootElement;
        Assert.AreEqual(201, root.GetProperty("status_code").GetInt32());
        Assert.AreEqual("Created", root.GetProperty("reason_phrase").GetString());
        Assert.IsTrue(root.GetProperty("success").GetBoolean());
        Assert.IsTrue(root.GetProperty("truncated").GetBoolean());
        Assert.AreEqual("""{"messag""", root.GetProperty("body").GetString());
        Assert.AreEqual("req-1", root.GetProperty("headers").GetProperty("x-request-id").GetString());
    }

    [TestMethod]
    public async Task Rejects_Non_Http_Urls_Before_Transport()
    {
        var webClient = new RecordingWebClient(new WebClientResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            ContentType = "text/plain",
            Body = "should not call",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            FinalUrl = "file:///etc/passwd",
        });
        var tool = CreateHttpFetchSkill(webClient);

        var result = await ExecuteHttpFetchAsync(tool, """
            {"url":"file:///etc/passwd"}
            """);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Only http:// and https:// URLs are supported.");
        Assert.IsNull(webClient.LastRequest);
    }

    private static HttpFetchSkill CreateHttpFetchSkill(RecordingWebClient webClient) =>
        new(
            webClient,
            new HttpFetchContentFormatter(new RecordingHtmlToMarkdownConverter(string.Empty)),
            NullLogger<HttpFetchSkill>.Instance);

    private static Task<ToolExecutionResult> ExecuteHttpFetchAsync(HttpFetchSkill tool, string argumentsJson) =>
        tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = argumentsJson,
            Context = SampleContext(),
        });

    private static ToolExecutionContext SampleContext() => new()
    {
        WorkspaceId = "workspace-1",
        SessionId = "session-1",
        AgentInstanceId = "agent-1",
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

    private sealed class RecordingHtmlToMarkdownConverter(string markdown) : IHtmlToMarkdownConverter
    {
        public string? LastHtml { get; private set; }

        public string Convert(string html)
        {
            LastHtml = html;
            return string.IsNullOrEmpty(markdown) ? HtmlContentExtractor.ToPlainText(html) : markdown;
        }
    }
}
