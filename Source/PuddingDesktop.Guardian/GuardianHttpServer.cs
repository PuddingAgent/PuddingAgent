using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PuddingDesktop.Guardian;

public static class GuardianToken
{
    public static string Read(GuardianSettings settings)
    {
        var document = JsonSerializer.Deserialize<TokenRoot>(File.ReadAllText(Path.Combine(settings.DataRoot, "config", "system.json")), GuardianJson.Options);
        var token = document?.Desktop?.Core?.ControlToken;
        return string.IsNullOrWhiteSpace(token) ? throw new InvalidDataException("Desktop control token is not initialized.") : token;
    }

    public static bool Matches(string? actual, string expected) => actual is not null && actual.Length <= 1024
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(actual)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    private sealed record TokenRoot(TokenDesktop? Desktop);
    private sealed record TokenDesktop(TokenCore? Core);
    private sealed record TokenCore(string? ControlToken);
}

public sealed class GuardianHttpServer(GuardianSettings settings, GuardianController controller) : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly SemaphoreSlim _requests = new(16);
    public void Start()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{settings.HttpPort}/");
        _listener.Start(); // Fail closed: a guardian without its authenticated control endpoint must not supervise.
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var registration = ct.Register(_listener.Stop);
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ct.IsCancellationRequested && ex is HttpListenerException or ObjectDisposedException) { break; }
            if (!_requests.Wait(0)) { await ReplyAsync(context, 429, new { error = "too_many_requests" }); continue; }
            _ = HandleAsync(context, ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken lifetime)
    {
        try
        {
            var request = context.Request;
            if (request.RemoteEndPoint is null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address)
                || request.Headers["Origin"] is not null)
            { await ReplyAsync(context, 403, new { error = "loopback_clients_only" }); return; }
            if (!GuardianToken.Matches(request.Headers["X-Control-Token"], GuardianToken.Read(settings)))
            { await ReplyAsync(context, 401, new { error = "unauthorized" }); return; }
            if (request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/guardian/status")
            {
                var state = controller.State;
                await ReplyAsync(context, 200, new { protocol = "pudding.guardian/1", processId = Environment.ProcessId,
                    state.Phase, state.Monitoring, busy = controller.Busy, state.RequestId, state.Error, state.PreparedDirectory,
                    settings.DesktopExecutablePath, settings.DataRoot, settings.DesktopHome,
                    hasFallback = state.Fallback is not null, pending = state.Pending is not null, state.UpdatedAt });
                return;
            }
            if (request.HttpMethod != "POST" || request.Url?.AbsolutePath != "/guardian/commands")
            { await ReplyAsync(context, 404, new { error = "not_found" }); return; }
            if (request.ContentLength64 > 16384) { await ReplyAsync(context, 413, new { error = "body_too_large" }); return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var body = new MemoryStream();
            var buffer = new byte[2048];
            int read;
            while ((read = await request.InputStream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (body.Length + read > 16384) { await ReplyAsync(context, 413, new { error = "body_too_large" }); return; }
                body.Write(buffer, 0, read);
            }
            var command = JsonSerializer.Deserialize<GuardianCommand>(body.ToArray(), GuardianJson.Options)
                ?? throw new ArgumentException("A command is required.");
            var result = controller.Submit(command, lifetime);
            await ReplyAsync(context, result == "accepted" ? 202 : result == "duplicate" ? 200 : 409,
                new { accepted = result is "accepted" or "duplicate", result, command.RequestId, statusUrl = $"http://127.0.0.1:{settings.HttpPort}/guardian/status" });
        }
        catch (Exception ex)
        {
            try { await ReplyAsync(context, ex is ArgumentException or JsonException ? 400 : 503,
                new { error = ex is ArgumentException or JsonException ? "invalid_command" : "unavailable" }); }
            catch { context.Response.Abort(); }
        }
        finally { _requests.Release(); }
    }

    private static async Task ReplyAsync(HttpListenerContext context, int status, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, GuardianJson.Options);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
    public void Dispose() => _listener.Close();
}
