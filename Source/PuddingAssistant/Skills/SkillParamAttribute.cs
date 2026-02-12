namespace PuddingAssistant.Skills;

/// <summary>
/// 标记技能方法的参数，提供 LLM 可读的描述。
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class SkillParamAttribute(string description) : Attribute
{
    /// <summary>参数描述，映射到 JSON Schema 的 description 字段。</summary>
    public string Description { get; } = description;
}
