using System.Security.Cryptography;
using System.Text;

namespace PuddingDesktop.Foundation;

/// <summary>
/// One editable slot. Template slots are fields on the global template; instance slots are the
/// per-instance Markdown documents Core keeps in the instance root.
/// </summary>
public sealed record AgentDocumentSlot(string Key, string Title, string FileName);

/// <summary>
/// Template documents with a fingerprint of exactly what was read. A save compares the fingerprint
/// against the stored template and refuses to overwrite a concurrent edit.
/// </summary>
public sealed record AgentTemplateDocuments(
    string TemplateId, string Fingerprint, IReadOnlyDictionary<string, string> Documents);

/// <summary>
/// An instance document plus the template default it was seeded from, so the UI can say whether the
/// instance currently overrides the template instead of implying one value is the other.
/// </summary>
public sealed record AgentInstanceDocument(
    string Key, string Title, string FileName, string Content, string Sha256,
    string TemplateDefault, bool OverridesTemplate, DateTimeOffset LastModifiedAt, string Issue = "")
{
    public bool IsHealthy => Issue.Length == 0;
}

public static class AgentDocuments
{
    /// <summary>Template-level documents, in the order the editor shows them.</summary>
    public static IReadOnlyList<AgentDocumentSlot> TemplateSlots { get; } =
    [
        new("systemPrompt", "系统提示词", ""),
        new("userPromptTemplate", "用户提示词模板", ""),
        new("personaPrompt", "人格设定（SOUL）", "SOUL.md"),
        new("agentsPrompt", "协作说明（AGENTS）", "AGENTS.md"),
        new("toolsDescription", "工具说明（TOOLS）", "TOOLS.md"),
        new("bootstrapTemplate", "启动模板（BOOTSTRAP）", "BOOTSTRAP.md"),
        new("memoryPrompt", "记忆提示（MEMORY）", "MEMORY.md"),
    ];

    /// <summary>Instance documents. Keys and file names match Core's self-state document contract.</summary>
    public static IReadOnlyList<AgentDocumentSlot> InstanceSlots { get; } =
    [
        new("soul", "人格设定（SOUL）", "SOUL.md"),
        new("agents", "协作说明（AGENTS）", "AGENTS.md"),
        new("tools", "工具说明（TOOLS）", "TOOLS.md"),
        new("bootstrap", "启动模板（BOOTSTRAP）", "BOOTSTRAP.md"),
        new("memory", "记忆提示（MEMORY）", "MEMORY.md"),
        new("heartbeat", "心跳提示词（HEARTBEAT）", "heartbeatPrompt.md"),
    ];

    /// <summary>Which template slot seeds which instance document. Empty key means "no template default".</summary>
    public static string TemplateSlotFor(string instanceKey) => instanceKey switch
    {
        "soul" => "personaPrompt",
        "agents" => "agentsPrompt",
        "tools" => "toolsDescription",
        "bootstrap" => "bootstrapTemplate",
        "memory" => "memoryPrompt",
        _ => ""
    };

    public static AgentDocumentSlot? Find(IReadOnlyList<AgentDocumentSlot> slots, string key) =>
        slots.FirstOrDefault(slot => string.Equals(slot.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Stable fingerprint of the document set. Order-independent across keys, but any change in a
    /// document's text (including whitespace and line endings) changes the result.
    /// </summary>
    public static string Fingerprint(IReadOnlyDictionary<string, string?> documents)
    {
        var builder = new StringBuilder();
        foreach (var slot in TemplateSlots)
        {
            builder.Append(slot.Key).Append('\u0000');
            if (documents.TryGetValue(slot.Key, out var value) && value is not null) builder.Append(value);
            builder.Append('\u0001');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>Instance content equal to the template default is not an override; trailing newlines are not "edits".</summary>
    public static bool IsOverride(string? instanceContent, string? templateDefault) =>
        !string.Equals((instanceContent ?? "").TrimEnd('\r', '\n'), (templateDefault ?? "").TrimEnd('\r', '\n'), StringComparison.Ordinal);
}
