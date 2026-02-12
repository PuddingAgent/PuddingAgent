using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingCoreTests.Memory;

[TestClass]
public sealed class MemoryMaintenancePlanValidatorTests
{
    [TestMethod]
    public void Validate_ShouldAcceptAppendAndSupersedeOperations_WhenReferencesAreAllowed()
    {
        var plan = CreatePlan() with
        {
            Operations =
            [
                new MemoryMaintenanceOperation
                {
                    OperationId = "op-append",
                    Action = MemoryMaintenanceActions.AppendNew,
                    Confidence = 0.82,
                    ProposedContent = "User prefers concise engineering summaries.",
                    Rationale = "New stable preference from the session.",
                },
                new MemoryMaintenanceOperation
                {
                    OperationId = "op-supersede",
                    Action = MemoryMaintenanceActions.SupersedeExisting,
                    Confidence = 0.91,
                    Target = new MemoryPlanReference
                    {
                        WorkspaceId = "workspace-1",
                        ChapterId = "chapter-1",
                    },
                    ProposedContent = "Updated decision replaces the older chapter.",
                    Rationale = "The later statement explicitly replaces the prior one.",
                },
            ],
        };
        var validator = new MemoryMaintenancePlanValidator();

        var result = validator.Validate(plan, Context("chapter-1"));

        Assert.IsTrue(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    [TestMethod]
    public void ValidateJson_ShouldRejectMalformedJson()
    {
        var validator = new MemoryMaintenancePlanValidator();

        var result = validator.ValidateJson("{not-json", Context());

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(e => e.Code == MemoryMaintenancePlanValidationErrors.InvalidJson));
    }

    [TestMethod]
    public void Validate_ShouldRejectCrossWorkspaceReferences()
    {
        var plan = CreatePlan() with
        {
            Operations =
            [
                new MemoryMaintenanceOperation
                {
                    OperationId = "op-reuse",
                    Action = MemoryMaintenanceActions.ReuseExisting,
                    Confidence = 0.9,
                    Target = new MemoryPlanReference
                    {
                        WorkspaceId = "workspace-2",
                        ChapterId = "chapter-1",
                    },
                    Rationale = "Same meaning.",
                },
            ],
        };
        var validator = new MemoryMaintenancePlanValidator();

        var result = validator.Validate(plan, Context("chapter-1"));

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(e => e.Code == MemoryMaintenancePlanValidationErrors.CrossWorkspaceReference));
    }

    [TestMethod]
    public void Validate_ShouldRejectPlanSourceOutsideSubconsciousMemoryScope()
    {
        var plan = CreatePlan() with
        {
            Source = CreatePlan().Source with
            {
                AgentId = "agent-2",
                MemoryLibraryId = "library-2",
            },
        };
        var validator = new MemoryMaintenancePlanValidator();

        var result = validator.Validate(plan, Context());

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(e => e.Code == MemoryMaintenancePlanValidationErrors.CrossAgentReference));
        Assert.IsTrue(result.Errors.Any(e => e.Code == MemoryMaintenancePlanValidationErrors.CrossMemoryLibraryReference));
    }

    [TestMethod]
    public void Validate_ShouldRejectLowConfidenceOperations()
    {
        var plan = CreatePlan() with
        {
            Operations =
            [
                new MemoryMaintenanceOperation
                {
                    OperationId = "op-low",
                    Action = MemoryMaintenanceActions.AppendNew,
                    Confidence = 0.3,
                    ProposedContent = "Weak signal.",
                    Rationale = "Maybe relevant.",
                },
            ],
        };
        var validator = new MemoryMaintenancePlanValidator();

        var result = validator.Validate(plan, Context());

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(e => e.Code == MemoryMaintenancePlanValidationErrors.LowConfidence));
    }

    [TestMethod]
    public void Validate_ShouldRejectReferencesOutsideCandidateSet()
    {
        var plan = CreatePlan() with
        {
            Operations =
            [
                new MemoryMaintenanceOperation
                {
                    OperationId = "op-delete",
                    Action = MemoryMaintenanceActions.Delete,
                    Confidence = 0.95,
                    Target = new MemoryPlanReference
                    {
                        WorkspaceId = "workspace-1",
                        ChapterId = "chapter-outside-candidates",
                    },
                    Rationale = "Delete obsolete memory.",
                },
            ],
        };
        var validator = new MemoryMaintenancePlanValidator();

        var result = validator.Validate(plan, Context("chapter-1"));

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(e => e.Code == MemoryMaintenancePlanValidationErrors.UnknownReference));
    }

    private static MemoryMaintenancePlan CreatePlan() => new()
    {
        PlanId = "plan-1",
        WorkspaceId = "workspace-1",
        Source = new MemoryMaintenancePlanSource
        {
            WorkspaceId = "workspace-1",
            SessionId = "session-1",
            HookEventId = "evt-1",
            SubconsciousJobId = "job-1",
            AgentId = "agent-1",
            AgentTemplateId = "template-1",
            MemoryLibraryId = "library-1",
        },
        CandidateReads =
        [
            new MemoryPlanReference
            {
                WorkspaceId = "workspace-1",
                ChapterId = "chapter-1",
            },
        ],
        Operations = [],
        Confidence = 0.88,
        Rationale = "Plan generated from session compression evidence.",
    };

    private static MemoryMaintenancePlanValidationContext Context(params string[] candidateIds) => new()
    {
        WorkspaceId = "workspace-1",
        MemoryScope = new SubconsciousMemoryScope
        {
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            AgentTemplateId = "template-1",
            SessionId = "session-1",
            MemoryLibraryId = "library-1",
        },
        AllowedReferenceIds = candidateIds.ToHashSet(StringComparer.Ordinal),
        MinimumOperationConfidence = 0.7,
    };
}
