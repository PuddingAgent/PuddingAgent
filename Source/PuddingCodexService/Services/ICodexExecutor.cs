using PuddingCodexService.Models;

namespace PuddingCodexService.Services;

public interface ICodexExecutor
{
    Task<CodexExecutionResult> ExecuteAsync(CodexTaskRecord task, CancellationToken ct);
}
