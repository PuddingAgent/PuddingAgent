using PuddingCode.Abstractions;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingPlatform.Services;

/// <summary>
/// 会话输出写入器，将 SSE 帧通过 ISessionStateManager 追加到会话。
/// 执行引擎与会话层之间的稳定边界。
/// </summary>
public sealed class SessionOutputWriter : ISessionOutputWriter
{
    private readonly ISessionStateManager _ssm;

    public SessionOutputWriter(ISessionStateManager ssm)
    {
        _ssm = ssm;
    }

    public async Task WriteFrameAsync(
        string sessionId,
        string workspaceId,
        ServerSentEventFrame frame,
        RuntimeTraceContext? trace = null,
        CancellationToken ct = default,
        string? component = null,
        string? operation = null)
    {
        await _ssm.AppendAsync(sessionId, workspaceId, frame, ct, trace, component, operation);
    }
}
