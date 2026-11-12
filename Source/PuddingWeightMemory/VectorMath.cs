using System.Buffers.Binary;

namespace PuddingWeightMemory;

/// <summary>
/// 归一化、点积与向量落盘的唯一实现。
/// <para>
/// 数值口径：累加一律用 <see cref="double"/>，存储与比较用 <see cref="float"/>。实验实现是
/// <c>float32</c> 全程累加，两者在 512 维、单位向量上的差在 1e-7 量级，远小于打分阈值；
/// 用 double 累加是为了让"同分并列"这类判定不受累加顺序影响。
/// </para>
/// </summary>
internal static class VectorMath
{
    /// <summary>低于此范数视为无效向量（实验实现同样以 1e-8 为界）。</summary>
    private const double MinimumNorm = 1e-8;

    /// <summary>点积。长度不一致即失败，不做截断或补零。</summary>
    internal static double Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length)
            throw new WeightMemoryException($"向量维度不一致：{left.Length} 与 {right.Length}。");

        var sum = 0.0;
        for (var index = 0; index < left.Length; index++)
            sum += (double)left[index] * right[index];

        return sum;
    }

    /// <summary>L2 归一化，返回新数组（不改写调用方数据）。</summary>
    internal static float[] Normalize(ReadOnlySpan<float> vector)
    {
        if (vector.Length == 0)
            throw new WeightMemoryException("空向量无法归一化。");

        var sum = 0.0;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
                throw new WeightMemoryException("向量含非有限分量。");

            sum += (double)value * value;
        }

        var norm = Math.Sqrt(sum);
        if (!(norm >= MinimumNorm))
            throw new WeightMemoryException($"向量范数无效（{norm:E3}），无法归一化。");

        var normalized = new float[vector.Length];
        for (var index = 0; index < vector.Length; index++)
            normalized[index] = (float)(vector[index] / norm);

        return normalized;
    }

    /// <summary>按小端序写入 BLOB，使库文件跨机器可读。</summary>
    internal static byte[] ToBytes(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var index = 0; index < vector.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), vector[index]);

        return bytes;
    }

    /// <summary>读取 <see cref="ToBytes"/> 写下的 BLOB。</summary>
    internal static float[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
            throw new WeightMemoryException($"权重 BLOB 长度 {bytes.Length} 不是 4 的倍数。");

        var vector = new float[bytes.Length / sizeof(float)];
        for (var index = 0; index < vector.Length; index++)
            vector[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(index * sizeof(float)));

        return vector;
    }
}
