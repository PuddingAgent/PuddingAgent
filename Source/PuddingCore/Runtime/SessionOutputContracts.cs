using PuddingCode.Observability;
using PuddingCode.Platform;

namespace PuddingCode.Runtime;

/// <summary>会话输出写入器，会话层与执行引擎之间的稳定边界。</summary>
public interface ISessionOutputWriter
{
    Task WriteFrameAsync(
        string sessionId,
        string workspaceId,
        ServerSentEventFrame frame,
        RuntimeTraceContext? trace = null,
        CancellationToken ct = default);
}
