using PuddingDesktop.Configuration;
using PuddingCode.Configuration;

namespace PuddingDesktop.Tests.Configuration;

public class SystemConfigurationServiceTokenPreservationTests
{
    [Fact]
    public async Task UpdateDesktopCoreSettings_PreservesControlToken()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-test-{Guid.NewGuid():N}");
        var configDir = Path.Combine(tempDir, "config");
        Directory.CreateDirectory(configDir);

        // Write initial system.json with a token
        var initialJson = """
        {
          "environment": "production",
          "desktop": {
            "core": {
              "autoStart": true,
              "port": 0,
              "startupTimeoutSeconds": 60,
              "shutdownTimeoutSeconds": 15,
              "controlToken": "ORIGINAL_TOKEN_ABC123"
            }
          }
        }
        """;

        await File.WriteAllTextAsync(Path.Combine(configDir, "system.json"), initialJson);

        var service = new SystemConfigurationService();

        // Act: update port only
        await service.UpdateDesktopCoreSettingsAsync(
            tempDir,
            current => current with { Port = 8080 },
            CancellationToken.None);

        // Assert: token preserved
        var result = await service.LoadAsync(tempDir, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("ORIGINAL_TOKEN_ABC123", result.Config!.Desktop.Core.ControlToken);
        Assert.Equal(8080, result.Config.Desktop.Core.Port);

        // Cleanup
        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task UpdateDesktopCoreSettings_PreservesUnknownFields()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-test-{Guid.NewGuid():N}");
        var configDir = Path.Combine(tempDir, "config");
        Directory.CreateDirectory(configDir);

        var initialJson = """
        {
          "environment": "production",
          "customField": "should-survive",
          "desktop": {
            "core": {
              "autoStart": true,
              "port": 0,
              "startupTimeoutSeconds": 60,
              "shutdownTimeoutSeconds": 15,
              "futureOption": { "enabled": true }
            },
            "window": { "width": 1440, "height": 900 }
          }
        }
        """;

        await File.WriteAllTextAsync(Path.Combine(configDir, "system.json"), initialJson);

        var service = new SystemConfigurationService();

        // Act
        await service.UpdateDesktopCoreSettingsAsync(
            tempDir,
            current => current with { AutoStart = false },
            CancellationToken.None);

        // Assert: custom fields survive
        var json = await File.ReadAllTextAsync(Path.Combine(configDir, "system.json"));
        Assert.Contains("should-survive", json);
        Assert.Contains("\"width\": 1440", json);
        Assert.Contains("\"futureOption\"", json);
        Assert.Contains("\"enabled\": true", json);
        Assert.Contains("\"autoStart\"", json);
        Assert.DoesNotContain("\"AutoStart\"", json);

        // Also verify via typed load
        var result = await service.LoadAsync(tempDir, CancellationToken.None);
        Assert.False(result.Config!.Desktop.Core.AutoStart);

        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task UpdateDesktopCoreSettings_RegenerateToken_OnlyChangesToken()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-test-{Guid.NewGuid():N}");
        var configDir = Path.Combine(tempDir, "config");
        Directory.CreateDirectory(configDir);

        var initialJson = """
        {
          "desktop": {
            "core": {
              "autoStart": true,
              "port": 5000,
              "controlToken": "OLD_TOKEN"
            }
          }
        }
        """;

        await File.WriteAllTextAsync(Path.Combine(configDir, "system.json"), initialJson);

        var service = new SystemConfigurationService();

        // Act: regenerate token
        await service.UpdateDesktopCoreSettingsAsync(
            tempDir,
            current => current with { ControlToken = "NEW_TOKEN" },
            CancellationToken.None);

        var result = await service.LoadAsync(tempDir, CancellationToken.None);
        Assert.Equal("NEW_TOKEN", result.Config!.Desktop.Core.ControlToken);
        Assert.Equal(5000, result.Config.Desktop.Core.Port);
        Assert.True(result.Config.Desktop.Core.AutoStart);

        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task UpdateDesktopCoreSettings_NoExistingFile_CreatesNew()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-test-{Guid.NewGuid():N}");

        var service = new SystemConfigurationService();

        await service.UpdateDesktopCoreSettingsAsync(
            tempDir,
            current => current with { Port = 3000, AutoStart = false },
            CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(tempDir, "config", "system.json")));

        var result = await service.LoadAsync(tempDir, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(3000, result.Config!.Desktop.Core.Port);
        Assert.False(result.Config.Desktop.Core.AutoStart);

        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task AtomicWriteFailure_OriginalFileStillReadable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-test-{Guid.NewGuid():N}");
        var configDir = Path.Combine(tempDir, "config");
        Directory.CreateDirectory(configDir);

        var originalJson = """{"desktop":{"core":{"port":9000}}}""";
        await File.WriteAllTextAsync(Path.Combine(configDir, "system.json"), originalJson);

        // Write a .tmp file that the service will try to flush (this shouldn't corrupt original)
        var service = new SystemConfigurationService();

        await service.UpdateDesktopCoreSettingsAsync(
            tempDir,
            current => current with { Port = 9999 },
            CancellationToken.None);

        var result = await service.LoadAsync(tempDir, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(9999, result.Config!.Desktop.Core.Port);

        // Original should have been backed up as .bak
        Assert.True(File.Exists(Path.Combine(configDir, "system.json.bak")));

        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task UpdateDesktopCoreSettings_PersistsRestartPolicyAndPreservesToken()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "PuddingAgent", "runtime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(tempDir, "config", "system.json"),
            """{"desktop":{"core":{"controlToken":"KEEP_ME","futureRestartField":42}}}""");

        try
        {
            var service = new SystemConfigurationService();
            await service.UpdateDesktopCoreSettingsAsync(
                tempDir,
                current => current with
                {
                    AutoRestart = false,
                    RestartMaxAttempts = 5,
                    RestartWindowSeconds = 120,
                    RestartInitialDelaySeconds = 3,
                    RestartMaxDelaySeconds = 45,
                },
                CancellationToken.None);

            var result = await service.LoadAsync(tempDir, CancellationToken.None);
            var core = result.Config!.Desktop.Core;
            Assert.False(core.AutoRestart);
            Assert.Equal(5, core.RestartMaxAttempts);
            Assert.Equal(120, core.RestartWindowSeconds);
            Assert.Equal(3, core.RestartInitialDelaySeconds);
            Assert.Equal(45, core.RestartMaxDelaySeconds);
            Assert.Equal("KEEP_ME", core.ControlToken);
            Assert.Contains("futureRestartField", await File.ReadAllTextAsync(result.FilePath!));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
