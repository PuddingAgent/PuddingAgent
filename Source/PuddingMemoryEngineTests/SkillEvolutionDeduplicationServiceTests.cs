using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

[TestClass]
public sealed class SkillEvolutionDeduplicationServiceTests
{
    [TestMethod]
    public async Task Admission_ShouldDefer_WhenCreateConfidenceIsBelowThreshold()
    {
        var store = new InMemorySkillStore(Skill(
            "health-check",
            "Check service health",
            "Verify service health and return diagnostics.",
            "turn-existing",
            ["http_get", "parse_json"]));
        var service = CreateService(
            store,
            """{"action":"create","targetSkillId":null,"confidence":0.79,"reason":"possibly distinct"}""");

        var result = await service.EvaluateAdmissionAsync(Candidate("turn-new"), config: null);

        Assert.AreEqual(SkillAdmissionActions.Defer, result.Action);
        Assert.AreEqual(0.79, result.Confidence, 0.001);
        Assert.AreEqual(0, store.CreatedCount);
    }

    [TestMethod]
    public async Task AdmissionMerge_ShouldAppendEvidenceAndStructuredSourceTags()
    {
        var store = new InMemorySkillStore(Skill(
            "health-check",
            "Check service health",
            "Verify service health and return diagnostics.",
            "turn-existing",
            ["http_get", "parse_json"]));
        var service = CreateService(
            store,
            """{"action":"merge","targetSkillId":"health-check","confidence":0.96,"reason":"same intent"}""");
        var candidate = Candidate("turn-new");

        var admission = await service.EvaluateAdmissionAsync(candidate, config: null);
        var merged = await service.MergeCandidateAsync(candidate, admission.TargetSkillId!);

        Assert.AreEqual(SkillAdmissionActions.Merge, admission.Action);
        Assert.IsNotNull(merged);
        StringAssert.Contains(merged.Markdown, "Turn: turn-new");
        StringAssert.Contains(merged.Markdown, "version: 1.0.1");
        CollectionAssert.Contains(merged.Tags.ToArray(), "source-turn:turn-new");
        Assert.AreEqual("1.0.1", merged.Version);
    }

    [TestMethod]
    public async Task Consolidation_ShouldDisableOnlyDeterministicallyEligibleDuplicates()
    {
        var store = new InMemorySkillStore(
            Skill("health-check", "Check service health", "Verify service health and return diagnostics.", "turn-health", ["http_get", "parse_json"]),
            Skill("health-status", "Verify service health", "Check service health and report diagnostics.", "turn-health", ["http_get", "parse_json"]),
            Skill("terminal-poll", "Poll async terminal command", "Wait for a terminal process and collect output.", "turn-shared", ["shell_command", "wait"]),
            Skill("git-commit", "Prepare git commit", "Inspect a repository and create a commit.", "turn-shared", ["shell_command", "wait"]));
        var llm = new QueueMemoryLlmClient(["""
            {"groups":[
              {"canonicalSkillId":"health-check","duplicateSkillIds":["health-status"],"confidence":0.99,"reason":"same health workflow"},
              {"canonicalSkillId":"terminal-poll","duplicateSkillIds":["git-commit"],"confidence":0.99,"reason":"same tools"}
            ],"distinctSkillIds":["terminal-poll","git-commit"]}
            """]);
        var service = new SkillEvolutionDeduplicationService(
            store,
            llm,
            NullLogger<SkillEvolutionDeduplicationService>.Instance);

        var result = await service.ConsolidateExistingAsync("agent-1", config: null);
        var retryResult = await service.ConsolidateExistingAsync("agent-1", config: null);

        Assert.AreEqual(1, result.Consolidated);
        CollectionAssert.AreEqual(new[] { "health-status" }, result.DisabledSkillIds.ToArray());
        Assert.IsFalse((await store.GetAsync("agent-1", "health-status"))!.Enabled);
        Assert.IsTrue((await store.GetAsync("agent-1", "git-commit"))!.Enabled);
        var canonical = await store.GetAsync("agent-1", "health-check");
        Assert.AreEqual("1.0.1", canonical!.Version);
        StringAssert.Contains(canonical.Markdown, "version: 1.0.1");
        CollectionAssert.Contains(canonical.Tags.ToArray(), "dedup-reviewed:1.0.1");
        Assert.AreEqual(0, retryResult.Consolidated);
        Assert.AreEqual(2, llm.CallCount);
        CollectionAssert.Contains(
            (await store.GetAsync("agent-1", "health-status"))!.Tags.ToArray(),
            "superseded-by:health-check");
    }

    [TestMethod]
    public void ProcessedTurnExtraction_ShouldReadStructuredAndLegacyProvenance()
    {
        var structured = Skill(
            "structured",
            "Structured",
            "Structured source",
            "turn-tagged",
            ["one_tool", "two_tool"]);
        var legacy = Skill(
            "legacy",
            "Legacy",
            "Legacy source",
            "turn-legacy",
            ["one_tool", "two_tool"]) with
        {
            Tags = ["auto-generated"],
        };

        var turns = SkillEvolutionDeduplicationService.ExtractProcessedTurnIds([structured, legacy]);

        Assert.IsTrue(turns.Contains("turn-tagged"));
        Assert.IsTrue(turns.Contains("turn-legacy"));
    }

