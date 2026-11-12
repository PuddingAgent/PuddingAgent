namespace PuddingWeightMemory;

/// <summary>
/// 文本到向量的一步：组件唯一的模型依赖，由宿主注入（产品里是常驻本地编码进程的适配器，
/// 测试里是确定性替身）。
/// <para>
/// 契约：同一文本必须给出同一向量；向量长度必须等于 <see cref="Dimension"/> 且每个分量有限。
/// 组件负责归一化，实现方不需要预归一化。更换编码器定义必须重建同一分区的全部向量，
/// 因此该身份由 <see cref="WeightMemoryOptions.EncoderId"/> 记在库里并在打开时比对。
/// </para>
/// </summary>
public interface ITextEncoder
{
    /// <summary>向量维度。打开已有数据库时与库内记录的维度比对，不一致即失败。</summary>
    int Dimension { get; }

    /// <summary>把一段文本编码为定长向量。</summary>
    /// <param name="text">待编码文本，非空。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>长度为 <see cref="Dimension"/> 的有限分量向量。</returns>
    ValueTask<float[]> EncodeAsync(string text, CancellationToken ct);
}
