using System.Text.Json;
using PuddingCode.Configuration;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class AgentSkillFileServiceTests
{
    [TestMethod]
    public async Task InitializeAsync_Creates_Skills_Root_And_Empty_Index()
    {
        using var temp = new TempDataRoot();
        var service = new AgentSkillFileService(temp.Paths);

        var result = await service.InitializeAsync("agent-1");

        Assert.IsTrue(Directory.Exists(result.SkillsRootPath));
        Assert.IsTrue(File.Exists(result.IndexPath));
        Assert.AreEqual(Path.Combine(temp.Paths.AgentInstanceRoot("agent-1"), "skills"), result.SkillsRootPath);
        Assert.AreEqual(Path.Combine(result.SkillsRootPath, "index.json"), result.IndexPath);

        var index = await ReadIndexAsync(result.IndexPath);
        Assert.AreEqual("agent-1", index.AgentInstanceId);
        Assert.AreEqual(0, index.Skills.Count);
    }

    [TestMethod]
    public async Task InitializeAsync_Is_Idempotent_And_Does_Not_Delete_Existing_Skills()
    {
        using var temp = new TempDataRoot();
        var service = new AgentSkillFileService(temp.Paths);

        await service.CreateAsync("agent-1", new AgentSkillCreateRequest
        {
            SkillId = "daily_notes",
            Name = "Daily Notes",
            Description = "Keeps local daily note rules.",
            Summary = "Use this when writing daily notes.",
            SkillMarkdown = "# Daily Notes\n\nKeep notes concise.",
        });

        await service.InitializeAsync("agent-1");

        var skill = await service.GetAsync("agent-1", "daily_notes");
        Assert.AreEqual("Daily Notes", skill.Manifest.Name);
        Assert.IsTrue(File.Exists(Path.Combine(skill.PhysicalPath, "SKILL.md")));

        var index = await service.GetIndexAsync("agent-1");
        Assert.AreEqual(1, index.Skills.Count);
        Assert.AreEqual("daily_notes", index.Skills[0].SkillId);
    }

    [TestMethod]
    public async Task CreateAsync_Writes_Manifest_SkillMarkdown_And_Index()
    {
        using var temp = new TempDataRoot();
        var service = new AgentSkillFileService(temp.Paths);

        var created = await service.CreateAsync("agent-1", new AgentSkillCreateRequest
        {
            SkillId = "coding_rules",
            Name = "Coding Rules",
            Version = "1.2.3",
            Description = "Local coding standards.",
            Summary = "Follow local coding standards before editing.",
            Tags = ["code", "standards"],
            SkillMarkdown = "# Coding Rules\n\nRead before editing code.",
        });

        Assert.AreEqual("coding_rules", created.Manifest.SkillId);
        Assert.AreEqual("1.2.3", created.Manifest.Version);
        Assert.IsTrue(created.Manifest.Enabled);
        Assert.IsFalse(string.IsNullOrWhiteSpace(created.Manifest.ContentHash));
        Assert.IsTrue(File.Exists(Path.Combine(created.PhysicalPath, "manifest.json")));
        Assert.IsTrue(File.Exists(Path.Combine(created.PhysicalPath, "SKILL.md")));

        var markdown = await File.ReadAllTextAsync(Path.Combine(created.PhysicalPath, "SKILL.md"));
        Assert.AreEqual("# Coding Rules\n\nRead before editing code.", markdown);

        var index = await service.GetIndexAsync("agent-1");
        Assert.AreEqual(1, index.Skills.Count);
        Assert.AreEqual("coding_rules", index.Skills[0].SkillId);
        Assert.AreEqual("Follow local coding standards before editing.", index.Skills[0].Summary);
        Assert.AreEqual(created.Manifest.ContentHash, index.Skills[0].ContentHash);
    }

    [TestMethod]
    public async Task CreateAsync_Rejects_Invalid_Ids_And_Path_Traversal()
    {
        using var temp = new TempDataRoot();
        var service = new AgentSkillFileService(temp.Paths);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            service.InitializeAsync("../agent"));

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            service.CreateAsync("agent-1", new AgentSkillCreateRequest
            {
                SkillId = "../escape",
                Name = "Escape",
                SkillMarkdown = "bad",
            }));

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            service.ReadFileAsync("agent-1", "safe_skill", "../secret.txt"));
    }

    [TestMethod]
    public async Task UpdateAsync_Updates_Manifest_Markdown_And_ContentHash()
    {
        using var temp = new TempDataRoot();
        var service = new AgentSkillFileService(temp.Paths);
        var created = await service.CreateAsync("agent-1", new AgentSkillCreateRequest
        {
            SkillId = "review_notes",
            Name = "Review Notes",
            Summary = "Initial summary",
            SkillMarkdown = "initial",
        });

        var updated = await service.UpdateAsync("agent-1", "review_notes", new AgentSkillUpdateRequest
        {
            Name = "Code Review Notes",
            Summary = "Updated summary",
            SkillMarkdown = "# Updated\n\nUse during review.",
        });

        Assert.AreEqual("Code Review Notes", updated.Manifest.Name);
        Assert.AreEqual("Updated summary", updated.Manifest.Summary);
        Assert.AreNotEqual(created.Manifest.ContentHash, updated.Manifest.ContentHash);
        Assert.AreEqual("# Updated\n\nUse during review.", await File.ReadAllTextAsync(Path.Combine(updated.PhysicalPath, "SKILL.md")));

        var index = await service.GetIndexAsync("agent-1");
        Assert.AreEqual("Updated summary", index.Skills.Single().Summary);
        Assert.AreEqual(updated.Manifest.ContentHash, index.Skills.Single().ContentHash);
    }

    [TestMethod]
    public async Task SetEnabledAsync_Updates_Index_Without_Deleting_Files()
    {
        using var temp = new TempDataRoot();
        var service = new AgentSkillFileService(temp.Paths);
        await service.CreateAsync("agent-1", new AgentSkillCreateRequest
        {
            SkillId = "disabled_skill",
            Name = "Disabled Skill",
            SkillMarkdown = "content",
        });

        var disabled = await service.SetEnabledAsync("agent-1", "disabled_skill", enabled: false);

        Assert.IsFalse(disabled.Manifest.Enabled);
        Assert.IsTrue(File.Exists(Path.Combine(disabled.PhysicalPath, "SKILL.md")));

        var index = await service.GetIndexAsync("agent-1");
        Assert.IsFalse(index.Skills.Single().Enabled);
    }

    [TestMethod]
    public async Task DeleteAsync_Removes_Skill_Directory_And_Index_Entry()
    {
        using var temp = new TempDataRoot();
        var service = new AgentSkillFileService(temp.Paths);
        var created = await service.CreateAsync("agent-1", new AgentSkillCreateRequest
        {
            SkillId = "delete_me",
            Name = "Delete Me",
            SkillMarkdown = "content",
        });

        var result = await service.DeleteAsync("agent-1", "delete_me");

        Assert.AreEqual(created.PhysicalPath, result.DeletedPath);
        Assert.IsFalse(Directory.Exists(created.PhysicalPath));
        Assert.AreEqual(0, result.Index.Skills.Count);
    }

    [TestMethod]
    public async Task ListAsync_And_RebuildIndexAsync_Return_Deterministic_Order()
    {
        using var temp = new TempDataRoot();
        var service = new AgentSkillFileService(temp.Paths);
        await service.CreateAsync("agent-1", new AgentSkillCreateRequest
        {
            SkillId = "zeta",
            Name = "Zeta",
            SkillMarkdown = "z",
        });
        await service.CreateAsync("agent-1", new AgentSkillCreateRequest
        {
            SkillId = "alpha",
            Name = "Alpha",
            SkillMarkdown = "a",
        });

        var list = await service.ListAsync("agent-1");
        CollectionAssert.AreEqual(new[] { "alpha", "zeta" }, list.Select(x => x.Manifest.SkillId).ToArray());

        var index = await service.RebuildIndexAsync("agent-1");
        CollectionAssert.AreEqual(new[] { "alpha", "zeta" }, index.Skills.Select(x => x.SkillId).ToArray());
    }

    private static async Task<AgentSkillIndex> ReadIndexAsync(string indexPath)
    {
        await using var stream = File.OpenRead(indexPath);
        return (await JsonSerializer.DeserializeAsync<AgentSkillIndex>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-agent-skill-tests", Guid.NewGuid().ToString("N"));
            Paths = PuddingDataPaths.FromRoot(Root);
        }

        public string Root { get; }
        public PuddingDataPaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
