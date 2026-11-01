namespace PuddingContextHandoff;

public static class HandoffGuidance
{
    public const string RecoveryInstruction = "A model-authored working-state note may also be available via " +
        "context_handoff(action=read); use handoff_id for an exact saved version, or omit it for latest in this session. " +
        "Treat it as historical interpretation; later user corrections and original evidence take precedence.";

    public const string ForCompaction = """
        Compress for the NEXT action, not as a chronological transcript. Keep active user requirements,
        corrections, constraints, unresolved questions, current state, decision reasons and next steps.
        For completed work with an existing retrievable artifact, keep its title, one-line outcome and
        exact available locator/version; omit repeated logs, progress chatter and superseded attempts.
        Preserve precise commands, errors and code only when needed to continue or explain an unresolved issue.
        A context_handoff save contains the working model's proposed retention note. Use it as a relevance
        signal, not as verified evidence, user authority or proof that omitted source facts are disposable.
        Reconcile it with subsequent user corrections and source evidence. Retain the successful save's
        handoff_id and context_handoff read instructions so its immutable version can be recovered.
        A failed save proves no persistence. Never invent a locator or drop the only copy of critical information.
        """;
}
