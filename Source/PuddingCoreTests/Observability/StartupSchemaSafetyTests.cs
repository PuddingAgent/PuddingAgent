namespace PuddingCoreTests.Observability;

[TestClass]
public sealed class StartupSchemaSafetyTests
{
    [TestMethod]
    public void Program_does_not_drop_session_state_tables_on_startup()
    {
        var programPath = FindRepoFile("Source", "PuddingAgent", "Program.cs");
        var source = File.ReadAllText(programPath);

        Assert.IsFalse(
            source.Contains("DROP TABLE IF EXISTS session_event_log", StringComparison.OrdinalIgnoreCase),
            "Startup must not drop append-only session event logs.");
        Assert.IsFalse(
            source.Contains("DROP TABLE IF EXISTS session_sub_agents", StringComparison.OrdinalIgnoreCase),
            "Startup must not drop persisted sub-agent status.");
    }

    private static string FindRepoFile(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        Assert.Fail($"Could not find repo file: {Path.Combine(segments)}");
        return string.Empty;
    }
}
