using System.Collections.Generic;

namespace PuddingCodeIndex.Contracts;

/// <summary>
/// Marks an <see cref="ICodeIndexer"/> as a <b>language implementation</b> that the aggregate
/// <c>CompositeCodeIndexer</c> fans a scope out to (C#, TypeScript/JavaScript, Python, …).
/// <para>
/// <b>Why a port of its own.</b> The aggregate has to consume every language implementation. Had it
/// injected <c>IEnumerable&lt;ICodeIndexer&gt;</c>, it would also inject <i>itself</i> — it is registered as
/// an <see cref="ICodeIndexer"/> too — and recurse without bound. Splitting "language implementation" from
/// "the contract callers consume" makes that mistake a compile-time impossibility instead of a runtime
/// stack overflow.
/// </para>
/// <para>
/// <b>Why it also declares the two facts below.</b> The aggregate routes one changed file to its
/// <i>unique owner</i> and must pass that owner's result through unchanged, so the owner has to be
/// identified <b>before</b> it is called. Probing is not an option: a non-owner rejects a file with the very
/// same <see cref="CodeIndexStatus.Failed"/> a genuine per-file failure reports, so probing could not tell
/// "not my file" apart from "my file failed" and the caller would lose the owner's own diagnosis. The
/// extension set therefore belongs to the contract — the shape <c>IFileOutliner.SupportedExtensions</c>
/// already uses in this codebase.
/// </para>
/// </summary>
public interface ILanguageCodeIndexer : ICodeIndexer
{
    /// <summary>
    /// Stable label of this language, used in diagnostics and in
    /// <see cref="CodeIndexResult.LanguageOutcomes"/> (e.g. <c>"C#"</c>,
    /// <c>"TypeScript/JavaScript"</c>, <c>"Python"</c>).
    /// </summary>
    string Language { get; }

    /// <summary>
    /// Extensions this implementation owns for per-file work, each with a leading dot and compared
    /// case-insensitively (e.g. <c>".cs"</c>). The sets of the registered implementations must be disjoint:
    /// routing needs exactly one owner per extension.
    /// </summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }
}