    [TestMethod]
    public async Task EvaluationWatermark_ShouldRemainCurrentOnlyForTheReviewedVersion()
    {
        var store = new InMemorySkillStore(Skill(
            "health-check",
            "Check service health",
            "Verify service health and return diagnostics.",
            "turn-existing",
            ["http_get", "parse_json"]));
        var service = CreateService(store);
        var skill = (await store.ListAutoGeneratedAsync("agent-1")).Single();

        var reviewed = await service.MarkEvaluatedAsync("agent-1", skill);
        var bumped = await store.UpdateAsync("agent-1", reviewed.SkillId, new AgentSkillEvolutionWriteRequest
        {
            SkillId = reviewed.SkillId,
            Name = reviewed.Name,
            Version = "1.0.1",
            Description = reviewed.Description,
            Tags = reviewed.Tags,
            Keywords = reviewed.Keywords,
            Markdown = reviewed.Markdown.Replace("version: 1.0.0", "version: 1.0.1"),
        });

        Assert.IsTrue(SkillEvolutionDeduplicationService.HasCurrentEvaluation(reviewed));
        Assert.IsFalse(SkillEvolutionDeduplicationService.HasCurrentEvaluation(bumped));
    }

    [TestMethod]
    public async Task DedupWatermark_ShouldSkipFlash_WhenEveryEnabledVersionWasConfidentlyReviewed()
    {
        var store = new InMemorySkillStore(
            Skill("health-check", "Check service health", "Verify service health.", "turn-health", ["http_get", "parse_json"]),
            Skill("git-commit", "Prepare git commit", "Create a verified commit.", "turn-git", ["shell_command", "wait"]));
        var llm = new QueueMemoryLlmClient(
        [
            """{"groups":[],"distinctSkillIds":["health-check","git-commit"]}""",
        ]);
        var service = new SkillEvolutionDeduplicationService(
            store,
            llm,
            NullLogger<SkillEvolutionDeduplicationService>.Instance);

        await service.ConsolidateExistingAsync("agent-1", config: null);
        await service.ConsolidateExistingAsync("agent-1", config: null);

        Assert.AreEqual(1, llm.CallCount);
    }

    private static SkillEvolutionDeduplicationService CreateService(
        IAgentSkillEvolutionStore store,
        params string[] responses)
        => new(
            store,
            new QueueMemoryLlmClient(responses),
            NullLogger<SkillEvolutionDeduplicationService>.Instance);

    private static PatternCandidate Candidate(string turnId) => new()
    {
        AgentInstanceId = "agent-1",
        SessionId = "session-1",
        TurnId = turnId,
        Title = "Check service health",
        Goal = "Verify service health and return diagnostics.",
        ToolSequence = ["http_get", "parse_json"],
        StepsCount = 2,
        AllSucceeded = true,
        Confidence = 0.95,
        Evidence = "Both calls completed successfully.",
    };

    private static AgentSkillEvolutionDocument Skill(
        string id,
        string name,
        string description,
        string turnId,
        IReadOnlyList<string> tools) => new()
    {
        SkillId = id,
        Name = name,
        Version = "1.0.0",
        Description = description,
        Tags = ["auto-generated", $"source-turn:{turnId}"],
        Keywords = tools,
        Enabled = true,
        Markdown = $"---\nversion: 1.0.0\n---\n\n# {name}\n\n- Turn: {turnId}\n",
    };

    private sealed class QueueMemoryLlmClient(IEnumerable<string> responses) : IMemoryLlmClient
    {
        private readonly Queue<string> _responses = new(responses);
        public int CallCount { get; private set; }

        public Task<MemoryClassification> ClassifyAsync(string messageText, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string?> SummarizeAsync(IReadOnlyList<string> memoryContents, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryQueryIntent?> ParseIntentAsync(string userMessage, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> ChatAsync(
            string systemPrompt,
            string userMessage,
            IReadOnlyList<object>? tools = null,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class InMemorySkillStore(params AgentSkillEvolutionDocument[] initial)
        : IAgentSkillEvolutionStore
    {
        private readonly Dictionary<string, AgentSkillEvolutionDocument> _skills = initial
            .ToDictionary(skill => skill.SkillId, StringComparer.OrdinalIgnoreCase);

        public int CreatedCount { get; private set; }

        public Task<AgentSkillEvolutionDocument?> GetAsync(
            string agentInstanceId,
            string skillId,
            CancellationToken ct = default)
            => Task.FromResult(_skills.GetValueOrDefault(skillId));

        public Task<IReadOnlyList<AgentSkillEvolutionDocument>> ListAutoGeneratedAsync(
            string agentInstanceId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentSkillEvolutionDocument>>(_skills.Values.ToArray());

        public Task<AgentSkillEvolutionDocument> CreateAsync(
            string agentInstanceId,
            AgentSkillEvolutionWriteRequest request,
            CancellationToken ct = default)
        {
            CreatedCount++;
            return SaveAsync(request, enabled: true);
        }

        public Task<AgentSkillEvolutionDocument> UpdateAsync(
            string agentInstanceId,
            string skillId,
            AgentSkillEvolutionWriteRequest request,
            CancellationToken ct = default)
            => SaveAsync(request, _skills[skillId].Enabled);

        public Task<AgentSkillEvolutionDocument> SetEnabledAsync(
            string agentInstanceId,
            string skillId,
            bool enabled,
            CancellationToken ct = default)
        {
            var updated = _skills[skillId] with { Enabled = enabled };
            _skills[skillId] = updated;
            return Task.FromResult(updated);
        }

        private Task<AgentSkillEvolutionDocument> SaveAsync(
            AgentSkillEvolutionWriteRequest request,
            bool enabled)
        {
            var document = new AgentSkillEvolutionDocument
            {
                SkillId = request.SkillId,
                Name = request.Name,
                Version = request.Version,
                Description = request.Description,
                Tags = request.Tags,
                Keywords = request.Keywords,
                Enabled = enabled,
                Markdown = request.Markdown,
            };
            _skills[document.SkillId] = document;
            return Task.FromResult(document);
        }
    }
}
