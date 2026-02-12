using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Services;

namespace PuddingCodeIntelligenceTests.Services;

[TestClass]
public sealed class DefaultCodeWorkspaceResolverTests
{
    [TestMethod]
    public async Task ResolveWorkspaceAsync_Discovers_CSharp_Workspace_Files_And_Ignores_Noise_Directories()
    {
        using var fixture = CodeIntelligenceFixture.Create();
        var projectDirectory = Path.Combine(fixture.Root, "external-source");
        var appDirectory = Path.Combine(projectDirectory, "src", "App");
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(Path.Combine(projectDirectory, "obj"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "node_modules", "ignored"));
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "Legacy.sln"), "\r\n");
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "Preferred.slnx"), "<Solution />");
        await File.WriteAllTextAsync(Path.Combine(appDirectory, "App.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "obj", "Ignored.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "node_modules", "ignored", "Ignored.csproj"), "<Project />");

        await fixture.Store.UpsertProjectAsync(Project("workspace-one", "project-one", projectDirectory));
        var resolver = new DefaultCodeWorkspaceResolver(fixture.Store);

        var descriptor = await resolver.ResolveWorkspaceAsync("workspace-one", "project-one");
        var byPath = await resolver.ResolveWorkspacesByProjectPathAsync(
            "workspace-one",
            Path.Combine(projectDirectory, "."));

        Assert.IsNotNull(descriptor);
        Assert.IsTrue(descriptor!.IsLoaded);
        Assert.AreEqual(Path.Combine(projectDirectory, "Preferred.slnx"), descriptor.SolutionPath);
        Assert.IsNotNull(descriptor.ProjectFilePaths);
        Assert.HasCount(1, descriptor.ProjectFilePaths!);
        Assert.AreEqual(Path.Combine(appDirectory, "App.csproj"), descriptor.ProjectFilePaths![0]);
        Assert.HasCount(1, byPath);
        Assert.AreEqual("project-one", byPath[0].ProjectId);
    }

    [TestMethod]
    public async Task ResolveWorkspaceAsync_Returns_Unloaded_Descriptor_When_Source_Directory_Is_Missing()
    {
        using var fixture = CodeIntelligenceFixture.Create();
        await fixture.Store.UpsertProjectAsync(Project(
            "workspace-one",
            "project-one",
            Path.Combine(fixture.Root, "missing-source")));
        var resolver = new DefaultCodeWorkspaceResolver(fixture.Store);

        var descriptor = await resolver.ResolveWorkspaceAsync("workspace-one", "project-one");

        Assert.IsNotNull(descriptor);
        Assert.IsFalse(descriptor!.IsLoaded);
        Assert.IsNull(descriptor.SolutionPath);
        Assert.IsNull(descriptor.ProjectFilePaths);
    }

    [TestMethod]
    public async Task ResolveWorkspaceAsync_Does_Not_Recurse_Into_Directory_Symbolic_Links()
    {
        using var fixture = CodeIntelligenceFixture.Create();
        var projectDirectory = Path.Combine(fixture.Root, "source");
        var linkedDirectory = Path.Combine(projectDirectory, "linked-source");
        var linkedTargetDirectory = Path.Combine(fixture.Root, "linked-target");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(linkedTargetDirectory);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "Root.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(linkedTargetDirectory, "ShouldBeIgnored.csproj"), "<Project />");

        if (!TryCreateDirectorySymlink(linkedDirectory, linkedTargetDirectory))
            return;

        await fixture.Store.UpsertProjectAsync(Project("workspace-one", "project-one", projectDirectory));
        var resolver = new DefaultCodeWorkspaceResolver(fixture.Store);

        var descriptor = await resolver.ResolveWorkspaceAsync("workspace-one", "project-one");

        Assert.IsNotNull(descriptor);
        Assert.IsNotNull(descriptor!.ProjectFilePaths);
        Assert.HasCount(1, descriptor.ProjectFilePaths!);
        Assert.AreEqual(Path.Combine(projectDirectory, "Root.csproj"), descriptor.ProjectFilePaths![0]);
    }

    private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static CodeProjectRecord Project(string workspaceId, string projectId, string projectPath) =>
        new(
            workspaceId,
            projectId,
            projectPath,
            CodeProjectStatus.Active,
            DisplayName: projectId,
            AddedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
}
