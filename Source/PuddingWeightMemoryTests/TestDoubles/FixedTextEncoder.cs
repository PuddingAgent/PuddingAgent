using PuddingWeightMemory;

namespace PuddingWeightMemoryTests;

/// <summary>
/// 确定性编码器替身：文本到向量的映射在构造时固定，未知文本直接失败。
/// <para>
/// 失败而不是哈希兜底，是为了让"测试里出现了一个没人配置过的文本"变成一条明确的错误，
/// 而不是一个看起来正常、实际不可解释的分数。返回副本，避免被调用方就地改写映射表。
/// </para>
/// </summary>
internal sealed class FixedTextEncoder : ITextEncoder
{
    private readonly Dictionary<string, float[]> _vectors;

    internal FixedTextEncoder(int dimension, params (string Text, float[] Vector)[] entries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimension, 1);
        Dimension = dimension;
        _vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);

        foreach (var (text, vector) in entries)
        {
            if (vector.Length != dimension)
                throw new ArgumentException($"文本 \"{text}\" 的向量是 {vector.Length} 维，期望 {dimension} 维。", nameof(entries));

            _vectors.Add(text, vector);
        }
    }

    public int Dimension { get; }

    public ValueTask<float[]> EncodeAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_vectors.TryGetValue(text, out var vector))
            throw new InvalidOperationException($"测试编码器没有为 \"{text}\" 配置向量。");

        return ValueTask.FromResult((float[])vector.Clone());
    }
}
