using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Protocol;

namespace Pudding.ToolPlugins.Windows;

public sealed class WindowsPluginWorkerFactory(string workerExecutable) : IPluginWorkerFactory
{
    public async Task<IPluginWorker> StartAsync(PluginPackage package, PluginScope scope, string activationId, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PluginException("plugin.platform_incompatible", "spawn", "Worker supervisor requires Windows.");
        if (!File.Exists(workerExecutable)) throw new PluginException("plugin.worker_missing", "spawn", "Plugin Worker is not installed with Core.");
        var name = "pudding-plugin-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
        Process? process = null; WorkerJob? job = null;
        try
        {
            var start = new ProcessStartInfo(Path.GetFullPath(workerExecutable))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(workerExecutable))! };
            start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(name);
            start.ArgumentList.Add("--activation"); start.ArgumentList.Add(activationId);
            var names = new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "PATH", "ProgramFiles", "ProgramFiles(x86)", "DOTNET_ROOT" };
            var allowed = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
            start.Environment.Clear(); foreach (var (key, value) in allowed) if (value is not null) start.Environment[key] = value;
            process = Process.Start(start) ?? throw new IOException("Cannot create worker.");
            // The trusted worker only waits on the pipe until Initialize. No plugin code runs before job assignment.
            job = new WorkerJob(process);
            _ = DrainAsync(process.StandardOutput); _ = DrainAsync(process.StandardError);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await pipe.WaitForConnectionAsync(deadline.Token);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != process.Id)
                throw new PluginException("plugin.handshake_failed", "handshake", "Unexpected named-pipe client process.");
            return new WindowsWorker(process, job, pipe, activationId);
        }
        catch
        {
            job?.Dispose(); pipe.Dispose();
            if (process is not null) { try { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { } process.Dispose(); }
            throw;
        }
    }
    private static async Task DrainAsync(StreamReader reader)
    { try { var buffer = new char[4096]; while (await reader.ReadAsync(buffer) > 0) { } } catch (IOException) { } catch (ObjectDisposedException) { } }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

    private sealed class WindowsWorker(Process process, WorkerJob job, NamedPipeServerStream pipe, string activationId) : IPluginWorker
    {
        private readonly SemaphoreSlim _rpc = new(1, 1);
        private int _stopped;
        private readonly object _stopGate = new();
        private Task<bool>? _stopTask;
        public bool StopWasForced { get; private set; }
        public int ProcessId { get; } = process.Id;
        public bool HasExited { get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } } }
        public async Task<JsonElement> RequestAsync(PluginWorkerRequest request, Func<string, JsonElement, CancellationToken, Task<JsonElement>> callback, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var remaining = request.Deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new OperationCanceledException("Plugin deadline expired.", ct);
            timeout.CancelAfter(remaining);
            await _rpc.WaitAsync(timeout.Token);
            try
            {
                if (HasExited || Volatile.Read(ref _stopped) != 0) throw new PluginException("plugin.worker_exited", "execute", "Worker has exited.", "unknown");
                var id = Guid.NewGuid().ToString("N");
                await PluginWire.WriteAsync(pipe, new(request.Kind, id, activationId, request.Payload, Deadline: request.Deadline), timeout.Token);
                var callbacks = 0;
                while (true)
                {
                    var frame = await PluginWire.ReadAsync(pipe, timeout.Token);
                    if (frame.ActivationId != activationId || frame.ReplyTo != id)
                        throw new PluginException("plugin.protocol_invalid", "execute", "Unexpected activation or request correlation.", "unknown");
                    if (frame.Kind == "reply") return frame.Payload;
                    if (frame.Kind == "error")
                    {
                        var error = frame.Payload.Deserialize<PluginFailure>(PluginJson.Options)!;
                        throw new PluginException(error);
                    }
                    if (frame.Kind != "core_call" || ++callbacks > 32)
                        throw new PluginException("plugin.protocol_invalid", "core_callback", "Invalid or excessive callbacks.", "unknown");
                    JsonElement response; var kind = "reply";
                    try { response = await callback(frame.Payload.GetProperty("method").GetString()!, frame.Payload.GetProperty("arguments"), timeout.Token); }
                    catch (PluginException ex) { kind = "error"; response = PluginJson.Value(ex.Failure); }
                    await PluginWire.WriteAsync(pipe, new(kind, Guid.NewGuid().ToString("N"), activationId, response, frame.RequestId), timeout.Token);
                }
            }
            finally { _rpc.Release(); }
        }
        public Task<bool> StopAsync(CancellationToken ct)
        { lock (_stopGate) return _stopTask ??= StopCoreAsync(CancellationToken.None); }
        private async Task<bool> StopCoreAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                if (!HasExited && await _rpc.WaitAsync(0, CancellationToken.None))
                {
                    try
                    {
                        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        var id = Guid.NewGuid().ToString("N");
                        await PluginWire.WriteAsync(pipe, new("shutdown", id, activationId, PluginJson.Value(new { }), Deadline: DateTimeOffset.UtcNow.AddSeconds(2)), grace.Token);
                        var reply = await PluginWire.ReadAsync(pipe, grace.Token);
                        if (reply.Kind == "reply" && reply.ReplyTo == id && reply.ActivationId == activationId)
                            await process.WaitForExitAsync(grace.Token);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or PluginException or ObjectDisposedException) { }
                    finally { _rpc.Release(); }
                }
                StopWasForced = !HasExited;
                // Always close the job, also reclaiming children left behind after a graceful parent exit.
                job.Dispose(); pipe.Dispose();
            }
            if (HasExited) { process.Dispose(); return true; }
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct); process.Dispose(); return true; }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { return HasExited; }
        }
        public async ValueTask DisposeAsync() { await StopAsync(CancellationToken.None); process.Dispose(); }
    }
}
