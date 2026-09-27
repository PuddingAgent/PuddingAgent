using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PuddingChat;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentChat;
using Core = PuddingCode.Platform;

namespace PuddingDesktop.Composition;

/// <summary>Direct application-service adapter. No HTTP, controller invocation or JSON serialization.</summary>
internal sealed class InProcessChatClient(IServiceScopeFactory scopes, CancellationToken hostStopping) : IChatClient
{
    private readonly CancellationTokenSource _shutdown = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);
    private readonly object _gate = new();
    private readonly HashSet<Task> _operations = [];
    private bool _closed;
    // Matches Core single-user session ownership; never supplied by the UI or HTTP ingress.
    private const string LocalUserId = "single-user";
    private Task<T> ExecuteAsync<T>(Func<IServiceProvider, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var task = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
                linked.Token.ThrowIfCancellationRequested();
                await using var scope = scopes.CreateAsyncScope();
                return await action(scope.ServiceProvider, linked.Token).ConfigureAwait(false);
            }, CancellationToken.None);
            _operations.Add(task);
            _ = task.ContinueWith(completed => { lock (_gate) _operations.Remove(completed); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }
    public Task<Workspace[]> GetWorkspacesAsync(CancellationToken ct) => ExecuteAsync(async (services, token) =>
    {
        return await services.GetRequiredService<PlatformDbContext>().Workspaces.AsNoTracking().OrderBy(w => w.Id)
            .Select(w => new Workspace(w.WorkspaceId, w.Name)).ToArrayAsync(token);
    }, ct);
    public Task<Agent[]> GetAgentsAsync(string workspace, CancellationToken ct) => ExecuteAsync(async (services, token) =>
    {
        return (await services.GetRequiredService<WorkspaceAgentFileService>().ListAgentsAsync(workspace, token))
            .Select(a => new Agent(a.AgentId, a.Name, a.DisplayName, a.Description, LocalAvatar(a.AvatarUrl), a.SourceTemplateId, a.MainSessionId, a.IsEnabled, a.IsFrozen)).ToArray();
    }, ct);
    public Task<AgentStatus[]> GetStatusesAsync(string workspace, CancellationToken ct) => ExecuteAsync(async (services, token) =>
    {
        return (await services.GetRequiredService<IAgentRunProjectionService>()
            .GetWorkspaceAgentStatusesAsync(workspace, LocalUserId, token))
            .Select(s => new AgentStatus(s.AgentId, s.Status, s.Summary, s.UnreadCount)).ToArray();
    }, ct);
    public Task<Conversation?> GetConversationAsync(RoleKey role, long? cursor, CancellationToken ct) => ExecuteAsync<Conversation?>(async (services, token) =>
    {
        const string owner = LocalUserId;
        var projection = services.GetRequiredService<IAgentConversationProjectionService>();
        if (cursor is not null && await projection.GetConversationCursorAsync(role.WorkspaceId, owner, role.AgentId, token) == cursor) return null;
        var view = await projection.GetConversationAsync(role.WorkspaceId, owner, role.AgentId, token);
        return new Conversation(view.WorkspaceId, view.AgentId, view.MainSessionId,
            view.Messages.Select(m => new ChatMessage(m.MessageId, m.RunId, m.Role, m.SourceName, m.CreatedAt, m.Content, m.Status,
                m.ProcessItems.Select(Map).ToArray(), m.TurnId,
                m.ProcessSummary is { } summary ? new ProcessSummary(summary.TotalItems, summary.ToolCalls, summary.FailedTools, summary.HasDetails) : null,
                m.TurnOutcome is { } outcome ? new TurnOutcome(outcome.Status, outcome.ErrorCode, outcome.ErrorMessage) : null,
                m.ContentParts?.Select(p => new PuddingChat.ContentPart(p.Type, p.ArtifactId, p.Detail)).ToArray())).ToArray(),
            view.ActiveRun is { } run ? new ActiveRun(run.RunId, run.Status, run.StatusText, run.Summary,
                new OutputSnapshot(run.OutputSnapshot.Markdown, run.OutputSnapshot.ProcessItems.Select(Map).ToArray(), Map(run.OutputSnapshot.Window))) : null,
            view.EventCursor);
    }, ct);
    public Task<string> EnsureSessionAsync(RoleKey role, Agent agent, CancellationToken ct) => ExecuteAsync(async (services, token) =>
    {
        return (await services.GetRequiredService<AgentMainSessionService>().EnsureAsync(role.WorkspaceId, role.AgentId,
            LocalUserId, token)).SessionId;
    }, ct);
    public Task<Acceptance> SendAsync(PendingSend send, CancellationToken ct) => ExecuteAsync(async (services, token) =>
    {
        var agent = await services.GetRequiredService<WorkspaceAgentFileService>().GetAgentAsync(send.Role.WorkspaceId, send.Role.AgentId, token);
        if (agent is null || !agent.IsEnabled || agent.IsFrozen) throw new InvalidOperationException("角色不存在、已停用或冻结。");
        var session = await services.GetRequiredService<ISessionRepository>().GetAsync(send.ConversationId, token);
        if (session is null || session.WorkspaceId != send.Role.WorkspaceId || (session.PrincipalId ?? session.AgentInstanceId) != send.Role.AgentId)
            throw new InvalidOperationException("消息与角色主会话归属不匹配。");
        var result = await services.GetRequiredService<ISubmitTurnHandler>().HandleAsync(new SubmitTurnCommand(
            send.ConversationId, send.Role.WorkspaceId, LocalUserId, send.ClientRequestId, send.ClientMessageId,
            new RecipientRequest { Type = "agent", AgentIds = [send.Role.AgentId] },
            [new Core.ContentPart { Type = "text", Text = send.Text }], null), token);
        return new Acceptance(result.ConversationId, result.MessageId, result.TurnIds.ToArray(), result.AcceptedSequence);
    }, ct);
    public async Task CancelAsync(string workspace, string conversation, string turn, CancellationToken ct) => await ExecuteAsync(async (services, token) =>
    {
        var session = await services.GetRequiredService<ISessionRepository>().GetAsync(conversation, token);
        if (session?.WorkspaceId != workspace) throw new InvalidOperationException("会话归属不匹配。");
        await services.GetRequiredService<IRequestTurnCancellationHandler>()
            .HandleAsync(new RequestTurnCancellationCommand(conversation, turn, LocalUserId), token);
        return true;
    }, ct);
    public Task<ProcessDetails> GetProcessAsync(RoleKey role, string message, CancellationToken ct) => ExecuteAsync(async (services, token) =>
    {
        var detail = await services.GetRequiredService<IAgentConversationProjectionService>().GetMessageProcessItemsAsync(
            role.WorkspaceId, LocalUserId, role.AgentId, message, token);
        return detail is null ? new ProcessDetails(message, []) : new ProcessDetails(detail.MessageId, detail.ProcessItems.Select(Map).ToArray(), Map(detail.Window));
    }, ct);
    private static ProcessItem Map(ProcessSummaryItem p) => new(p.Id, p.Kind, p.Status, p.Text, p.Sequence, p.Name, p.Arguments, p.Output, p.ExitCode, p.Message, p.ToolCallId, p.TurnId, p.DelegationRunId);
    private static EventWindow? Map(TurnEventWindow? w) => w is null ? null : new(w.TurnId, w.ThroughSequence, w.MinSequence, w.MaxSequence, w.HasMoreBefore);
    private static string? LocalAvatar(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal)) return null;
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot")) + Path.DirectorySeparatorChar;
            var file = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(path.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar)));
            return file.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(file) ? new Uri(file).AbsoluteUri : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { return null; }
    }
    public void Dispose() { lock (_gate) { if (_closed) return; _closed = true; _shutdown.Cancel(); } }
    internal async Task DrainAsync(CancellationToken ct)
    {
        Dispose(); Task[] operations; lock (_gate) operations = _operations.ToArray();
        try { await Task.WhenAll(operations).WaitAsync(ct).ConfigureAwait(false); }
        catch when (!ct.IsCancellationRequested) { /* Each caller observes its own operation failure. */ }
        _shutdown.Dispose();
    }
}
