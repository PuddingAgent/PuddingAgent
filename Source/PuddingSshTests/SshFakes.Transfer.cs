using System.Text;
using PuddingSsh.Contracts;
using PuddingSsh.Transport;

namespace PuddingSshTests;

/// <summary>
/// 内存版 SFTP 服务端（切片 B 门禁）：可以精确编排「原子替换是否支持」「标准 rename 是否覆盖」
/// 「排他创建是否支持」「realpath 指到哪里」「删除是否失败」，从而确定性覆盖发布语义与清理路径。
/// </summary>
internal sealed class FakeSftpChannel : ISshSftpChannel
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    /// <summary>服务端是否支持 <c>posix-rename@openssh.com</c>（原子替换）。</summary>
    public bool SupportsAtomicReplace { get; set; } = true;

    /// <summary>标准 SFTP RENAME 是否覆盖已有目标（OpenSSH 实测为 true）。</summary>
    public bool StandardRenameOverwrites { get; set; } = true;

    /// <summary>是否支持排他创建（SSH_FXF_EXCL）。</summary>
    public bool SupportsExclusiveCreate { get; set; } = true;

    /// <summary>realpath 结果覆盖（用于模拟符号链接逃逸）。</summary>
    public string? RealPathOverride { get; set; }

    /// <summary>删除是否失败（用于验证「清理失败必须明确报告残留」）。</summary>
    public bool FailDelete { get; set; }

    /// <summary>取消钩子：在把本地内容写进目标的过程中触发。</summary>
    public Action? OnBeforeUploadCommit { get; set; }

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

    public bool Exists(string remotePath) => _files.ContainsKey(remotePath);

    public bool IsRegularFile(string remotePath) => _files.ContainsKey(remotePath);

    public long GetLength(string remotePath) => _files.TryGetValue(remotePath, out var bytes) ? bytes.Length : 0;

    public string GetRealDirectory(string remotePath) => RealPathOverride ?? remotePath;

    public Stream OpenRead(string remotePath) => new MemoryStream(_files[remotePath], writable: false);

    public Stream OpenWriteNew(string remotePath)
    {
        if (_files.ContainsKey(remotePath) && SupportsExclusiveCreate)
        {
            throw new IOException("exclusive create rejected: file exists");
        }

        return new CommittingStream(bytes => _files[remotePath] = bytes);
    }

    public async Task UploadAsync(Stream source, string remotePath, long maxBytes, CancellationToken cancellationToken)
    {
        UploadCount++;
        UploadedPaths.Add(remotePath);

        // 真实服务端语义：排他创建**立刻落地**，中断时服务器上会留下**部分内容**，
        // 必须由调用方清理（这样"取消后是否清理"才是可被门禁检出的行为）。
        _files[remotePath] = [];
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                if (buffer.Length + read > maxBytes)
                {
                    throw new SshTransferLimitExceededException(maxBytes, buffer.Length + read);
                }

                buffer.Write(chunk, 0, read);
                _files[remotePath] = buffer.ToArray();
            }

            OnBeforeUploadCommit?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            _files[remotePath] = buffer.ToArray();
        }
        catch (Exception)
        {
            // 部分内容留在"服务器"上；调用方负责删除。
            _files[remotePath] = buffer.ToArray();
            throw;
        }
    }

    public async Task DownloadAsync(string remotePath, Stream destination, CancellationToken cancellationToken)
    {
        var bytes = _files[remotePath];
        await destination.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public void Rename(string oldPath, string newPath)
    {
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
        DeleteCount++;
        if (FailDelete)
        {
            throw new IOException("delete denied");
        }

        DeletedPaths.Add(remotePath);
        _files.Remove(remotePath);
    }

    public void Dispose() => Disposed = true;

    /// <summary>只在正常结束时提交字节的流：取消/异常路径不会留下部分内容。</summary>
    private sealed class CommittingStream(Action<byte[]> commit) : MemoryStream
    {
        private bool _committed;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_committed)
            {
                _committed = true;
                commit(ToArray());
            }

            base.Dispose(disposing);
        }
    }
}
