using PuddingChat;
using Xunit;

public class ConfigurationTests
{
    private static ProviderModelEdit Valid => new("test", "Test", "https://example.invalid/v1", true,
        new("model", "Model", "openai", 32768, 4096), SecretChange.Keep, null);
    [Theory]
    [InlineData("file:///tmp")]
    [InlineData("https://user:password@example.invalid")]
    [InlineData("https://example.invalid?key=secret")]
    public void RejectsNonEndpointUrls(string url) => Assert.Throws<ArgumentException>(() => (Valid with { BaseUrl = url }).Validate());
    [Fact] public void ReplaceRequiresKey() => Assert.Throws<ArgumentException>(() => (Valid with { KeyChange = SecretChange.Replace }).Validate());
    [Fact] public void EditToStringDoesNotExposeKey() => Assert.DoesNotContain("fixture-key", (Valid with { NewKey = "fixture-key" }).ToString());
    [Fact] public void ValidSettingsAccepted() => Valid.Validate();
}
