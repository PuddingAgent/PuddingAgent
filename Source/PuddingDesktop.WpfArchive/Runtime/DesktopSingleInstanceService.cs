using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using PuddingDesktop.Configuration;

namespace PuddingDesktop.Runtime;

public sealed class DesktopSingleInstanceService : IAsyncDisposable
{
    private readonly string _semaphoreName;
    private readonly string _pipeName;
    private readonly string _discoveryPath;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Semaphore? _ownershipSemaphore;
    private Task? _listenerTask;
    private string? _primaryPipeName;
    private bool _ownsSemaphore;
    private int _disposeState;

    public event EventHandler? ActivationRequested;

    public DesktopSingleInstanceService(
        string instanceKey = "PuddingDesktop",
        string? discoveryDirectory = null)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(instanceKey)))[..16];
        _semaphoreName = $"Local\\PuddingDesktop.SingleInstance.v1.{hash}";
        _pipeName = $"PuddingDesktop.Activate.v1.{hash}.{Environment.ProcessId}";
        _discoveryPath = Path.Combine(
            discoveryDirectory ?? DesktopBootstrapPathProvider.GetDirectoryPath(),
            $"desktop.instance.{hash}");
    }

    public bool TryAcquirePrimary()
    {
        ThrowIfDisposed();
        _ownershipSemaphore = new Semaphore(1, 1, _semaphoreName);
        _ownsSemaphore = _ownershipSemaphore.WaitOne(0);
        if (!_ownsSemaphore)
        {
            _ownershipSemaphore.Dispose();
            _ownershipSemaphore = null;
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_discoveryPath)!);
        File.WriteAllText(_discoveryPath, _pipeName);
        _primaryPipeName = _pipeName;
        _listenerTask = ListenAsync(_lifetimeCts.Token);
        return true;
    }

    public async Task<bool> SignalPrimaryAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(_discoveryPath))
                {
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                var pipeName = (await File.ReadAllTextAsync(_discoveryPath, cancellationToken)).Trim();
                if (pipeName.Length == 0)
                    continue;

                await using var client = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(250, cancellationToken);
                await client.WriteAsync(new byte[] { 1 }, cancellationToken);
                await client.FlushAsync(cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        return false;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                var buffer = new byte[1];
                if (await server.ReadAsync(buffer, cancellationToken) > 0)
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _lifetimeCts.Cancel();
        if (_listenerTask is not null)
        {
            try { await _listenerTask; }
            catch { }
        }

        if (_ownsSemaphore && _ownershipSemaphore is not null)
        {
            try { _ownershipSemaphore.Release(); }
            catch { }
        }
        _ownershipSemaphore?.Dispose();

        if (_ownsSemaphore)
        {
            try
            {
                if (File.Exists(_discoveryPath)
                    && string.Equals(File.ReadAllText(_discoveryPath).Trim(), _primaryPipeName, StringComparison.Ordinal))
                {
                    File.Delete(_discoveryPath);
                }
            }
            catch { }
        }

        _lifetimeCts.Dispose();
    }
}
