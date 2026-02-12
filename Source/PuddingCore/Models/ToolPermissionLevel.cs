namespace PuddingCode.Models;

/// <summary>
/// Agent 工具权限分级。三级：Low（自动授权）、Medium（需 Agent 配置）、High（需用户运行时确认）。
/// </summary>
public enum ToolPermissionLevel
{
    /// <summary>低风险：只读操作，自动授权，无需额外权限</summary>
    Low = 0,
    /// <summary>中等风险：读取系统/用户信息、写入文件、网络访问。需要 Agent 配置授权</summary>
    Medium = 1,
    /// <summary>高风险：删除、格式化、重启、SSH。需要用户运行时确认</summary>
    High = 2,
}
