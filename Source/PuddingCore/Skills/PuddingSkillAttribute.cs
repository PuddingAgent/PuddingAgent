namespace PuddingCode.Skills;

/// <summary>
/// 标记一个方法为 PuddingCode 技能。
/// SkillRegistry 通过反射自动发现此标记，生成 JSON Function Schema 并注册。
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PuddingSkillAttribute(string description) : Attribute
{
    /// <summary>技能描述，用于 LLM function calling 的 description 字段。</summary>
    public string Description { get; } = description;

    /// <summary>
    /// 允许调用此技能的角色。空数组表示所有角色均可调用。
    /// </summary>
    public AgentRole[] AllowedRoles { get; set; } = [];

    /// <summary>技能所属分组（如 Environment、Social、Orchestration），用于分组展示和懒加载。</summary>
    public string Group { get; set; } = "General";
}
