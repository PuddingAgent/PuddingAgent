namespace PuddingCode.Models;

/// <summary>
/// Worker 角色类型，决定蜂群中的职责分工。
/// 每个 Worker 是一个独立的 Agent 实例，拥有角色专属的 System Prompt 和作用域约束。
/// </summary>
public enum WorkerRole
{
    /// <summary>蜂群 Leader，负责定义契约、拆分任务、分配 Worker、监控进度和合并结果。</summary>
    Leader,

    /// <summary>构建工程师，负责实现 Leader 分配的具体模块/类/方法，仅限作用域内。</summary>
    Builder,

    /// <summary>QA 工程师，负责编写和运行测试用例，验证 Builder 输出。</summary>
    QA,

    /// <summary>文档工程师，负责生成和更新技术文档。</summary>
    Docs
}
