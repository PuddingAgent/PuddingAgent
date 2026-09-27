namespace PuddingDesktop.Foundation;

/// <summary>Presentation only; never authorizes tools or changes Agent execution targets.</summary>
public interface IDesktopServices
{
    Task ShowAsync(ShellPage page, CancellationToken cancellationToken = default);
    Task OpenDocumentAsync(WorkspaceDocument document, CancellationToken cancellationToken = default);
}

public interface IKernelSession : IAsyncDisposable
{
    Uri WorkbenchAddress { get; }
    CancellationToken Stopping => CancellationToken.None;
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IKernelSessionFactory
{
    // A failing factory must dispose its partially constructed host before throwing.
    Task<IKernelSession> StartAsync(string dataRoot, CancellationToken cancellationToken);
}
