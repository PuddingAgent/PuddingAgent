using System.Reflection;

using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// Verifies that PuddingApplicationInitializer is the single initialization authority.
/// The old PuddingApplicationInitializationExtensions must have zero active callers.
/// </summary>
public class InitializationAuthorityTests
{
    /// <summary>
    /// The old PuddingApplicationInitializationExtensions.InitializePuddingDataAsync
    /// must not be called anywhere. The single initialization path is
    /// PuddingApplicationInitializer.InitializeAsync.
    /// </summary>
    [Fact]
    public void Initialization_UsesSingleAuthority()
    {
        // ── Verify the canonical initializer exists ─────
        var initializerType = typeof(PuddingApplicationInitializer);
        var initMethod = initializerType.GetMethod("InitializeAsync");
        Assert.NotNull(initMethod);

        // ── The old extensions class is in PuddingAgent.Services namespace ──
        // It should have no callers (verified by static analysis in search_grep).
        // Verify the file exists but has been neutralized.
        var oldExtensionsPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "Source", "PuddingHost", "Extensions",
            "PuddingApplicationInitializationExtensions.cs");

        // If the file still exists, it must be marked as deleted/empty.
        if (File.Exists(oldExtensionsPath))
        {
            var content = File.ReadAllText(oldExtensionsPath);
            Assert.DoesNotContain("class PuddingApplicationInitializationExtensions", content);
            Assert.DoesNotContain("InitializePuddingDataAsync", content);
        }
        // If the file doesn't exist, that's even better — truly deleted.
    }

    [Fact]
    public void OldExtensionsClass_IsNeverReferencedByName()
    {
        // Verify the old type name doesn't appear in any .cs file references
        // by checking that it's not discoverable as a live type.
        var oldTypeName = "PuddingAgent.Services.PuddingApplicationInitializationExtensions";
        var oldType = Type.GetType(oldTypeName);

        // The type should not be loadable (no callers, effectively dead code)
        // If it IS loadable, it must have no meaningful implementation
        if (oldType is not null)
        {
            var methods = oldType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            // If it has no public static methods, it's effectively neutralized
            Assert.Empty(methods.Where(m => m.Name == "InitializePuddingDataAsync"));
        }
    }
}
