using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingController.Services;

/// <summary>
/// Agent 模板注册表——合并内置模板与运行时动态注册的用户自定义模板。
/// V1 使用内存存储；内置模板只读，用户模板可增删改。
/// </summary>
public sealed class AgentTemplateRegistry
{
    private readonly ConcurrentDictionary<string, AgentTemplateDefinition> _userTemplates = new();

    /// <summary>查询指定模板（用户 → 内置的顺序）。</summary>
    public AgentTemplateDefinition? FindById(string templateId)
        => _userTemplates.TryGetValue(templateId, out var t) ? t
         : BuiltInAgentTemplates.FindById(templateId);

    /// <summary>列出所有可用模板（内置 + 用户自定义，用户模板优先覆盖同 ID）。</summary>
    public IReadOnlyList<AgentTemplateDefinition> GetAll()
    {
        var result = new Dictionary<string, AgentTemplateDefinition>(
            BuiltInAgentTemplates.GetAll().ToDictionary(t => t.TemplateId));

        foreach (var (id, t) in _userTemplates)
            result[id] = t;

        return result.Values.ToList();
    }

    /// <summary>注册或覆盖一个用户自定义模板。内置模板 ID 不可覆盖。</summary>
    public bool Register(AgentTemplateDefinition template, out string? error)
    {
        if (BuiltInAgentTemplates.FindById(template.TemplateId) is not null)
        {
            error = $"Template ID '{template.TemplateId}' is reserved by a built-in template.";
            return false;
        }
        _userTemplates[template.TemplateId] = template;
        error = null;
        return true;
    }

    /// <summary>删除用户自定义模板。内置模板不可删除。</summary>
    public bool Remove(string templateId, out string? error)
    {
        if (BuiltInAgentTemplates.FindById(templateId) is not null)
        {
            error = $"Cannot remove built-in template '{templateId}'.";
            return false;
        }
        error = null;
        return _userTemplates.TryRemove(templateId, out _);
    }

    /// <summary>列出所有用户自定义模板。</summary>
    public IReadOnlyList<AgentTemplateDefinition> GetUserTemplates()
        => _userTemplates.Values.ToList();
}
