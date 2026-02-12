using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Services;
using PuddingCodeIntelligence.Storage;

namespace PuddingCodeIntelligenceTests.Services;

[TestClass]
public sealed class CodeProjectRegistryTests
{
    [TestMethod]
    public async Task AddProjectAsync_Generates_Stable_Project_Id_For_Directory_Outside_Workspace()
    {
        using var fixture = CodeIntelligenceFixture.Create();
        var workspaceDirectory = Path.Combine(fixture.Root, "agent-workspace");
        var externalProjectDirectory = Path.Combine(fixture.Root, "external-source");
        Directory.CreateDirectory(workspaceDirectory);
        Directory.CreateDirectory(externalProjectDirectory);
        await File.WriteAllTextAsync(Path.Combine(externalProjectDirectory, "ExternalSolution.slnx"), "<Solution />");

        var registry = fixture.CreateRegistry();
        var first = await registry.AddProjectAsync(new CodeProjectAddRequest(
            WorkspaceId: "workspace-one",
            ProjectPath: Path.Combine(externalProjectDirectory, ".")));
        var second = await registry.AddProjectAsync(new CodeProjectAddRequest(
            WorkspaceId: "workspace-one",
            ProjectPath: externalProjectDirectory + Path.DirectorySeparatorChar));

        Assert.IsTrue(first.Success);
        Assert.IsTrue(second.Success);
        Assert.IsFalse(string.IsNullOrWhiteSpace(first.ProjectId));
        Assert.AreEqual(first.ProjectId, second.ProjectId);

        var stored = await fixture.Store.GetProjectAsync("workspace-one", first.ProjectId!);
        Assert.IsNotNull(stored);
        Assert.AreEqual(CodeProjectStatus.Active, stored!.Status);
        Assert.AreEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(externalProjectDirectory)), stored.ProjectPath);
        Assert.AreEqual("ExternalSolution", stored.DisplayName);
    }

    [TestMethod]
    public async Task RemoveProjectAsync_Removes_Index_Data_Without_Deleting_Source_Directory()
    {
        using var fixture = CodeIntelligenceFixture.Create();
        var projectDirectory = Path.Combine(fixture.Root, "registered-source");
        Directory.CreateDirectory(projectDirectory);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "Program.cs"), "class Program { }\r\n");
        var registry = fixture.CreateRegistry();

        var addResult = await registry.AddProjectAsync(new CodeProjectAddRequest(
            WorkspaceId: "workspace-one",
            ProjectPath: projectDirectory,
            ProjectId: "project-one"));
        await fixture.Store.UpsertFilesAsync("workspace-one", "project-one", [
            new CodeFileRecord("workspace-one", "project-one", "Program.cs", "CSharp")
        ]);

        var removeResult = await registry.RemoveProjectAsync(new CodeProjectRemoveRequest(
            WorkspaceId: "workspace-one",
            ProjectId: "project-one"));

        Assert.IsTrue(addResult.Success);
        Assert.IsTrue(removeResult.Success);
        Assert.IsTrue(Directory.Exists(projectDirectory));
        Assert.IsEmpty(await fixture.Store.ListProjectsAsync("workspace-one"));
        Assert.IsNull(await fixture.Store.GetProjectAsync("workspace-one", "project-one"));
    }

    [TestMethod]
    public async Task AddProjectAsync_Returns_Failure_For_Missing_Project_Directory()
    {
        using var fixture = CodeIntelligenceFixture.Create();
        var registry = fixture.CreateRegistry();

        var result = await registry.AddProjectAsync(new CodeProjectAddRequest(
            WorkspaceId: "workspace-one",
            ProjectPath: Path.Combine(fixture.Root, "missing")));

        Assert.IsFalse(result.Success);
        Assert.AreEqual(CodeIndexStatus.Failed, result.Status);
        Assert.IsTrue(result.Message!.Contains("does not exist", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AddProjectAsync_Returns_Failure_For_Invalid_Project_Path()
    {
        using var fixture = CodeIntelligenceFixture.Create();
        var registry = fixture.CreateRegistry();

        var result = await registry.AddProjectAsync(new CodeProjectAddRequest(
            WorkspaceId: "workspace-one",
            ProjectPath: "invalid\0path"));

        Assert.IsFalse(result.Success);
        Assert.AreEqual(CodeIndexStatus.Failed, result.Status);
        Assert.IsTrue(result.Message!.Contains("ProjectPath is invalid", StringComparison.Ordinal));
    }
}
