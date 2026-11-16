using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingPlatform.Controllers.Api;
using PuddingSpeech;

namespace PuddingPlatformTests.Controllers;

/// <summary>
/// 覆盖 TTS 的唯一 HTTP 入口：<c>POST /api/voice/tts/synthesize</c>。
/// 错误映射不在这里断言 try/catch，而是断言“抛 SpeechFailure + 过滤器映射”，
/// 因为 <see cref="VoiceFailureAttribute"/> 是唯一真源。
/// </summary>
[TestClass]
public sealed class VoiceControllerTests
{
    private static readonly byte[] WavBytes =
    [
        0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45,
        0x66, 0x6D, 0x74, 0x20, 0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
        0xC0, 0x5D, 0x00, 0x00, 0x80, 0xBB, 0x00, 0x00, 0x02, 0x00, 0x10, 0x00,
        0x64, 0x61, 0x74, 0x61, 0x00, 0x00, 0x00, 0x00,
    ];

    [TestMethod]
    public async Task Synthesize_ReturnsWavAudioBytes_AndUsesConfiguredService()
    {
        VoiceSynthesisRequest? captured = null;
        var service = new Mock<IVoiceSynthesisService>();
        service
            .Setup(s => s.SynthesizeAsync(It.IsAny<VoiceSynthesisRequest>(), It.IsAny<CancellationToken>()))
            .Callback<VoiceSynthesisRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new VoiceSynthesisResult
            {
                MessageId = "msg-test",
                AudioBytes = WavBytes,
                Format = VoiceAudioFormats.Wav,
                Provider = VoiceSynthesisProviders.DashScope,
                Model = "qwen3-tts-flash",
            });
        var controller = new VoiceController();

        var action = await controller.Synthesize(
            new VoiceSynthesizeInput { Text = "你好，世界。" },
            service.Object,
            default);

