using Microsoft.Extensions.DependencyInjection;
using PuddingDesktop.Foundation;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-04 document slice: template prompt/Markdown fields and per-instance Markdown documents.
///
/// Template saves carry a fingerprint of exactly what was read and refuse to overwrite a concurrent
/// edit. Instance saves use Core's own SHA-256 conflict token, so a stale editor can never silently
/// overwrite another writer. Nothing here clears an undisplayed document.
/// </summary>
internal sealed partial class DesktopAgentDirectorySettings
{
    private Task<T> AgentPair<T>(string operationId,
        Func<AgentTemplateFileService, WorkspaceAgentFileService, CancellationToken, Task<T>> body,
        CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId, (scope, token) => body(
            scope.Services.GetRequiredService<AgentTemplateFileService>(),
            scope.Services.GetRequiredService<WorkspaceAgentFileService>(), token), cancellationToken);

    public Task<AgentTemplateDocuments> ReadTemplateDocumentsAsync(string templateId, CancellationToken cancellationToken = default)
        => Templates("agents.documents.template.read", async (service, token) =>
        {
            var template = await service.GetTemplateAsync(templateId, token)
                ?? throw new InvalidOperationException($"模板 {templateId} 不存在。");
            return Build(template);
        }, cancellationToken);

    public Task SaveTemplateDocumentsAsync(AgentTemplateDocuments documents, CancellationToken cancellationToken = default)
        => Templates("agents.documents.template.save", async (service, token) =>
        {
            var current = await service.GetTemplateAsync(documents.TemplateId, token)
                ?? throw new InvalidOperationException($"模板 {documents.TemplateId} 不存在。");
            var stored = Build(current);
            if (!string.Equals(stored.Fingerprint, documents.Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new SettingsConflictException(
                    "模板文档已在别处修改。请重新读取后再保存，避免覆盖他人的改动。");
            await service.UpdateTemplateAsync(documents.TemplateId, TemplateRequest(current,
                systemPrompt: Value(documents, "systemPrompt", current.SystemPrompt),
                userPromptTemplate: Value(documents, "userPromptTemplate", current.UserPromptTemplate),
                personaPrompt: Value(documents, "personaPrompt", current.PersonaPrompt),
                agentsPrompt: Value(documents, "agentsPrompt", current.AgentsPrompt),
                toolsDescription: Value(documents, "toolsDescription", current.ToolsDescription),
                bootstrapTemplate: Value(documents, "bootstrapTemplate", current.BootstrapTemplate),
                memoryPrompt: Value(documents, "memoryPrompt", current.MemoryPrompt)), token);
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<AgentInstanceDocument>> ReadInstanceDocumentsAsync(
        string workspaceId, string agentId, CancellationToken cancellationToken = default)
        => AgentPair("agents.documents.instance.read", async (templates, instances, token) =>
        {
            var agent = await instances.GetAgentAsync(workspaceId, agentId, token)
                ?? throw new InvalidOperationException($"角色 {agentId} 不存在。");
            var template = string.IsNullOrWhiteSpace(agent.SourceTemplateId)
                ? null
                : await templates.GetTemplateAsync(agent.SourceTemplateId, token);
            var templateDocuments = template is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : Build(template).Documents.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            var result = new List<AgentInstanceDocument>();
            foreach (var slot in AgentDocuments.InstanceSlots)
            {
                var templateSlot = AgentDocuments.TemplateSlotFor(slot.Key);
                var templateDefault = templateSlot.Length > 0 && templateDocuments.TryGetValue(templateSlot, out var value)
                    ? value
                    : "";
                try
                {
                    var document = await instances.ReadDocumentAsync(agentId, slot.Key, token);
                    result.Add(new AgentInstanceDocument(slot.Key, slot.Title, document.FileName, document.Content,
                        document.Sha256, templateDefault, AgentDocuments.IsOverride(document.Content, templateDefault),
                        document.LastModifiedAt));
                }
                catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException)
                {
                    // Core refuses to read a document the manifest does not reference, or one whose file is
                    // missing. Both are repairable by a save, so report the reason instead of failing the page.
                    var issue = exception is FileNotFoundException
                        ? "实例缺少该文档文件；保存会用当前内容重新创建。"
                        : "实例 manifest 未引用该文档；保存会创建文件并修复引用。";
                    result.Add(new AgentInstanceDocument(slot.Key, slot.Title, slot.FileName, templateDefault,
                        "", templateDefault, false, DateTimeOffset.MinValue, issue));
                }
            }
            return (IReadOnlyList<AgentInstanceDocument>)result;
        }, cancellationToken);

    public Task SaveInstanceDocumentAsync(string workspaceId, string agentId, string key, string content,
        string expectedSha256, CancellationToken cancellationToken = default)
        => Instances("agents.documents.instance.save", async (service, token) =>
        {
            if (AgentDocuments.Find(AgentDocuments.InstanceSlots, key) is null)
                throw new ArgumentException($"未知的实例文档 '{key}'。");
            try
            {
                await service.UpdateDocumentAsync(agentId, key, content,
                    string.IsNullOrEmpty(expectedSha256) ? null : expectedSha256, token);
            }
            catch (PuddingCode.Agents.AgentSelfStateConflictException conflict)
            {
                throw new SettingsConflictException(
                    $"{conflict.Document} 已在别处修改（当前 SHA-256 {conflict.ActualSha256[..Math.Min(8, conflict.ActualSha256.Length)]}）。" +
                    "请重新读取后再保存。");
            }
            return true;
        }, cancellationToken);

    private static AgentTemplateDocuments Build(GlobalAgentTemplateDto template)
    {
        var documents = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["systemPrompt"] = template.SystemPrompt ?? "",
            ["userPromptTemplate"] = template.UserPromptTemplate ?? "",
            ["personaPrompt"] = template.PersonaPrompt ?? "",
            ["agentsPrompt"] = template.AgentsPrompt ?? "",
            ["toolsDescription"] = template.ToolsDescription ?? "",
            ["bootstrapTemplate"] = template.BootstrapTemplate ?? "",
            ["memoryPrompt"] = template.MemoryPrompt ?? "",
        };
        return new AgentTemplateDocuments(template.TemplateId, AgentDocuments.Fingerprint(documents), documents);
    }

    private static string? Value(AgentTemplateDocuments documents, string key, string? fallback) =>
        documents.Documents.TryGetValue(key, out var value) ? value : fallback;
}
