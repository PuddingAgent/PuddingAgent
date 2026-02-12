using System.Net;
using System.Text;
using PuddingCode.Configuration;
using PuddingCode.Models;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class DashScopeAsrProviderTests
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

    private static PuddingAsrModelConfig CreateModelConfig(
        string modelId = "qwen3-asr-flash") => new()
    {
        ModelId = modelId,
        Name = "Qwen3 ASR Flash",
        Path = "api/v1/services/aigc/multimodal-generation/generation",
        Languages = ["zh-CN", "en-US"],
        SampleRates = [16000],
        SupportsEmotion = true,
        SupportsTimestamps = true,
        IsDefault = true,
        SortOrder = 1,
    };

    private static byte[] CreateWavBytes(int sampleRate = 16000, double durationSec = 1.0)
    {
        // 生成最小 WAV 头 + 静音 PCM 数据
        var dataSize = (int)(sampleRate * durationSec * 2); // 16-bit mono
        var wav = new byte[44 + dataSize];
        // RIFF header
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(36 + dataSize).CopyTo(wav, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        // fmt chunk
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BitConverter.GetBytes(16).CopyTo(wav, 16); // chunk size
        BitConverter.GetBytes((short)1).CopyTo(wav, 20); // PCM
        BitConverter.GetBytes((short)1).CopyTo(wav, 22); // mono
        BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
        BitConverter.GetBytes(sampleRate * 2).CopyTo(wav, 28); // byte rate
        BitConverter.GetBytes((short)2).CopyTo(wav, 32); // block align
        BitConverter.GetBytes((short)16).CopyTo(wav, 34); // bits per sample
        // data chunk
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(dataSize).CopyTo(wav, 40);
        return wav;
    }

    // ── 识别测试 ──

    [TestMethod]
    public async Task Recognize_WavBase64_ReturnsText()
    {
        const string response = """{"output":{"choices":[{"message":{"content":[{"text":"你好世界"}]}}]}}""";
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json"),
        });
        var provider = new DashScopeAsrProvider(
            new HttpClient(handler),
            CreateProviderConfig(),
            CreateModelConfig(),
            NullLogger<DashScopeAsrProvider>.Instance);

        var result = await provider.RecognizeAsync(CreateWavBytes());

        Assert.AreEqual("你好世界", result.Text);
        Assert.IsNull(result.Emotion);
    }

    [TestMethod]
    public async Task Recognize_WithEmotion_ReturnsEmotion()
    {
        const string response = """{"output":{"choices":[{"message":{"content":[{"text":"你好"}],"annotations":[{"emotion":"neutral"}]}}]}}""";
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json"),
        });
        var provider = new DashScopeAsrProvider(
            new HttpClient(handler),
            CreateProviderConfig(),
            CreateModelConfig(),
            NullLogger<DashScopeAsrProvider>.Instance);

        var result = await provider.RecognizeAsync(CreateWavBytes());

        Assert.AreEqual("你好", result.Text);
        Assert.AreEqual("neutral", result.Emotion);
    }

    [TestMethod]
    public async Task Recognize_ApiError_ThrowsException()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"Invalid API Key"}""", Encoding.UTF8, "application/json"),
        });
        var provider = new DashScopeAsrProvider(
            new HttpClient(handler),
            CreateProviderConfig(apiKey: "bad-key"),
            CreateModelConfig(),
            NullLogger<DashScopeAsrProvider>.Instance);

        var threw = false;
        try { await provider.RecognizeAsync(CreateWavBytes()); } catch { threw = true; }
        Assert.IsTrue(threw, "Expected exception for 401 Unauthorized");
    }

    [TestMethod]
    public async Task Recognize_ServerError_ThrowsException()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var provider = new DashScopeAsrProvider(
            new HttpClient(handler),
            CreateProviderConfig(),
            CreateModelConfig(),
            NullLogger<DashScopeAsrProvider>.Instance);

        var threw = false;
        try { await provider.RecognizeAsync(CreateWavBytes()); } catch { threw = true; }
        Assert.IsTrue(threw, "Expected exception for 500 Internal Server Error");
    }

    // ── 请求体测试 ──

    [TestMethod]
    public async Task BuildRequestBody_EncodesBase64Correctly()
    {
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"output":{"choices":[{"message":{"content":[{"text":"x"}]}}]}}""",
                    Encoding.UTF8, "application/json"),
            };
        });
        var provider = new DashScopeAsrProvider(
            new HttpClient(handler),
            CreateProviderConfig(),
            CreateModelConfig(),
            NullLogger<DashScopeAsrProvider>.Instance);

        var audio = CreateWavBytes();
        await provider.RecognizeAsync(audio, "wav");

        Assert.IsNotNull(capturedBody);
        Assert.IsTrue(capturedBody!.Contains("data:audio/wav;base64,"),
            $"Body should contain data URL prefix. Got: {capturedBody[..Math.Min(150, capturedBody.Length)]}");
        Assert.IsTrue(capturedBody.Contains("qwen3-asr-flash"));
    }

    [TestMethod]
    public async Task BuildRequestBody_WithLanguage_HasLanguageParam()
    {
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"output":{"choices":[{"message":{"content":[{"text":"x"}]}}]}}""",
                    Encoding.UTF8, "application/json"),
            };
        });
        var provider = new DashScopeAsrProvider(
            new HttpClient(handler),
            CreateProviderConfig(),
            CreateModelConfig(),
            NullLogger<DashScopeAsrProvider>.Instance);

        await provider.RecognizeAsync(CreateWavBytes(), "wav", "zh-CN");

        Assert.IsNotNull(capturedBody);
        Assert.IsTrue(capturedBody!.Contains("\"language\":\"zh-CN\""),
            $"Body should contain language param. Got: {capturedBody[..Math.Min(200, capturedBody.Length)]}");
    }

    // ── 端点测试 ──

    [TestMethod]
    public async Task BuildEndpoint_UsesProviderEndpointAndModelPath()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            Assert.IsTrue(url.StartsWith("https://dashscope.aliyuncs.com/"));
            Assert.IsTrue(url.Contains("multimodal-generation/generation"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"output":{"choices":[{"message":{"content":[{"text":"x"}]}}]}}""",
                    Encoding.UTF8, "application/json"),
            };
        });
        var provider = new DashScopeAsrProvider(
            new HttpClient(handler),
            CreateProviderConfig(),
            CreateModelConfig(),
            NullLogger<DashScopeAsrProvider>.Instance);

        await provider.RecognizeAsync(CreateWavBytes());
    }

    // ── ProviderId 测试 ──

    [TestMethod]
    public void ProviderId_Returns_ConfiguredId()
    {
        var provider = new DashScopeAsrProvider(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            CreateProviderConfig(),
            CreateModelConfig(),
            NullLogger<DashScopeAsrProvider>.Instance);

        Assert.AreEqual("dashscope", provider.ProviderId);
    }

    // ── Stub ──

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