        Assert.IsInstanceOfType<FileContentResult>(action);
        var file = (FileContentResult)action;
        Assert.AreEqual("audio/wav", file.ContentType);
        CollectionAssert.AreEqual(WavBytes, file.FileContents);
        Assert.IsNotNull(captured);
        Assert.AreEqual("你好，世界。", captured.Text);
        Assert.AreEqual(VoiceAudioFormats.Wav, captured.AudioFormat);
        Assert.AreEqual(24_000, captured.SampleRate);
        service.VerifyAll();
    }

    [TestMethod]
    public async Task Synthesize_EmptyText_Throws400_WithoutCallingProvider()
    {
        var service = new Mock<IVoiceSynthesisService>(MockBehavior.Strict);
        var controller = new VoiceController();

        var failure = await CaptureFailure(() => controller.Synthesize(
            new VoiceSynthesizeInput { Text = "   " },
            service.Object,
            default));

        Assert.AreEqual(400, failure.Status);
        Assert.AreEqual("voice_invalid_text", failure.Code);
        service.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Synthesize_TooLongText_Throws400_WithoutCallingProvider()
    {
        var service = new Mock<IVoiceSynthesisService>(MockBehavior.Strict);
        var controller = new VoiceController();

        var failure = await CaptureFailure(() => controller.Synthesize(
            new VoiceSynthesizeInput { Text = new string('声', VoiceController.MaxTextLength + 1) },
            service.Object,
            default));

        Assert.AreEqual(400, failure.Status);
        Assert.AreEqual("voice_text_too_long", failure.Code);
        service.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Synthesize_UnsupportedFormat_Throws400_WithoutCallingProvider()
    {
        var service = new Mock<IVoiceSynthesisService>(MockBehavior.Strict);
        var controller = new VoiceController();

        var failure = await CaptureFailure(() => controller.Synthesize(
            new VoiceSynthesizeInput { Text = "你好", Format = "ogg" },
            service.Object,
            default));

        Assert.AreEqual(400, failure.Status);
        Assert.AreEqual("voice_unsupported_format", failure.Code);
        service.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void VoiceFailureAttribute_ProviderFailure_MapsTo502_WithoutLeakingUpstreamBody()
    {
        var context = CreateExceptionContext(
            SpeechFailure.FromProvider(500, """{"code":"InternalError","message":"boom"}"""));

        new VoiceFailureAttribute().OnException(context);

        Assert.IsTrue(context.ExceptionHandled);
        var result = context.Result as ObjectResult;
        Assert.IsNotNull(result);
        Assert.AreEqual(502, result.StatusCode);
        var json = JsonSerializer.Serialize(result.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual("voice_provider_failed", doc.RootElement.GetProperty("code").GetString());
        StringAssert.Contains(doc.RootElement.GetProperty("message").GetString()!, "语音服务");
        Assert.DoesNotContain("boom", json);
        Assert.IsTrue(doc.RootElement.TryGetProperty("errorId", out _));
    }

    [TestMethod]
    public void Route_IsAuthorizedPostApiVoiceTtsSynthesize()
    {
        Assert.AreEqual("api/voice", typeof(VoiceController).GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.IsTrue(typeof(VoiceController).IsDefined(typeof(AuthorizeAttribute), inherit: true));
        Assert.IsTrue(typeof(VoiceController).IsDefined(typeof(VoiceFailureAttribute), inherit: true));
        var action = typeof(VoiceController).GetMethod(nameof(VoiceController.Synthesize));
        Assert.AreEqual("tts/synthesize", action?.GetCustomAttribute<HttpPostAttribute>()?.Template);
    }

    private static async Task<SpeechFailure> CaptureFailure(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (SpeechFailure failure)
        {
            return failure;
        }

        Assert.Fail("预期抛出 SpeechFailure，但调用成功返回。");
        throw new InvalidOperationException("unreachable");
    }
    [TestMethod]
    public async Task Recognize_ValidatesContentAndUsesUnifiedService()
    {
        var wave = new byte[364];
        "RIFF"u8.CopyTo(wave); "WAVEfmt "u8.CopyTo(wave.AsSpan(8)); "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), 320);
        var service = new Mock<IAudioTranscriptionService>();
        service.Setup(s => s.TranscribeAsync(It.Is<AudioTranscriptionRequest>(r => r.Format == "wav" && r.Provider == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AudioTranscriptionResult { Text = "你好", Provider = "fake-provider", Model = "fake-model" });
        var http = new DefaultHttpContext(); http.Request.Body = new MemoryStream(wave);
        http.Request.ContentType = "audio/webm"; // actual WAV wins over misleading MIME
        var controller = new VoiceController { ControllerContext = new ControllerContext { HttpContext = http } };
        Assert.IsInstanceOfType<OkObjectResult>(await controller.Recognize(service.Object, default));
        service.VerifyAll();
    }

    [TestMethod]
    public async Task Recognize_WebmLabeledWav_IsRejectedBeforeCallingProvider()
    {
        var service = new Mock<IAudioTranscriptionService>(MockBehavior.Strict);
        var http = new DefaultHttpContext(); http.Request.Body = new MemoryStream(new byte[100]); http.Request.ContentType = "audio/wav";
        var controller = new VoiceController { ControllerContext = new ControllerContext { HttpContext = http } };
        var failure = await Assert.ThrowsAsync<SpeechFailure>(() => controller.Recognize(service.Object, default));
        Assert.AreEqual("voice_unsupported_audio", failure.Code);
    }

    [TestMethod]
    public void QuotaFailure_Is503WithSafeActionableBody()
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        var context = new ExceptionContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), [])
        { Exception = SpeechFailure.FromProvider(403, """{"code":"AllocationQuota.FreeTierOnly","message":"secret"}""") };
        new VoiceFailureAttribute().OnException(context);
        Assert.IsTrue(context.ExceptionHandled);
        var result = (ObjectResult)context.Result!;
        Assert.AreEqual(503, result.StatusCode);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        StringAssert.Contains(json, "voice_quota_exhausted");
        Assert.DoesNotContain("secret", json);
    }
    [TestMethod]
    public async Task Stream_EmptyText_Throws400_BeforeWritingResponseHeaders()
    {
        var service = new Mock<IVoiceSynthesisService>(MockBehavior.Strict);
        var http = new DefaultHttpContext();
        var controller = new VoiceController { ControllerContext = new ControllerContext { HttpContext = http } };

        var failure = await CaptureFailure(() => controller.Stream(
            new VoiceSynthesizeInput { Text = "  " },
            service.Object,
            default));

        Assert.AreEqual(400, failure.Status);
        Assert.AreEqual("voice_invalid_text", failure.Code);
        Assert.IsNull(http.Response.ContentType); // SSE 头未写出，状态码仍可改为 400
        service.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void NormalizeFormat_DefaultsToWav_AndRejectsUnknown()
    {
        Assert.AreEqual(VoiceAudioFormats.Wav, TtsInputValidator.NormalizeFormat(null));
        Assert.AreEqual(VoiceAudioFormats.Mp3, TtsInputValidator.NormalizeFormat(" MP3 "));
        var failure = Assert.Throws<SpeechFailure>(() => TtsInputValidator.NormalizeFormat("ogg"));
        Assert.AreEqual(400, failure.Status);
        Assert.AreEqual("voice_unsupported_format", failure.Code);
    }

    private static ExceptionContext CreateExceptionContext(Exception exception)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new ExceptionContext(actionContext, new List<IFilterMetadata>()) { Exception = exception };
    }
}
