using PuddingCode.Models;

namespace PuddingRuntime.Services;

public static class MemoryMaintenancePlanWriteCommandMapper
{
    public static MemoryWriteCommand MapOperation(
        MemoryMaintenancePlan plan,
        MemoryMaintenanceOperation operation,
        string mode)
    {
        return new MemoryWriteCommand
        {
            CommandId = $"{plan.PlanId}:{operation.OperationId}",
            WorkspaceId = plan.WorkspaceId,
            Intent = MapIntent(operation.Action),
            Mode = mode,
            Source = new MemoryWriteSource
            {
                SourceKind = MemoryWriteSourceKinds.SubconsciousPlan,
                SessionId = plan.Source.SessionId,
                HookEventId = plan.Source.HookEventId,
                SubconsciousJobId = plan.Source.SubconsciousJobId,
                PlanId = plan.PlanId,
                OperationId = operation.OperationId,
                AgentId = plan.Source.AgentId,
                AgentTemplateId = plan.Source.AgentTemplateId,
                MemoryLibraryId = plan.Source.MemoryLibraryId,
            },
            Target = MapReference(operation.Target),
            Candidates = plan.CandidateReads
                .Select(MapReference)
                .Where(reference => reference is not null)
                .Cast<MemoryWriteCandidate>()
                .ToArray(),
            Payload = new MemoryWritePayload
            {
                Title = operation.ProposedTitle,
                Content = operation.ProposedContent,
                Confidence = operation.Confidence,
                Rationale = operation.Rationale,
                RiskFlags = operation.RiskFlags,
            },
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["plan_confidence"] = plan.Confidence.ToString("0.###"),
                ["operation_action"] = operation.Action,
                ["agent_id"] = plan.Source.AgentId ?? string.Empty,
                ["agent_template_id"] = plan.Source.AgentTemplateId ?? string.Empty,
                ["memory_library_id"] = plan.Source.MemoryLibraryId ?? string.Empty,
            },
        };
    }

    private static string MapIntent(string action) =>
        action switch
        {
            MemoryMaintenanceActions.ReuseExisting => MemoryWriteIntents.ReuseExisting,
            MemoryMaintenanceActions.AppendNew => MemoryWriteIntents.AppendNew,
            MemoryMaintenanceActions.SupersedeExisting => MemoryWriteIntents.SupersedeExisting,
            MemoryMaintenanceActions.MergeCandidates => MemoryWriteIntents.MergeCandidates,
            MemoryMaintenanceActions.Deprecate => MemoryWriteIntents.Archive,
            MemoryMaintenanceActions.Delete => MemoryWriteIntents.DeleteRequested,
            MemoryMaintenanceActions.UpdateIndex => MemoryWriteIntents.UpdateIndex,
            MemoryMaintenanceActions.UpdateSkillPointer => MemoryWriteIntents.UpdateSkillPointer,
            _ => action,
        };

    private static MemoryWriteCandidate? MapReference(MemoryPlanReference? reference) =>
        reference is null
            ? null
            : new MemoryWriteCandidate
            {
                WorkspaceId = reference.WorkspaceId,
                BookId = reference.BookId,
                ChapterId = reference.ChapterId,
                FactId = reference.FactId,
                PointerId = reference.PointerId,
            };
}
