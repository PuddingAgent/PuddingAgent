namespace PuddingDesktop.Foundation;

public sealed record AgentIdentity(string WorkspaceId, string AgentId);

/// <summary>Presentation identity, not an execution grant or a new Role entity.</summary>
public sealed record RoleSummary(
    AgentIdentity Identity, string TemplateReference, string Name,
    string Responsibility, string MainSessionId, string Status);

public sealed record WorkContext(AgentIdentity Agent, string SessionId, string? RunId = null);

public enum WorkspaceDocumentKind { File, Diff, Terminal, Browser, Artifact }

public sealed record WorkspaceDocument(
    string Id, WorkspaceDocumentKind Kind, string Title,
    string ResourceReference, WorkContext Owner, string Content);

public enum ShellPage { Workbench, Settings, RuntimeCenter }
