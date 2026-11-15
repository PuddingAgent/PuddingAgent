using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PuddingSpeech;

namespace PuddingPlatform.Controllers.Api;

public sealed class VoiceFailureAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested) return;
        var failure = context.Exception switch
        {
            SpeechFailure known => known,
            System.Text.Json.JsonException => SpeechFailure.Protocol(),
            OperationCanceledException or TimeoutException => new SpeechFailure("voice_timeout", "语音服务响应超时，请重试。", 504, true),
            HttpRequestException => new SpeechFailure("voice_provider_failed", "无法连接语音服务，请检查网络和语音设置。", 502, true),
            InvalidOperationException => new SpeechFailure("voice_model_unavailable", "语音服务尚未配置完整，请检查语音设置。", 503),
            NotSupportedException or InvalidDataException => new SpeechFailure("voice_unsupported_audio", "音频格式不支持，请重新录音。", 415),
            _ => null,
        };
        if (failure is null) return;
        var errorId = Guid.NewGuid().ToString("N")[..12];
        context.HttpContext.RequestServices.GetService<ILogger<VoiceFailureAttribute>>()?
            .LogWarning("[Voice] errorId={ErrorId} code={Code} path={Path}", errorId, failure.Code, context.HttpContext.Request.Path);
        context.Result = new ObjectResult(new { code = failure.Code, message = failure.Message, retryable = failure.Retryable, errorId }) { StatusCode = failure.Status };
        context.ExceptionHandled = true;
    }
}
