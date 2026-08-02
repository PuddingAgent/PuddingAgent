using PuddingDesktop.Runtime;

namespace PuddingDesktop.Tests.Runtime;

public sealed class DesktopSingleInstanceServiceTests
{
    [Fact]
    public async Task SecondInstance_SignalsPrimaryWithoutAcquiringOwnership()
    {
        var root = CreateTemporaryDirectory();
        var key = $"PuddingDesktop.Tests.{Guid.NewGuid():N}";
        try
        {
            await using var primary = new DesktopSingleInstanceService(key, root);
            await using var secondary = new DesktopSingleInstanceService(key, root);
            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            primary.ActivationRequested += (_, _) => activated.TrySetResult();

            Assert.True(primary.TryAcquirePrimary());
            Assert.False(secondary.TryAcquirePrimary());
            Assert.True(await secondary.SignalPrimaryAsync(CancellationToken.None));
            await activated.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisposedPrimary_ReleasesOwnership()
    {
        var root = CreateTemporaryDirectory();
        var key = $"PuddingDesktop.Tests.{Guid.NewGuid():N}";
        try
        {
            var first = new DesktopSingleInstanceService(key, root);
            Assert.True(first.TryAcquirePrimary());
            await first.DisposeAsync();

            await using var next = new DesktopSingleInstanceService(key, root);
            Assert.True(next.TryAcquirePrimary());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "runtime-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
