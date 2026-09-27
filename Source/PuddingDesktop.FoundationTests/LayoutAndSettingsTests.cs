using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

public sealed class LayoutAndSettingsTests
{
    [Theory]
    [InlineData(1500, true, true)]
    [InlineData(1000, false, true)]
    [InlineData(750, false, false)]
    [InlineData(400, false, false)]
    public void ResponsiveLayoutPreservesWorkbench(double width, bool navigation, bool workspace)
    {
        var layout = new ShellLayout(); var result = layout.Allocate(width);
        Assert.Equal(navigation, result.NavigationWidth > 0);
        Assert.Equal(workspace, result.WorkspaceWidth > 0);
        Assert.Equal(width, result.NavigationWidth + result.WorkbenchWidth + result.WorkspaceWidth);
        Assert.True(result.WorkbenchWidth >= Math.Min(480, width));
        Assert.True(layout.NavigationVisible); Assert.True(layout.WorkspaceVisible);
    }

    [Fact]
    public void InvalidDimensionsAreNormalized()
    {
        var normalized = new ShellLayout(double.NaN, double.PositiveInfinity).Normalize();
        Assert.Equal(248, normalized.NavigationWidth); Assert.Equal(520, normalized.WorkspaceWidth);
        Assert.Equal(0, normalized.Allocate(-1).WorkbenchWidth);
    }

    [Fact]
    public async Task CorruptSettingsPreservedAndReported_AtomicSaveRoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-winui-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SkeletonSettingsStore(root);
            Assert.Null((await store.LoadAsync()).Warning);
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(store.FilePath, "broken{");
            Assert.NotNull((await store.LoadAsync()).Warning);
            Assert.Equal("broken{", await File.ReadAllTextAsync(store.FilePath));
            await store.SaveAsync(new(new(999, 450, false, true), "Dark", "Acrylic"));
            var loaded = await store.LoadAsync();
            Assert.Null(loaded.Warning); Assert.Equal("Dark", loaded.Settings.Theme);
            Assert.Equal("Acrylic", loaded.Settings.Material);
            Assert.Equal(320, loaded.Settings.Layout.NavigationWidth);
            Assert.False(loaded.Settings.Layout.NavigationVisible);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AssemblyHasNoUiOrHostDependency()
    {
        Assert.All(typeof(ShellState).Assembly.GetReferencedAssemblies(), reference =>
            Assert.True(reference.Name == "netstandard" || reference.Name!.StartsWith("System", StringComparison.Ordinal), reference.FullName));
    }
}
