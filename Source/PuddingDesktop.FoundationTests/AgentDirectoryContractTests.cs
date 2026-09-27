using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-04 directory slice: form helpers, emoji normalization and the freeze/enable distinction.</summary>
public sealed class AgentDirectoryContractTests
{
    [Fact]
    public void AvatarsAreReferencedByIdBecauseTheTemplateContractHasNoEmojiField()
    {
        // The avatar is a catalog entry, not a free-text emoji: the form must submit an id.
        var avatar = new AgentAvatarOption("pudding", "布丁", "通用助手", true);
        Assert.Equal("pudding", avatar.AvatarId);
        Assert.Empty(AgentDirectoryText.Validate(new AgentTemplateEdit("t", "T", "Service", "", true, 0, avatar.AvatarId)));
        Assert.Empty(AgentDirectoryText.Validate(new AgentTemplateEdit("t", "T", "Service", "", true, 0, "")));
    }

    [Fact]
    public void TemplateFormRejectsWhatCoreRejects()
    {
        var valid = new AgentTemplateEdit("general-assistant", "General", "Service", "", true, 0, "pudding");
        Assert.Empty(AgentDirectoryText.Validate(valid));
        Assert.Contains("模板 ID", AgentDirectoryText.Validate(valid with { TemplateId = "bad id" }).Single(), StringComparison.Ordinal);
        Assert.Contains("模板 ID", AgentDirectoryText.Validate(valid with { TemplateId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("模板名称", AgentDirectoryText.Validate(valid with { Name = " " }).Single(), StringComparison.Ordinal);
        Assert.Contains("角色标识", AgentDirectoryText.Validate(valid with { Role = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("排序", AgentDirectoryText.Validate(valid with { SortOrder = -1 }).Single(), StringComparison.Ordinal);
        Assert.Empty(AgentDirectoryText.Validate(valid with { SortOrder = 0, AvatarId = "" }));
    }

    [Fact]
    public void InstanceFormsRequireWorkspaceNameAndTemplate()
    {
        var create = new AgentInstanceCreate("default", "Builder", "", "general-assistant");
        Assert.Empty(AgentDirectoryText.Validate(create));
        Assert.Contains("工作区", AgentDirectoryText.Validate(create with { WorkspaceId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("角色名称", AgentDirectoryText.Validate(create with { Name = " " }).Single(), StringComparison.Ordinal);
        Assert.Contains("来源模板", AgentDirectoryText.Validate(create with { SourceTemplateId = "" }).Single(), StringComparison.Ordinal);

        var edit = new AgentInstanceEdit("default", "default.builder_1", "Builder", "", "Coding", true, "pudding");
        Assert.Empty(AgentDirectoryText.Validate(edit));
        Assert.Contains("角色实例", AgentDirectoryText.Validate(edit with { AgentId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("角色名称", AgentDirectoryText.Validate(edit with { Name = "" }).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void FreezeAndDisableAreNeverCollapsedIntoOneLabel()
    {
        Assert.Equal("已启用", AgentDirectoryText.DescribeState(true, false));
        Assert.Equal("已冻结（不可执行）", AgentDirectoryText.DescribeState(true, true));
        Assert.Equal("已停用", AgentDirectoryText.DescribeState(false, false));
        Assert.Equal("已停用且已冻结", AgentDirectoryText.DescribeState(false, true));
        Assert.Equal(4, new[] { (true, false), (true, true), (false, false), (false, true) }
            .Select(state => AgentDirectoryText.DescribeState(state.Item1, state.Item2)).Distinct().Count());
    }
}
