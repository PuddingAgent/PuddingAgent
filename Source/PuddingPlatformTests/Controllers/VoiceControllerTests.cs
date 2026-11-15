using System.Buffers.Binary;
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

[TestClass]
public sealed class VoiceControllerTests
{
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
}
