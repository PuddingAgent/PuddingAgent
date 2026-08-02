using System.IO.Compression;
using PuddingDesktop.Core;
using PuddingDesktop.Runtime;

namespace PuddingDesktop.Tests.Runtime;

public sealed class DiagnosticBundleServiceTests
{
    [Fact]
    public async Task CreateAsync_IncludesConfigKeysButNotSecretValues()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "runtime-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(dataRoot, "config", "system.json"),
            """
            {
              "desktop": { "core": { "controlToken": "TOP_SECRET_TOKEN" } },
              "providers": [{ "apiKey": "TOP_SECRET_KEY" }]
            }
            """);

        try
        {
            var logs = new CoreProcessLogBuffer();
            logs.Append("[Desktop] safe log line");
            logs.Append("Authorization: Bearer-secret-value");
            logs.Append("controlToken=DESKTOP_SECRET");
            var service = new DiagnosticBundleService(
                () => new DesktopRuntimeSnapshot { State = DesktopRuntimeState.Stopped },
                logs);

            var bundlePath = await service.CreateAsync(dataRoot, CancellationToken.None);

            using var archive = ZipFile.OpenRead(bundlePath);
            Assert.Contains(archive.Entries, entry => entry.FullName == "runtime.json");
            Assert.Contains(archive.Entries, entry => entry.FullName == "core-tail.log");
            var keysEntry = Assert.Single(archive.Entries, entry => entry.FullName == "system-config-keys.txt");
            using var reader = new StreamReader(keysEntry.Open());
            var keys = await reader.ReadToEndAsync();
            Assert.Contains("desktop.core.controlToken", keys);
            Assert.Contains("providers[].apiKey", keys);
            Assert.DoesNotContain("TOP_SECRET_TOKEN", keys);
            Assert.DoesNotContain("TOP_SECRET_KEY", keys);

            var logEntry = Assert.Single(archive.Entries, entry => entry.FullName == "core-tail.log");
            using var logReader = new StreamReader(logEntry.Open());
            var logText = await logReader.ReadToEndAsync();
            Assert.Contains("[REDACTED]", logText);
            Assert.DoesNotContain("Bearer-secret-value", logText);
            Assert.DoesNotContain("DESKTOP_SECRET", logText);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }
}
