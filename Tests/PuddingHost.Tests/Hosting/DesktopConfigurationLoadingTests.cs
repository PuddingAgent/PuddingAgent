using PuddingCode.Configuration;

namespace PuddingHost.Tests.Hosting;

public sealed class DesktopConfigurationLoadingTests
{
    [Fact]
    public void LlmConfigurationLoad_DoesNotDeadlockOnDesktopSynchronizationContext()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "pudding-host-tests",
            Guid.NewGuid().ToString("N"));
        var paths = PuddingDataPaths.FromRoot(tempRoot);
        Directory.CreateDirectory(paths.ConfigRoot);
        File.WriteAllText(
            paths.SystemConfigFile("llm.providers.json"),
            """
            {
              "providers": [
                {
                  "providerId": "deepseek",
                  "name": "DeepSeek",
                  "protocol": "openai",
                  "baseUrl": "https://api.deepseek.com/v1",
                  "models": [
                    { "modelId": "deepseek-chat", "name": "DeepSeek Chat" }
                  ]
                }
              ]
            }
            """);

        Exception? failure = null;
        ConfigLoadResult<PuddingLlmProvidersConfig>? result = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new NonPumpingSynchronizationContext());
            try
            {
                result = new PuddingFileConfigLoader(paths)
                    .LoadLlmProvidersAsync()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };

        try
        {
            thread.Start();
            Assert.True(
                thread.Join(TimeSpan.FromSeconds(5)),
                "Configuration loading captured the desktop SynchronizationContext and deadlocked.");
            Assert.Null(failure);
            Assert.NotNull(result);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // A WPF startup thread cannot pump continuations while it is blocked
            // in the synchronous composition-root call.
        }
    }
}
