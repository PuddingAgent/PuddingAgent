using System.Net.WebSockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Services;
using PuddingSpeech;

namespace PuddingPlatform.Controllers.Api;

[ApiController]
[Route("api/voice/asr")]
[VoiceFailure]
public sealed class RealtimeVoiceController(VoiceStreamTicketStore tickets, RealtimeAudioTranscriptionService transcription,
    ILogger<RealtimeVoiceController> logger) : ControllerBase
{
    [Authorize]
    [HttpPost("ticket")]
    public async Task<IActionResult> Ticket(CancellationToken ct)
    {
        await transcription.CreateAsync(ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(new { ticket = tickets.Issue(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "") });
    }

    // JWT-authorized POST issues the single-use ticket. It is presented in the first frame, never in the URL.
    [AllowAnonymous]
    [HttpGet("stream")]
    public async Task Stream(CancellationToken ct)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest) { Response.StatusCode = 400; return; }
        var origin = Request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Authority, Request.Host.Value, StringComparison.OrdinalIgnoreCase) || parsed.Scheme != Request.Scheme))
        { Response.StatusCode = 403; return; }
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        var peer = new WebSocketSpeechPeer(socket, logger);
        try
        {
            using var authentication = CancellationTokenSource.CreateLinkedTokenSource(ct);
            authentication.CancelAfter(TimeSpan.FromSeconds(10));
            if (!tickets.TryConsume(await peer.ReadTicketAsync(authentication.Token)))
                throw new SpeechFailure("voice_unauthorized", "语音连接授权已过期，请重新录音。", 401);
            var recognizer = await transcription.CreateAsync(ct);
            await new RealtimeAsrSession().RunAsync(peer, recognizer, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var failure = error as SpeechFailure ?? new SpeechFailure("voice_connection_failed", "实时语音连接失败，请重新录音。", 503, true);
            if (socket.State == WebSocketState.Open)
            {
                try { await peer.SendAsync(new("failed", Code: failure.Code, Message: failure.Message, ErrorId: Guid.NewGuid().ToString("N")[..12]), ct); }
                catch (WebSocketException) { }
            }
        }
        finally
        {
            using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "capture ended", closing.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException) { socket.Abort(); }
        }
    }
}
