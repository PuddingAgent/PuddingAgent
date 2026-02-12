using System.Runtime.CompilerServices;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>
/// ITerminalProcessManager 的空操作实现，用于 TerminalProcessManager 未被 DI 注册时的降级。
/// 所有操作返回空/失败，不会抛出异常。
/// </summary>
public sealed class NoOpTerminalProcessManager : ITerminalProcessManager
{
    public static readonly NoOpTerminalProcessManager Instance = new();

    public Task<TerminalProcessInfo> StartAsync(string sessionId, string command, string workingDir, CancellationToken ct)
        => Task.FromResult(new TerminalProcessInfo
        {
            ProcessId = "noop",
            SessionId = sessionId,
            Command = command,
            Status = TerminalProcessStatus.Failed,
        });

    public async IAsyncEnumerable<string> SubscribeAsync(string processId, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return "[ERROR] TerminalProcessManager is not registered.";
        await Task.CompletedTask;
    }

    public Task<TerminalOutputSnapshot?> ReadOutputAsync(
        string processId,
        int offset = 0,
        int? maxLines = null,
        int? maxChars = null,
        CancellationToken ct = default)
        => Task.FromResult<TerminalOutputSnapshot?>(null);

    public Task<bool> WriteInputAsync(string processId, string input, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> KillAsync(string processId)
        => Task.FromResult(false);

    public IReadOnlyList<TerminalProcessInfo> ListProcesses(string? sessionId = null)
        => [];

    public Task<int> ReapAsync()
        => Task.FromResult(0);
}
