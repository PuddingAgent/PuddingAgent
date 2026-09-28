using PuddingDesktop.Composition;
using PuddingDesktop.Foundation;

namespace PuddingDesktop.CompositionTests;

/// <summary>
/// Regression coverage for Desktop host <b>startup configuration composition</b>.
/// <para>
/// Kept in its own file rather than appended to <c>DesktopCompositionTests</c> so the two can be
/// edited and reviewed independently. The data-root and desktop-services doubles are intentionally
/// local copies of the ones in that file, for the same reason.
/// </para>
/// </summary>
public sealed class DesktopHostStartupConfigTests
{
    /// <summary>
    /// A real DataRoot whose <c>config/system.json</c> enables authoritative TaskAutoDispatch must
    /// still boot the Desktop host.
    /// <para>
    /// The Goal prerequisites (<c>TaskBoundGoals:Enabled</c>, <c>GoalRuns:Enabled</c>,
    /// <c>GoalRuns:ContinuationEnabled</c>) are packaged defaults that live in the host's own
    /// <c>appsettings.json</c>. When the Desktop output was missing that file, those sections fell
    /// back to disabled defaults while the operator section still enabled authoritative dispatch, so
    /// <c>OptionsValidationException</c> killed Core during <c>Host.StartAsync</c> and the shell sat
    /// on its "初始化进程内 Core" skeleton forever with no role list.
    /// </para>
    /// <para>
    /// No other test started the Desktop host with auto-dispatch enabled in <c>system.json</c>, which
    /// is exactly why this reached a real user. Removing <c>appsettings.json</c> from the host output
    /// makes this test fail with that original exception.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RealHostStartsWhenOperatorSystemConfigEnablesAuthoritativeAutoDispatch()
    {
        var root = await CreateIsolatedDataRootAsync();
        await MergeIntoSystemConfigAsync(root, writer =>
        {
            // Exactly the operator sections observed in a real DataRoot.
            writer.WritePropertyName("taskAutoDispatch");
            writer.WriteStartObject();
            writer.WriteNumber("PolicyRevision", 1);
            writer.WriteBoolean("Enabled", true);
            writer.WriteString("Mode", "authoritative");
            writer.WriteBoolean("EventDrivenEnabled", true);
            writer.WriteEndObject();

            // A real system.json also carries a *partial* GoalRuns section. Configuration merges per
            // key, so the packaged Enabled/ContinuationEnabled must survive this overlap — if the
            // operator section replaced the whole section, authoritative dispatch would lose its
            // prerequisite for a second, subtler reason.
            writer.WritePropertyName("GoalRuns");
            writer.WriteStartObject();
            writer.WriteString("CheckWorkingDirectory", AppContext.BaseDirectory);
            writer.WriteNumber("CheckTimeoutSeconds", 600);
            writer.WriteEndObject();
        });

        await using var kernel = new InProcessKernel(new DesktopKernelFactory(new DesktopHostServices()));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await kernel.StartAsync(root, timeout.Token);

            Assert.Equal(DesktopKernelState.Ready, kernel.Snapshot.State);
            using var http = new HttpClient();
            Assert.True((await http.GetAsync(
                    new Uri(kernel.Snapshot.WorkbenchAddress!, "/health/ready"), timeout.Token))
                .IsSuccessStatusCode);

            await kernel.StopAsync(timeout.Token);
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// The packaged defaults must actually reach the host's configuration. This asserts the values the
    /// prerequisite check reads, so a future content-packaging regression names the affected section
    /// instead of only surfacing as an opaque startup failure.
    /// </summary>
    [Fact]
    public void PackagedAppSettingsShipTheGoalPrerequisitesAuthoritativeDispatchRequires()
    {
        var hostConfig = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(hostConfig),
            $"The host content root is missing appsettings.json at {hostConfig}; " +
            "a DataRoot that enables authoritative TaskAutoDispatch would fail to start Core.");

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(hostConfig));
        var root = document.RootElement;

        Assert.True(root.GetProperty("TaskBoundGoals").GetProperty("Enabled").GetBoolean());
        Assert.True(root.GetProperty("GoalRuns").GetProperty("Enabled").GetBoolean());
        Assert.True(root.GetProperty("GoalRuns").GetProperty("ContinuationEnabled").GetBoolean());
    }

    private static async Task<string> CreateIsolatedDataRootAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Pudding-kernel-config-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var key = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(root, "config", "system.json"),
            System.Text.Json.JsonSerializer.Serialize(
                new { Jwt = new { Key = key, Issuer = "kernel-test", Audience = "kernel-test" } }));
        return root;
    }

    /// <summary>Rewrites system.json with an extra section, preserving the existing keys verbatim.</summary>
    private static async Task MergeIntoSystemConfigAsync(string root, Action<System.Text.Json.Utf8JsonWriter> writeExtra)
    {
        var path = Path.Combine(root, "config", "system.json");
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
        using var stream = new MemoryStream();
        await using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
                property.WriteTo(writer);
            writeExtra(writer);
            writer.WriteEndObject();
        }

        await File.WriteAllTextAsync(path, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    private sealed class DesktopHostServices : IDesktopServices
    {
        public Task ShowAsync(ShellPage page, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task OpenDocumentAsync(WorkspaceDocument document, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
