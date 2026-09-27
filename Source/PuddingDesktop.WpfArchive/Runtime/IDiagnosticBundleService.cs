namespace PuddingDesktop.Runtime;

public interface IDiagnosticBundleService
{
    Task<string> CreateAsync(string dataRoot, CancellationToken cancellationToken);
}
