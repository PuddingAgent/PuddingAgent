using PuddingSsh.Contracts;
using PuddingSsh.Transport;
using Renci.SshNet;
using Renci.SshNet.Common;

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

    public Stream OpenWriteNew(string remotePath)
    {
        try
        {
            return _client.Open(remotePath, FileMode.CreateNew);
        }
        catch (Exception ex) when (ex is SftpPathNotFoundException or SshException)
        {
            // 排他创建被拒：没有创建任何东西 ⇒ 上层不得回滚删除。
            throw new SshSftpTargetExistsException(remotePath);
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
