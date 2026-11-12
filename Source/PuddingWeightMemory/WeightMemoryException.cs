namespace PuddingWeightMemory;

/// <summary>
/// 组件在不可继续的状态下抛出的异常：库内编码器身份不符、事件标识被复用于不同内容、
/// 向量维度或范数非法、反馈指向不存在的章节。
/// <para>
/// 这些都不是"可以降级"的情况——降级只发生在写入队列满（见 <see cref="IWriteAdmissionGate"/>），
/// 而这里是调用方或配置的硬错误，静默吞掉会让权重停在无法解释的状态。
/// </para>
/// </summary>
public sealed class WeightMemoryException : Exception
{
    /// <summary>用一条说明创建异常。</summary>
    /// <param name="message">说明，包含可定位的事实（分区、章节或事件标识）。</param>
    public WeightMemoryException(string message)
        : base(message)
    {
    }

    /// <summary>用一条说明与底层原因创建异常。</summary>
    /// <param name="message">说明。</param>
    /// <param name="innerException">底层原因。</param>
    public WeightMemoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
