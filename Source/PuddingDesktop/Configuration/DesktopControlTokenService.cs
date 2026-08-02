using System.Security.Cryptography;

namespace PuddingDesktop.Configuration;

public interface IDesktopControlTokenService
{
    Task<string> GetOrCreateAsync(string dataRoot, CancellationToken cancellationToken);
    Task<string> RegenerateAsync(string dataRoot, CancellationToken cancellationToken);
}

/// <summary>
/// Manages the Core control token stored in <DataRoot>/config/system.json
/// under desktop.core.controlToken.
/// Token is never displayed in full; only "已生成"/"重新生成" shown in UI.
/// </summary>
public sealed class DesktopControlTokenService : IDesktopControlTokenService
{
    private readonly ISystemConfigurationService _configService;

    public DesktopControlTokenService(ISystemConfigurationService configService)
    {
        _configService = configService;
    }

    public async Task<string> GetOrCreateAsync(string dataRoot, CancellationToken cancellationToken)
    {
        var result = await _configService.LoadAsync(dataRoot, cancellationToken);
        if (!result.Success || result.Config is null)
            throw new InvalidOperationException($"Cannot load system config: {string.Join("; ", result.Errors)}");

        var existing = result.Config.Desktop.Core.ControlToken;
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        var token = GenerateToken();
        await _configService.UpdateDesktopCoreSettingsAsync(
            dataRoot,
            current => current with { ControlToken = token },
            cancellationToken);

        return token;
    }

    public async Task<string> RegenerateAsync(string dataRoot, CancellationToken cancellationToken)
    {
        var result = await _configService.LoadAsync(dataRoot, cancellationToken);
        if (!result.Success || result.Config is null)
            throw new InvalidOperationException($"Cannot load system config: {string.Join("; ", result.Errors)}");

        var token = GenerateToken();
        await _configService.UpdateDesktopCoreSettingsAsync(
            dataRoot,
            current => current with { ControlToken = token },
            cancellationToken);

        return token;
    }

    private static string GenerateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }
}
