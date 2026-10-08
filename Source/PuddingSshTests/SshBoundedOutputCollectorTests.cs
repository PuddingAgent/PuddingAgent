using System.Text;
using PuddingSsh.Execution;

namespace PuddingSshTests;

/// <summary>
/// 有界输出采集器（设计 §9.1）的白盒断言：预算、计数、截断标记与**截断不破坏字符**。
/// 这些断言不需要真实服务端——正是「先独立可测」的兑现方式。
/// </summary>
[TestClass]
public sealed class SshBoundedOutputCollectorTests
{
    [TestMethod]
    public async Task CaptureIsBounded_WhileReceivedBytesKeepCounting()
    {
        var collector = new SshBoundedOutputCollector(8);
        var stdout = new MemoryStream(Encoding.ASCII.GetBytes(new string('a', 100)));

        await collector.DrainStdoutAsync(stdout);

        Assert.AreEqual(8, collector.CapturedBytes);
        Assert.AreEqual(100, collector.ReceivedBytes, "捕获满后必须继续计数");
        Assert.IsTrue(collector.IsTruncated);
        Assert.AreEqual("aaaaaaaa", collector.DecodeStdout());
        Assert.AreEqual(string.Empty, collector.DecodeStderr());
        Assert.AreEqual(16, collector.ReservedBufferBytes, "固定缓冲 = 2 × 预算，与输出量无关");
    }

    [TestMethod]
    public async Task BudgetIsSharedBetweenStdoutAndStderr()
    {
        var collector = new SshBoundedOutputCollector(8);
        var stdout = new MemoryStream(Encoding.ASCII.GetBytes("12345678"));
        var stderr = new MemoryStream(Encoding.ASCII.GetBytes("abcdefgh"));

        await Task.WhenAll(collector.DrainStdoutAsync(stdout), collector.DrainStderrAsync(stderr));

        Assert.AreEqual(8, collector.CapturedBytes, "两条流共享同一个预算");
        Assert.AreEqual(16, collector.ReceivedBytes);
        Assert.IsTrue(collector.IsTruncated);
        Assert.AreEqual(8, collector.DecodeStdout().Length + collector.DecodeStderr().Length);
    }

    [TestMethod]
    public async Task Truncation_DoesNotSplitCharacters()
    {
        var collector = new SshBoundedOutputCollector(4); // 「中」是 3 字节 ⇒ 第 2 个字符必然被截断
        var stdout = new MemoryStream(Encoding.UTF8.GetBytes("中中"));

        await collector.DrainStdoutAsync(stdout);

        Assert.AreEqual(4, collector.CapturedBytes);
        Assert.AreEqual("中", collector.DecodeStdout());
        Assert.DoesNotContain('\uFFFD', collector.DecodeStdout(), "截断处不得产生替换字符");
    }

    [TestMethod]
    public async Task MultibyteCharacters_SurviveBytewiseReads()
    {
        var collector = new SshBoundedOutputCollector(32);
        var payload = Encoding.UTF8.GetBytes("中中中");
        var stream = new ChunkedStream(payload, maxChunk: 1); // 每次只给 1 字节，故意切在字符中间

        await collector.DrainStdoutAsync(stream);

        Assert.AreEqual("中中中", collector.DecodeStdout());
        Assert.AreEqual(payload.Length, collector.ReceivedBytes);
        Assert.IsFalse(collector.IsTruncated);
    }

    [TestMethod]
    public async Task EmptyStreams_ProduceEmptyResults()
    {
        var collector = new SshBoundedOutputCollector(16);

        await Task.WhenAll(
            collector.DrainStdoutAsync(new MemoryStream()),
            collector.DrainStderrAsync(new MemoryStream()));

        Assert.AreEqual(0, collector.CapturedBytes);
        Assert.AreEqual(0, collector.ReceivedBytes);
        Assert.IsFalse(collector.IsTruncated);
        Assert.AreEqual(string.Empty, collector.DecodeStdout());
        Assert.AreEqual(string.Empty, collector.DecodeStderr());
    }

    [TestMethod]
    public void BudgetMustBePositive()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshBoundedOutputCollector(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshBoundedOutputCollector(-1));
    }

    /// <summary>按固定上限分片的流：模拟 SSH 通道按包/小片交付数据，检验跨读取边界的解码。</summary>
    private sealed class ChunkedStream(byte[] payload, int maxChunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(Math.Min(count, maxChunk), payload.Length - _position);
            if (take <= 0)
            {
                return 0;
            }

            Array.Copy(payload, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var take = Math.Min(Math.Min(buffer.Length, maxChunk), payload.Length - _position);
            if (take <= 0)
            {
                return ValueTask.FromResult(0);
            }

            payload.AsMemory(_position, take).CopyTo(buffer);
            _position += take;
            return ValueTask.FromResult(take);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
