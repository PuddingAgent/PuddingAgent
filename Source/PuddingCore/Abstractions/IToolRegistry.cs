namespace PuddingCode.Abstractions;

/// <summary>
/// 管理所有可用工具。支持手动注册和按名称查找。
/// </summary>
public interface IToolRegistry
{
    /// <summary>注册一个工具</summary>
    void Register(ITool tool);

    /// <summary>按名称查找工具</summary>
    ITool? GetTool(string name);

    /// <summary>获取所有已注册工具</summary>
    IReadOnlyList<ITool> GetAllTools();
}
