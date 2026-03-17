using System.Collections.Concurrent;
using PuddingCode.Platform;
using PuddingAgent;

namespace PuddingController.Services;

/// <summary>
/// 内存 Workspace 目录——V1 仅支持内存且预置默认 Workspace。
/// </summary>
public sealed class InMemoryWorkspaceCatalog : IWorkspaceCatalog
{
    private readonly ConcurrentDictionary<string, WorkspaceDefinition> _workspaces = new();

    /// <summary>预置默认 Workspace。</summary>
    public void SeedDefaults()
    {
        var ws = new WorkspaceDefinition
        {
            WorkspaceId = "default",
            Name = "Default Workspace",
            Description = "预置的默认工作空间",
            AgentTemplateIds = BuiltInAgentTemplates.GetAll().Select(t => t.TemplateId).ToList(),
            ChannelBindings = [new ChannelBindingDefinition { ChannelId = "cli", ChannelType = "cli" }],
            PermissionPolicy = new PermissionPolicyDefinition
            {
                DefaultDeny = false,
            },
        };
        _workspaces[ws.WorkspaceId] = ws;
    }

    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ReloadAsync(CancellationToken ct = default)
    {
        _workspaces.Clear();
        SeedDefaults();
        return Task.CompletedTask;
    }

    public WorkspaceDefinition? GetWorkspace(string workspaceId)
        => _workspaces.GetValueOrDefault(workspaceId);

    public WorkspaceDefinition? FindByChannel(string channelId)
        => _workspaces.Values.FirstOrDefault(ws =>
            ws.ChannelBindings.Any(c => c.ChannelId == channelId));

    public IReadOnlyList<WorkspaceDefinition> GetAll()
        => _workspaces.Values.ToList();
}
