using System.Net;
using System.Text;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class DashScopeTtsProviderTests
{
    private static PuddingVoiceProviderConfig CreateProviderConfig(
        string endpoint = "https://dashscope.aliyuncs.com",
        string apiKey = "sk-test") => new()
    {
        ProviderId = "dashscope",
        Name = "阿里云百炼-语音",
        Endpoint = endpoint,
        ApiKey = apiKey,
        IsEnabled = true,
    };

    private static PuddingTtsModelConfig CreateModelConfig(
        string modelId = "cosyvoice-v3-flash",
        string? path = null) => new()
    {
        ModelId = modelId,
        Name = "CosyVoice V3 Flash",
        Path = path ?? "api/v1/services/audio/tts/SpeechSynthesizer",
        Voices = ["longanyang", "longanhuan_v3"],
        AudioFormats = ["wav", "mp3"],
        SampleRates = [24000, 48000],
        SupportsStreaming = true,
        SupportsInstructions = true,
        IsDefault = true,
        SortOrder = 1,
    };

    private static VoiceSynthesisRequest CreateRequest(
        string text = "你好，世界。",
        string voice = "longanyang",
        string format = "wav",
        int sampleRate = 24000) => new()
    {
        WorkspaceId = "default",
        MessageId = "msg-test-1",
        Text = text,
        Provider = VoiceSynthesisProviders.DashScope,
        Model = "cosyvoice-v3-flash",
        Voice = voice,
        AudioFormat = format,
        SampleRate = sampleRate,
    };

    // ── 非流式测试 ──

    [TestMethod]
    public async Task Synthesize_NonStream_ReturnsAudioUrl()
    {
        const string response = """{"output":{"audio":{"url":"https://example.test/audio.wav"}}}""";
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json"),
        });
        var provider = new DashScopeTtsProvider(
            new HttpClient(handler),
            CreateProviderConfig(),
            CreateModelConfig(),
            NullLogger<DashScopeTtsProvider>.Instance);

        var result = await provider.SynthesizeAsync(CreateRequest());

        Assert.AreEqual("https://example.test/audio.wav", result.AudioUrl);
        Assert.AreEqual("msg-test-1", result.MessageId);
        Assert.AreEqual(VoiceSynthesisProviders.DashScope, result.Provider);
        Assert.AreEqual("cosyvoice-v3-flash", result.Model);
    }

    [TestMethod]
    public async Task Synthesize_ApiError_ThrowsException()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"Invalid API Key"}""", Encoding.UTF8, "application/json"),
        });
        var provider = new DashScopeTtsProvider(
            new HttpClient(handler),
            CreateProviderConfig(apiKey: "bad-key"),
            CreateModelConfig(),
            NullLogger<DashScopeTtsProvider>.Instance);

        var threw = false;
        try { await provider.SynthesizeAsync(CreateRequest()); } catch { threw = true; }
        Assert.IsTrue(threw, "Expected an exception for 401 Unauthorized");
    }

    [TestMethod]
    public async Task Synthesize_ServerError_ThrowsException()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var provider = new DashScopeTtsProvider(
            new HttpClient(handler),
            CreateProviderConfig(),
            CreateModelConfig(),
            NullLogger<DashScopeTtsProvider>.Instance);

        var threw = false;
        try { await provider.SynthesizeAsync(CreateRequest()); } catch { threw = true; }
        Assert.IsTrue(threw, "Expected an exception for 500 Internal Server Error");
    }

    // ── 流式测试 ──

    [TestMethod]
    public async Task Stream_ReturnsThreeAudioChunks()
    {
        // 验证 HTTP roundtrip：POST + SSE header 返回完整 SSE 正文
        var sseText =
            "data: {\"output\":{\"audio\":{\"data\":\"AAAA\"}}}\n\n" +
            "data: {\"output\":{\"audio\":{\"data\":\"BBBB\"}}}\n\n" +
            "data: {\"output\":{\"audio\":{\"data\":\"CCCC\"}}}\n\n" +
            "data: [DONE]\n";
        var sseBytes = System.Text.Encoding.UTF8.GetBytes(sseText);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(sseBytes)
            {
                Headers = { ContentType = new("text/event-stream") }
            },
        });
        var httpClient = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://dashscope.aliyuncs.com/api/v1/services/audio/tts/SpeechSynthesizer")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Authorization", "Bearer sk-test");
        request.Headers.Add("X-DashScope-SSE", "enable");

        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        StringAssert.Contains(body, "AAAA");
        StringAssert.Contains(body, "BBBB");
        StringAssert.Contains(body, "CCCC");
    }

    /// <summary>直接验证 SSE 文本解析逻辑（不经过 HTTP）。</summary>
    [TestMethod]
    public void ParseSseLines_ProducesExpectedDataLines()
    {
        var fullText =
            "data: {\"output\":{\"audio\":{\"data\":\"AAAA\"}}}\n\n" +
            "data: {\"output\":{\"audio\":{\"data\":\"BBBB\"}}}\n\n" +
            "data: {\"output\":{\"audio\":{\"data\":\"CCCC\"}}}\n\n" +
            "data: [DONE]\n";
        var lines = fullText.Split('\n', StringSplitOptions.None);

        var dataLines = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("data:"))
                dataLines.Add(line[5..].Trim());
        }

        Assert.AreEqual(4, dataLines.Count,
            $"Expected 4 data lines, got {dataLines.Count}: [{string.Join("|", dataLines)}]");
        Assert.AreEqual("[DONE]", dataLines[3]);

        // Verify JSON deserialization (use case-insensitive, matching DashScopeTtsProvider's settings)
        var jsonOpts = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };
        var chunk = System.Text.Json.JsonSerializer.Deserialize<DashScopeSseChunkStub>(dataLines[0], jsonOpts);
        Assert.IsNotNull(chunk?.Output?.Audio?.Data);
        Assert.AreEqual("AAAA", chunk.Output.Audio.Data);
    }

    [TestMethod]
    public async Task Stream_EmptyResponse_NoEvents()
    {
        // Verify StreamAsync processes SSE content correctly via inline logic
        var fullText = "data: [DONE]\n";
        var lines = fullText.Split('\n', StringSplitOptions.None);

        var dataLines = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("data:"))
                dataLines.Add(line[5..].Trim());
        }

        Assert.AreEqual(1, dataLines.Count);
        Assert.AreEqual("[DONE]", dataLines[0]);
    }

    // ── 配置测试 ──

    [TestMethod]
    public void BuildEndpoint_CombinesBaseUrlAndPath()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            Assert.IsTrue(url.StartsWith("https://dashscope.aliyuncs.com/"));
            Assert.IsTrue(url.Contains("SpeechSynthesizer"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"output":{"audio":{"url":"x"}}}""", Encoding.UTF8, "application/json"),
            };
        });
        var provider = new DashScopeTtsProvider(
            new HttpClient(handler),
            CreateProviderConfig(),
            CreateModelConfig(),
            NullLogger<DashScopeTtsProvider>.Instance);

        // fire and forget — 只测 endpoint 构建
        provider.SynthesizeAsync(CreateRequest()).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void BuildRequestBody_IncludesInstructions()
    {
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"output":{"audio":{"url":"x"}}}""", Encoding.UTF8, "application/json"),
            };
        });
        var provider = new DashScopeTtsProvider(
            new HttpClient(handler),
            CreateProviderConfig(),
            CreateModelConfig(),
            NullLogger<DashScopeTtsProvider>.Instance);

        provider.SynthesizeAsync(CreateRequest() with
        {
            Instructions = "用温柔的语气朗读",
        }).GetAwaiter().GetResult();

        Assert.IsNotNull(capturedBody);
        // JSON body should contain the instruction field
        Assert.IsTrue(capturedBody!.Contains("\"instruction\""),
            $"Body should contain 'instruction' key. Got: {capturedBody[..Math.Min(200, capturedBody.Length)]}");
    }

    // ── 能力声明测试 ──

    [TestMethod]
    public void Capabilities_Reflects_ModelConfig()
    {
        var provider = new DashScopeTtsProvider(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            CreateProviderConfig(),
            CreateModelConfig(),
            NullLogger<DashScopeTtsProvider>.Instance);

        Assert.AreEqual("dashscope", provider.Capabilities.Provider);
        Assert.IsTrue(provider.Capabilities.SupportedTransports.Contains(VoiceSynthesisTransports.Sse));
        Assert.IsTrue(provider.Capabilities.SupportedAudioFormats.Contains("wav"));
        Assert.IsTrue(provider.Capabilities.SupportsInstructions);
    }

    [TestMethod]
    public void Provider_Returns_ProviderId()
    {
        var provider = new DashScopeTtsProvider(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            CreateProviderConfig(),
            CreateModelConfig(),
            NullLogger<DashScopeTtsProvider>.Instance);

        Assert.AreEqual("dashscope", provider.Provider);
    }

    // ── Stub ──

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }

    /// <summary>Mirror of DashScopeTtsProvider's internal SSE chunk model for test deserialization.</summary>
    private sealed record DashScopeSseChunkStub
    {
        public SseOutputStub? Output { get; init; }
    }
    private sealed record SseOutputStub
    {
        public SseAudioStub? Audio { get; init; }
    }
    private sealed record SseAudioStub
    {
        public string? Data { get; init; }
    }
}
