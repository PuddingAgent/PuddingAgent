namespace PuddingDesktop.Configuration;

public interface IDesktopBootstrapSettingsStore
{
    Task<DesktopBootstrapSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(DesktopBootstrapSettings settings, CancellationToken cancellationToken);
}
