using System.Text.Json.Serialization;
using PuddingRetrieval.Contracts;
using PuddingRetrievalEval.Contracts;

namespace PuddingRetrievalEvalProbe;

/// <summary>
/// One retrieval face's tri-state, as written into the environment-freeze block.
/// <para>
/// The three states are deliberately not a boolean: <see cref="ProductionCapabilityPolicy.Executed"/>
/// means "this run really drove the face and left evidence", <c>not-executed</c> means "the face
/// exists but this run never reached it" (a reason is then mandatory), and <c>unsupported</c> means
/// "this build cannot do it at all". Collapsing them into a boolean is how a report ends up printing
/// vector numbers it never measured.
/// </para>
/// </summary>
/// <param name="State">One of <c>executed</c> / <c>not-executed</c> / <c>unsupported</c>.</param>
/// <param name="Reason">Why the face was not executed; mandatory whenever <paramref name="State"/> is not <c>executed</c>.</param>
internal sealed record ProbeCapability(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("reason")] string? Reason = null);

/// <summary>The per-face capability block of <c>&lt;out&gt;.probe.json</c>.</summary>
internal sealed record ProbeCapabilities(
    [property: JsonPropertyName("lexical")] ProbeCapability Lexical,
    [property: JsonPropertyName("relations")] ProbeCapability Relations,
    [property: JsonPropertyName("vector")] ProbeCapability Vector);

/// <summary>Facts the capability verdict is derived from — never assumptions.</summary>
/// <param name="SuccessfulCalls">Probe calls this run that returned successfully (0 = nothing ran).</param>
/// <param name="Claims">Every claim the port produced this run (carries the searched-face evidence).</param>
/// <param name="RelationsFaceExecuted">True only if an anchored relations-intent request really ran.</param>
/// <param name="VectorFaceAvailable">True only if the probe build can drive the semantic face at all.</param>
internal sealed record ProbeCapabilityEvidence(
    int SuccessfulCalls,
    IReadOnlyList<SearchProbeClaim> Claims,
    bool RelationsFaceExecuted,
    bool VectorFaceAvailable);

/// <summary>
/// Derives the capability tri-state from what actually happened — the reason this policy is a
/// separate, pure function instead of inline strings is that "did the vector face run?" must be
/// testable, and a test can only pin it if the answer is computed rather than hard-coded next to the
/// JSON writer.
/// </summary>
internal static class ProductionCapabilityPolicy
{
    public const string Executed = "executed";
    public const string NotExecuted = "not-executed";
    public const string Unsupported = "unsupported";

    /// <inheritdoc cref="Build"/>
    public static ProbeCapabilities Build(ProbeCapabilityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.Claims);

        return new ProbeCapabilities(
            Lexical: Lexical(evidence),
            Relations: Relations(evidence),
            Vector: Vector(evidence));
    }

    private static ProbeCapability Lexical(ProbeCapabilityEvidence evidence)
    {
        if (evidence.SuccessfulCalls <= 0)
        {
            return new ProbeCapability(
                NotExecuted,
                "本轮没有任何一次调用成功返回：词面面没有留下可判定的执行证据。");
        }

        var claimsMissingLexical = evidence.Claims
            .Where(claim => !claim.Searched.Contains(RetrievalClaim.SurfaceLexical, StringComparer.Ordinal))
            .ToList();

        if (claimsMissingLexical.Count > 0)
        {
            // The port guarantees "lexical is always searched"; a violation is evidence, not something
            // to smooth over by writing "executed".
            return new ProbeCapability(
                NotExecuted,
                $"{claimsMissingLexical.Count} 条 claim 未在 searched 里声明 {RetrievalClaim.SurfaceLexical}："
                + "词面面的执行证据不完整，不能宣布已执行。");
        }

        return new ProbeCapability(Executed);
    }

    private static ProbeCapability Relations(ProbeCapabilityEvidence evidence)
    {
        if (evidence.RelationsFaceExecuted)
            return new ProbeCapability(Executed);

        return new ProbeCapability(
            NotExecuted,
            $"本轮用例全部是无锚点的 {RetrievalIntent.Auto} 查询；{RetrievalClaim.SurfaceRelations} 面只在"
            + "符号锚点 + 关系意图下执行，本集未触达（见 fixtureProbes.relations 的额外测量）。");
    }

    private static ProbeCapability Vector(ProbeCapabilityEvidence evidence)
    {
        // R5 未落地：生产端口没有接入 IEmbeddingProvider。无论配置与否，本探针都没有执行向量面，
        // 因此状态恒为 not-executed，且报告里不会出现任何向量指标数值。
        var reason = evidence.VectorFaceAvailable
            ? "向量面可用但本探针未执行：本轮未接入向量检索（R5 未落地），本探针不伪造向量指标。"
            : "未配置向量：生产端口未接入 IEmbeddingProvider（R5 未落地），本探针不伪造向量指标。";

        return new ProbeCapability(NotExecuted, reason);
    }
}
