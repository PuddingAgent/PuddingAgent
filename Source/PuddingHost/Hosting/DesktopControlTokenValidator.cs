using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingCode.Configuration;

namespace PuddingHost.Hosting;

/// <summary>
/// Validates the desktop control token against the current system.json value.
/// The file is read per request so token rotation does not require a Core restart.
/// </summary>
public sealed class DesktopControlTokenValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _dataRoot;

    public DesktopControlTokenValidator(string dataRoot)
    {
        _dataRoot = dataRoot;
    }

    public bool Validate(string? presentedToken)
    {
        if (string.IsNullOrWhiteSpace(presentedToken))
            return false;
        return ValidateAgainstStored(presentedToken);
    }

    public Task<bool> ValidateAsync(Microsoft.AspNetCore.Http.IHeaderDictionary headers)
    {
        var token = headers[PuddingBrowser.Protocol.BrowserBridgeProtocol.ControlTokenHeader].FirstOrDefault();
        return Task.FromResult(Validate(token));
    }

    private bool ValidateAgainstStored(string presentedToken)
    {
        var storedToken = ReadStoredToken();
        if (string.IsNullOrWhiteSpace(storedToken))
            return false;

        var presentedBytes = Encoding.UTF8.GetBytes(presentedToken);
        var storedBytes = Encoding.UTF8.GetBytes(storedToken);
        return presentedBytes.Length == storedBytes.Length
            && CryptographicOperations.FixedTimeEquals(presentedBytes, storedBytes);
    }

    private string? ReadStoredToken()
    {
        var configPath = Path.Combine(_dataRoot, "config", "system.json");
        if (!File.Exists(configPath))
            return null;

        try
        {
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<PuddingSystemConfig>(json, JsonOptions)
                ?.Desktop.Core.ControlToken;
        }
        catch
        {
            return null;
        }
    }
}
