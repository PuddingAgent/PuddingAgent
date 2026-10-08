using System.Buffers;
using System.Text;

namespace PuddingSsh.Execution;

/// <summary>
/// stdout/stderr **有界**采集器（设计 §9.1）：两条流同时排空，独立计数，共享一个固定捕获预算。
/// <para>
/// 硬约束：禁止 <c>RunCommand</c> 后读取整份 <c>Result/Error</c>，禁止串行 <c>ReadToEnd</c>、
/// 禁止无界 <c>StringBuilder/MemoryStream</c>。捕获满后**继续计数并丢弃**额外输出，直到执行终止或 deadline。
/// </para>
/// <para>
/// 峰值内存是固定的：两个 <c>_budget</c> 大小的字节数组（默认 2 × 64 KiB）+ 每次读取的 16 KiB 池化缓冲，
/// 与远端实际输出量无关。增量 UTF-8 解码使用 <c>flush: false</c>，截断处**不会**产生替换字符。
/// </para>
/// </summary>
internal sealed class SshBoundedOutputCollector
{
    private const int ReadBufferSize = 16 * 1024;

    private readonly byte[] _stdout;
    private readonly byte[] _stderr;
    private readonly int _budget;
    private readonly object _budgetGate = new();

    private int _stdoutLength;
    private int _stderrLength;
    private int _budgetUsed;
    private long _receivedBytes;
    private bool _truncated;

    public SshBoundedOutputCollector(int budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        _budget = budgetBytes;
        _stdout = new byte[budgetBytes];
        _stderr = new byte[budgetBytes];
    }

    /// <summary>双流实际到达的字节总数（含被丢弃部分）。</summary>
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);

    public long CapturedBytes
    {
        get
        {
            lock (_budgetGate)
            {
                return _stdoutLength + _stderrLength;
            }
        }
    }

    public bool IsTruncated
    {
        get
        {
            lock (_budgetGate)
            {
                return _truncated;
            }
        }
    }

    /// <summary>固定缓冲峰值（字节）：两个预算数组。用于探针核对「与输出量无关」。</summary>
    public long ReservedBufferBytes => 2L * _budget;

    public Task DrainStdoutAsync(Stream stream) => DrainAsync(stream, isStderr: false);

    public Task DrainStderrAsync(Stream stream) => DrainAsync(stream, isStderr: true);

    public string DecodeStdout()
    {
        lock (_budgetGate)
        {
            return Decode(_stdout, _stdoutLength);
        }
    }

    public string DecodeStderr()
    {
        lock (_budgetGate)
        {
            return Decode(_stderr, _stderrLength);
        }
    }

    private async Task DrainAsync(Stream stream, bool isStderr)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 通道被服务端关闭、或本端释放连接导致的流中止 = 该流结束。
                    // 真实传输故障由 ExecuteAsync 的任务负责报告，这里不吞掉执行事实。
                    break;
                }

                if (read <= 0)
                {
                    break;
                }

                Interlocked.Add(ref _receivedBytes, read);
                Store(buffer, read, isStderr);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Store(byte[] source, int count, bool isStderr)
    {
        lock (_budgetGate)
        {
            var remaining = _budget - _budgetUsed;
            var take = Math.Min(remaining, count);
            if (take > 0)
            {
                if (isStderr)
                {
                    Buffer.BlockCopy(source, 0, _stderr, _stderrLength, take);
                    _stderrLength += take;
                }
                else
                {
                    Buffer.BlockCopy(source, 0, _stdout, _stdoutLength, take);
                    _stdoutLength += take;
                }

                _budgetUsed += take;
            }

            if (take < count)
            {
                _truncated = true;
            }
        }
    }

    private static string Decode(byte[] bytes, int length)
    {
        if (length == 0)
        {
            return string.Empty;
        }

        // UTF-8 字符数必 ≤ 字节数；flush:false 让末尾不完整的序列留在解码器状态里（不产生 U+FFFD），
        // 这样「截断」不会破坏字符。
        var chars = new char[length];
        var decoder = Encoding.UTF8.GetDecoder();
        var written = decoder.GetChars(bytes, 0, length, chars, 0, flush: false);
        return written == 0 ? string.Empty : new string(chars, 0, written);
    }
}
