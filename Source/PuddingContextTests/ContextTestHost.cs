using System.Collections.Immutable;
using PuddingContext;
using PuddingContext.Contracts;

namespace PuddingContextTests;

public sealed class CountingTokenizer : IContextTokenizer
{
    public bool Fail { get; set; }
    public int Calls { get; private set; }

    public ValueTask<int> CountAsync(string content, CancellationToken ct = default)
    {
        Calls++;
        if (Fail) throw new InvalidOperationException("tokenizer down");
        return ValueTask.FromResult(content.Length);
    }
}

internal static class ContextFixtures
{
    public const string UserTurn = "current user turn";

    public static ImmutableArray<ContextMessage> Canonical(string userTurn = UserTurn) =>
    [
        new("m-system", ContextRole.System, "SYSTEM", ContextSourceKind.System, 0),
        new("m-assistant", ContextRole.Assistant, "previous answer", ContextSourceKind.Canonical, 1),
        new("m-user", ContextRole.User, userTurn, ContextSourceKind.Canonical, 2,
            ContextContributionValidator.Hash(userTurn)),
    ];

    public static ContextContribution Memory(string id = "memory-1", int priority = 10, int maxTokens = 100,
        string content = "memory note", string? hash = null, ContextRole role = ContextRole.Tool) => new(id,
        ContextSourceKind.Memory, role, content, priority, maxTokens, 3, hash, "turn-1", ExternalData: true);

    public static ContextAssemblyRequest Request(int maxTokens = 10_000,
        ImmutableArray<ContextContribution>? contributions = null,
        ImmutableArray<ContextMessage>? canonical = null, string? fence = null)
        => new("ws-1", "agent-1", "session-1", "turn-1", fence ?? ContextContributionValidator.Hash(UserTurn),
            maxTokens, canonical ?? Canonical(), contributions ?? [Memory()]);
}
