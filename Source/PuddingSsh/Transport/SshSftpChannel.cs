using System.Buffers;
using PuddingSsh.Contracts;
using PuddingSsh.Transport;
using Renci.SshNet;

namespace PuddingSsh.Transport;

/// <summary>
/// SSH.NET 的 SFTP 通道（切片 B）：把用到的原语收成小接口。
/// 复制循环是**同步**的（SSH.NET 两种重载都会在本线程推进）：阻塞由协调器的有界 worker 隔离，
/// 与 exec 的「同步启动段」同一处理方式。
/// </summary>
internal sealed class SshSftpChannel : ISshSftpChannel
{
    private const int CopyBufferSize = 64 * 1024;

    private readonly SftpClient _client;
    private string? _workingDirectory;

    public SshSftpChannel(SftpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public bool IsConnected => _client.IsConnected;

    public bool Exists(string remotePath) => _client.Exists(remotePath);

    public bool IsRegularFile(string remotePath)
    {
        var attributes = _client.GetAttributes(remotePath);
        return attributes.IsRegularFile;
    }

    public long GetLength(string remotePath) => _client.GetAttributes(remotePath).Size;

    /// <summary>
    /// SFTP realpath：SSH.NET 没有公开 REALPATH 原语，但 <c>ChangeDirectory</c> 之后的
    /// <c>WorkingDirectory</c> 就是服务端解析后的真实路径。
    /// </summary>
    public string GetRealDirectory(string remotePath)
    {
        _workingDirectory ??= _client.WorkingDirectory;
        try
        {
            _client.ChangeDirectory(remotePath);
            return _client.WorkingDirectory;
        }
        finally
        {
            try
            {
                _client.ChangeDirectory(_workingDirectory);
            }
            catch (Exception)
            {
                // 恢复失败不影响已解析出的 realpath。
            }
        }
    }

    public Stream OpenRead(string remotePath) => _client.OpenRead(remotePath);

    public Stream OpenWriteNew(string remotePath) => _client.Open(remotePath, FileMode.CreateNew);

    public async Task UploadAsync(Stream source, string remotePath, long maxBytes, CancellationToken cancellationToken)
    {
        using var target = _client.Open(remotePath, FileMode.CreateNew);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long total = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                total += read;
                if (total > maxBytes)
                {
                    // 超限立即中止（不把整文件写完再判断）。
                    throw new SshTransferLimitExceededException(maxBytes, total);
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task DownloadAsync(string remotePath, Stream destination, CancellationToken cancellationToken)
    {
        using var source = _client.OpenRead(remotePath);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Rename(string oldPath, string newPath) => _client.RenameFile(oldPath, newPath, false);

    public void RenamePosix(string oldPath, string newPath) => _client.RenameFile(oldPath, newPath, true);

    public void Delete(string remotePath) => _client.DeleteFile(remotePath);

    public void Dispose()
    {
        try
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }
        }
        catch (Exception)
        {
            // 连接可能已被远端关闭。
        }

        _client.Dispose();
    }
}
