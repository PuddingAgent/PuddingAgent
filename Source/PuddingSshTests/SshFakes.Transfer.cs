using System.Text;
using PuddingSsh.Contracts;
using PuddingSsh.Transport;

namespace PuddingSshTests;

/// <summary>
/// 内存版 SFTP 服务端（切片 B 门禁）：可以精确编排「原子替换是否支持」「标准 rename 是否覆盖」
/// 「排他创建是否支持/是否被竞争对手抢先」「realpath 指到哪里」「删除是否失败」「STAT 后远端增长」，
/// 从而确定性覆盖发布语义、所有权回滚与清理路径。
/// </summary>
internal sealed class FakeSftpChannel : ISshSftpChannel
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    /// <summary>服务端是否支持 <c>posix-rename@openssh.com</c>（原子替换）。</summary>
    public bool SupportsAtomicReplace { get; set; } = true;

    /// <summary>标准 SFTP RENAME 是否覆盖已有目标（OpenSSH 实测为 true）。</summary>
    public bool StandardRenameOverwrites { get; set; } = true;

    /// <summary>是否支持排他创建（SSH_FXF_EXCL）；关闭时表现为普通覆盖创建。</summary>
    public bool SupportsExclusiveCreate { get; set; } = true;

    /// <summary>realpath 结果覆盖（用于模拟符号链接逃逸）。</summary>
    public string? RealPathOverride { get; set; }

    /// <summary>删除是否失败（用于验证「清理失败必须明确报告残留」）。</summary>
    public bool FailDelete { get; set; }

    /// <summary>排他创建**之前**的钩子（模拟竞争对手抢先创建目标）。</summary>
    public Action<string>? OnBeforeCreateNew { get; set; }

    /// <summary>自定义只读流（模拟 STAT 之后远端文件增长）。</summary>
    public Func<string, Stream>? OnOpenRead { get; set; }

    /// <summary>每次写入后的钩子 (targetPath, totalBytesWritten)；可在写入中途取消/抛错。</summary>
    public Action<string, long>? OnWrite { get; set; }

    /// <summary>模拟"创建可能已发生但调用失败"：先落地文件再抛错（所有权不可证明）；只对指定路径生效。</summary>
    public string? FailAfterCreatePath { get; set; }

    public int UploadCount { get; private set; }

    public int RenameCount { get; private set; }

    public int PosixRenameCount { get; private set; }

    public int DeleteCount { get; private set; }

    public bool Disposed { get; private set; }

    public bool IsConnected => !Disposed;

    public List<string> DeletedPaths { get; } = [];

    public List<string> UploadedPaths { get; } = [];

    public FakeSftpChannel Seed(string path, string content) => Seed(path, Encoding.UTF8.GetBytes(content));

    public FakeSftpChannel Seed(string path, byte[] content)
    {
        _files[path] = content;
        return this;
    }

    public string? ContentOf(string path) =>
        _files.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    public byte[]? BytesOf(string path) => _files.TryGetValue(path, out var bytes) ? bytes : null;

    public bool Exists(string remotePath)
    {
        EnsureNotDisposed();
        return _files.ContainsKey(remotePath);
    }

    public bool IsRegularFile(string remotePath) => _files.ContainsKey(remotePath);

    public long GetLength(string remotePath) => _files.TryGetValue(remotePath, out var bytes) ? bytes.Length : 0;

    public string GetRealDirectory(string remotePath) => RealPathOverride ?? remotePath;

    public Stream OpenRead(string remotePath) =>
        OnOpenRead is { } factory ? factory(remotePath) : new MemoryStream(_files[remotePath], writable: false);

    public Stream OpenWriteNew(string remotePath)
    {
        EnsureNotDisposed();
        OnBeforeCreateNew?.Invoke(remotePath);

        if (_files.ContainsKey(remotePath) && SupportsExclusiveCreate)
        {
            // 排他创建被拒绝：没有创建/写入任何东西 ⇒ 上层不得回滚删除。
            throw new SshSftpTargetExistsException(remotePath);
        }

        UploadCount++;
        UploadedPaths.Add(remotePath);
        _files[remotePath] = [];
        if (FailAfterCreatePath is { } failPath && string.Equals(failPath, remotePath, StringComparison.Ordinal))
        {
            // 服务端/库在"创建已落地"之后才报错：本次无法证明所有权 ⇒ 上层不得回滚删除。
            throw new IOException("create ack lost after file was created");
        }

        return new WriteThroughStream(this, remotePath);
    }

    public void Rename(string oldPath, string newPath)
    {
        EnsureNotDisposed();
        RenameCount++;
        if (_files.ContainsKey(newPath) && !StandardRenameOverwrites)
        {
            throw new IOException("standard rename rejected: target exists");
        }

        _files[newPath] = _files[oldPath];
        _files.Remove(oldPath);
    }

    public void RenamePosix(string oldPath, string newPath)
    {
        EnsureNotDisposed();
        PosixRenameCount++;
        if (!SupportsAtomicReplace)
        {
            throw new NotSupportedException("posix-rename@openssh.com not supported");
        }

        _files[newPath] = _files[oldPath];
        _files.Remove(oldPath);
    }

    public void Delete(string remotePath)
    {
        EnsureNotDisposed();
        DeleteCount++;
        if (FailDelete)
        {
            throw new IOException("delete denied");
        }

        DeletedPaths.Add(remotePath);
        _files.Remove(remotePath);
    }

    public void Dispose() => Disposed = true;

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(Disposed, this);

    private void CommitWrite(string path, byte[] content) => _files[path] = content;

    /// <summary>写入即落地：模拟真实服务端在中断时会留下**部分内容**。</summary>
    private sealed class WriteThroughStream(FakeSftpChannel channel, string path) : Stream
    {
        private readonly MemoryStream _buffer = new();
        private bool _disposed;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _buffer.Length;

        public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _buffer.Write(buffer, offset, count);
            channel.CommitWrite(path, _buffer.ToArray());
            channel.OnWrite?.Invoke(path, _buffer.Length);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _buffer.Write(buffer.Span);
            channel.CommitWrite(path, _buffer.ToArray());
            channel.OnWrite?.Invoke(path, _buffer.Length);
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                // 不在这里重新提交：写入已逐次落地；否则清理删除后会被"复活"。
                _buffer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
