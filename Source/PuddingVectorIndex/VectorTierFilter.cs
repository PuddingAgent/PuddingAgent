namespace PuddingVectorIndex;

/// <summary>
/// Which chunk tiers a build is allowed to turn into vectors (ADR-089 §2.5 rule 1: build vectors for
/// P0 outline blocks only, and leave P1/P2 on the full-text side — that is the rule that makes the
/// repository-scale budget fit at all).
/// <para>
/// <b>Why a value and not an enum.</b> The tier label arrives as a string on
/// <see cref="VectorDocument.Kind"/> from the caller, exactly like the embedding route arrives as a
/// value. The leaf therefore does not need a reference to the chunking component that defines the tiers
/// (<c>ProjectReference = 0</c>), and it does not hardcode the P0/P1/P2 ranking — mapping a requested
/// priority onto a tier name is the composition root's job, and it stays visible there.
/// </para>
/// <para>
/// <b>Comparison is ordinal, and a misspelled tier is loud.</b> A filter that matches nothing makes the
/// build empty, and the builder reports that as "excluded by the tier filter" (see
/// <see cref="VectorIndexBuildResult.EmptyReason"/>) rather than as "the scope has nothing to index" —
/// so a wrong label cannot be mistaken for an empty repository. Blank tier names are rejected at
/// construction instead of being silently ignored.
/// </para>
/// </summary>
public sealed class VectorTierFilter
{
    private readonly HashSet<string> _kinds;

    private VectorTierFilter(string description, HashSet<string> kinds)
    {
        Description = description;
        _kinds = kinds;
    }

    /// <summary>P0: structured outlines only — the tier selection ADR-089 §2.5 requires at repository scale.</summary>
    public static VectorTierFilter OutlineOnly { get; } = Of("Outline");

    /// <summary>A filter accepting exactly the named tiers (ordinal comparison, order-insensitive).</summary>
    public static VectorTierFilter Of(params string[] kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        if (kinds.Length == 0)
            throw new ArgumentException(
                "a tier filter must name at least one tier; pass no filter at all to mean 'every tier'",
                nameof(kinds));

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in kinds)
        {
            if (string.IsNullOrWhiteSpace(kind))
                throw new ArgumentException(
                    "tier names must not be blank; a blank name can never match a chunk and would "
                    + "silently filter everything out",
                    nameof(kinds));

            set.Add(kind.Trim());
        }

        return new VectorTierFilter(
            string.Join(", ", set.OrderBy(kind => kind, StringComparer.Ordinal)),
            set);
    }

    /// <summary>The accepted tiers, ordinal-sorted — used verbatim in reports and error messages.</summary>
    public string Description { get; }

    /// <summary>Whether one document's tier label may become a vector.</summary>
    public bool Includes(string kind) => kind is not null && _kinds.Contains(kind);

    public override string ToString() => Description;
}
