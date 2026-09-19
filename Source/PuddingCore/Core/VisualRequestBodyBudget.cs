using System.Text;
using PuddingCode.Abstractions;

namespace PuddingCode.Core;

internal sealed record PreparedVisualRequest<T>(string Body, VisualInputRequestBudget Budget, T State);

/// <summary>Rebuild locally with a smaller image allocation when text/tools consume the wire budget.</summary>
internal static class VisualRequestBodyBudget
{
    internal const long MaxBodyBytes = 48L * 1024 * 1024;

    internal static async Task<PreparedVisualRequest<T>> BuildAsync<T>(
        Func<long?, Task<PreparedVisualRequest<T>>> build,
        IVisualArtifactResolver? resolver, bool enforce, CancellationToken ct)
    {
        long? allocation = null;
        var hadImages = false;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await build(allocation);
            var bodyBytes = Encoding.UTF8.GetByteCount(result.Body);
            if (!enforce || bodyBytes <= MaxBodyBytes)
                return result;
            var imageBytes = result.Budget.SerializedImageBytes;
            hadImages = result.Budget.ImageCount > 0;
            var remaining = MaxBodyBytes - (bodyBytes - imageBytes);
            if (remaining <= 0 || imageBytes == 0 || resolver is not IVisualArtifactPreprocessor)
                break;
            allocation = Math.Min(result.Budget.PreparationMaxBytes - 1,
                (long)(result.Budget.PreparationMaxBytes * ((double)remaining / imageBytes) * 0.9));
            if (allocation <= 0)
                break;
        }
        throw new VisionPipelineException(VisionErrorCodes.RequestLimitExceeded,
            "The complete JSON request still exceeds 48 MiB after bounded image preprocessing.",
            userMessage: hadImages
                ? "包含图片、文字与工具定义的请求仍超过 48 MiB，自动预处理无法满足限制。请减少图片或缩短内容；仍可继续发送文字消息。"
                : "文字与工具定义组成的请求超过 48 MiB。请缩短内容或减少工具后重试。");
    }
}
