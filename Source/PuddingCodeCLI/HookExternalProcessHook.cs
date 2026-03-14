using System.Diagnostics;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCodeCLI;

internal sealed class HookExternalProcessHook(
    string name,
    string command,
    string arguments,
    int timeoutMs) : IAgentHook
{
    private readonly string _name = name;
    private readonly string _command = command;
    private readonly string _arguments = arguments;
    private readonly int _timeoutMs = timeoutMs <= 0 ? 8000 : timeoutMs;

    public Task OnPreToolCallAsync(ToolCall call, CancellationToken ct = default) =>
        InvokeAsync("pre_tool_call", new { call.Id, call.Name, call.ArgumentsJson }, ct);

    public Task OnPostToolCallAsync(ToolCall call, string result, bool isError, CancellationToken ct = default) =>
        InvokeAsync("post_tool_call", new { call.Id, call.Name, isError, result }, ct);

    public Task OnPreReplyAsync(string reply, CancellationToken ct = default) =>
        InvokeAsync("pre_reply", new { reply }, ct);

    public Task OnPostReplyAsync(string reply, CancellationToken ct = default) =>
        InvokeAsync("post_reply", new { reply }, ct);

    private async Task InvokeAsync(string evt, object payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_command))
            return;

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _command,
                Arguments = _arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        if (!proc.Start())
            return;

        var data = JsonSerializer.Serialize(new
        {
            name = _name,
            @event = evt,
            timestamp = DateTimeOffset.Now,
            payload
        });

        await proc.StandardInput.WriteAsync(data);
        await proc.StandardInput.FlushAsync();
        proc.StandardInput.Close();

        using var timeout = new CancellationTokenSource(_timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}

