namespace PuddingCode.Skills;

/// <summary>
/// Agent 角色，决定可调用哪些技能。
/// 技能系统通用于蜂群 Agent、桌面精灵等所有 Agent 形态。
/// </summary>
public enum AgentRole
{
    /// <summary>蜂群 Leader，可调用编排技能。</summary>
    Leader,

    /// <summary>蜂群 Worker，受作用域限制。</summary>
    Worker,

    /// <summary>桌面精灵 / 通用助手，拥有用户级权限。</summary>
    Spirit
}
