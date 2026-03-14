using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCodeCLI;

internal sealed class HookRuntimeMetrics : IAgentHook
{
    public int PreToolCalls { get; private set; }
    public int PostToolCalls { get; private set; }
    public int PreReplies { get; private set; }
    public int PostReplies { get; private set; }
    public int HookErrors { get; private set; }

    public Task OnPreToolCallAsync(ToolCall call, CancellationToken ct = default)
    {
        PreToolCalls++;
        return Task.CompletedTask;
    }

    public Task OnPostToolCallAsync(ToolCall call, string result, bool isError, CancellationToken ct = default)
    {
        PostToolCalls++;
        if (isError) HookErrors++;
        return Task.CompletedTask;
    }

    public Task OnPreReplyAsync(string reply, CancellationToken ct = default)
    {
        PreReplies++;
        return Task.CompletedTask;
    }

    public Task OnPostReplyAsync(string reply, CancellationToken ct = default)
    {
        PostReplies++;
        return Task.CompletedTask;
    }
}

