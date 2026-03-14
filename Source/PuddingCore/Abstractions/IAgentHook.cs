using PuddingCode.Models;

namespace PuddingCode.Abstractions;

public interface IAgentHook
{
    Task OnPreToolCallAsync(ToolCall call, CancellationToken ct = default);
    Task OnPostToolCallAsync(ToolCall call, string result, bool isError, CancellationToken ct = default);
    Task OnPreReplyAsync(string reply, CancellationToken ct = default);
    Task OnPostReplyAsync(string reply, CancellationToken ct = default);
}

