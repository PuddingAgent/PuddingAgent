using System.Text.Json;
using PuddingDesktop.Configuration;
using PuddingDesktop.Runtime;

namespace PuddingDesktop.Tests.Configuration;

public sealed class DesktopBootstrapSettingsJsonTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    [Fact]
    public void Deserialize_AcceptsNamedCloseBehavior()
    {
        var settings = JsonSerializer.Deserialize<DesktopBootstrapSettings>(
            """{"closeBehavior":"ExitAndStopCore"}""",
            JsonOptions);

        Assert.NotNull(settings);
        Assert.Equal(DesktopCloseBehavior.ExitAndStopCore, settings.CloseBehavior);
    }

    [Fact]
    public void Serialize_WritesNamedCloseBehavior()
    {
        var json = JsonSerializer.Serialize(
            new DesktopBootstrapSettings
            {
                CloseBehavior = DesktopCloseBehavior.MinimizeToTray,
            },
            JsonOptions);

        Assert.Contains("\"closeBehavior\": \"MinimizeToTray\"", json);
    }
}
