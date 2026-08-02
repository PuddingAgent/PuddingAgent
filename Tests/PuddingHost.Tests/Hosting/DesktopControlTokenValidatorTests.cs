using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

public sealed class DesktopControlTokenValidatorTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(),
        $"pudding-host-token-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("controlToken")]
    [InlineData("ControlToken")]
    public void Validate_AcceptsCurrentToken_ForEitherExistingJsonCasing(string propertyName)
    {
        WriteConfig(propertyName, "secret-token");
        var validator = new DesktopControlTokenValidator(_dataRoot);

        Assert.True(validator.Validate("secret-token"));
        Assert.False(validator.Validate("wrong-token"));
        Assert.False(validator.Validate(null));
    }

    [Fact]
    public void Validate_RereadsConfiguration_ForTokenRotation()
    {
        WriteConfig("controlToken", "token-one");
        var validator = new DesktopControlTokenValidator(_dataRoot);
        Assert.True(validator.Validate("token-one"));

        WriteConfig("controlToken", "token-two");
        Assert.False(validator.Validate("token-one"));
        Assert.True(validator.Validate("token-two"));
    }

    [Fact]
    public void Validate_DeniesMissingOrMalformedConfiguration()
    {
        var validator = new DesktopControlTokenValidator(_dataRoot);
        Assert.False(validator.Validate("anything"));

        Directory.CreateDirectory(Path.Combine(_dataRoot, "config"));
        File.WriteAllText(Path.Combine(_dataRoot, "config", "system.json"), "not-json");
        Assert.False(validator.Validate("anything"));
    }

    private void WriteConfig(string propertyName, string token)
    {
        var configDirectory = Path.Combine(_dataRoot, "config");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(
            Path.Combine(configDirectory, "system.json"),
            $$"""
            {
              "desktop": {
                "core": {
                  "{{propertyName}}": "{{token}}"
                }
              }
            }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch { }
    }
}
