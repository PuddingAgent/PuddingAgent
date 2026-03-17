using System.Text;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCodeCLI;

/// <summary>
/// Implements <see cref="IHistoryCompressor"/> using the subconscious (fast/cheap) LLM.
/// Formats the message window into a concise summarisation prompt, then delegates
/// to <paramref name="summarizer"/> — the same delegate used for memory compaction.
/// </summary>
internal sealed class SubconsciousHistoryCompressor : IHistoryCompressor
{
    private readonly Func<string, int, CancellationToken, Task<string?>> _summarizer;
    private readonly int _maxOutputChars;

    public SubconsciousHistoryCompressor(
        Func<string, int, CancellationToken, Task<string?>> summarizer,
        int maxOutputChars = 1200)
    {
        _summarizer = summarizer;
        _maxOutputChars = maxOutputChars;
    }

    public async Task<string?> CompressAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Summarize this conversation window concisely as bullet points.");
        sb.AppendLine("Preserve: key decisions made, files changed, tool call outcomes, errors encountered.");
        sb.AppendLine();

        foreach (var m in messages)
        {
            var roleLabel = m.Role switch
            {
                ChatRole.User      => "User",
                ChatRole.Assistant => "Assistant",
                ChatRole.Tool      => "Tool",
                ChatRole.System    => "System",
                _                  => m.Role.ToString()
            };

            string content;
            if (!string.IsNullOrWhiteSpace(m.Content))
                content = m.Content.Length > 500 ? m.Content[..500] + "…" : m.Content;
            else if (m.ToolCalls?.Count > 0)
                content = $"[Called: {string.Join(", ", m.ToolCalls.Select(t => t.Name))}]";
            else
                content = "[no content]";

            sb.AppendLine($"{roleLabel}: {content}");
        }

        return await _summarizer(sb.ToString(), _maxOutputChars, ct).ConfigureAwait(false);
    }
}
