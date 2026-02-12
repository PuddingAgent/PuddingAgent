using Microsoft.Extensions.Logging.Abstractions;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class DirectMemoryLlmClientTests
{
    [TestMethod]
    public async Task ClassifyAsync_WhenMemoryLlmConfigMissing_ShouldThrowConfigurationError()
    {
        var client = new DirectMemoryLlmClient(
            new TestHttpClientFactory(),
            NullLogger<DirectMemoryLlmClient>.Instance);

        InvalidOperationException? ex = null;
        try
        {
            await client.ClassifyAsync("remember this");
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        Assert.IsNotNull(ex);
        StringAssert.Contains(ex.Message, "Memory LLM config is missing or incomplete");
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
