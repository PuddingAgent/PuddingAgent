using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCodeCLI;

internal sealed class HookAuditFileHook(string filePath) : IAgentHook
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task OnPreToolCallAsync(ToolCall call, CancellationToken ct = default)
    {
        await WriteAsync($"pre_tool {call.Name}", ct);
    }

    public async Task OnPostToolCallAsync(ToolCall call, string result, bool isError, CancellationToken ct = default)
    {
        await WriteAsync($"post_tool {call.Name} error={isError}", ct);
    }

    public async Task OnPreReplyAsync(string reply, CancellationToken ct = default)
    {
        await WriteAsync("pre_reply", ct);
    }

    public async Task OnPostReplyAsync(string reply, CancellationToken ct = default)
    {
        await WriteAsync("post_reply", ct);
    }

    private async Task WriteAsync(string message, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct);
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            await File.AppendAllTextAsync(
                filePath,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}",
                ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}

