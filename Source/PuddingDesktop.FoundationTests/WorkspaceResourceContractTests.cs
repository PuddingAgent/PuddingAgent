using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-05 resource cards: vocabulary, JSON pre-checks and the empty-id-means-create rule.</summary>
public sealed class WorkspaceResourceContractTests
{
    [Fact]
    public void VocabulariesMatchTheCoreService()
    {
        Assert.Equal(["VectorStore", "Graph", "FileIndex"], WorkspaceResourceText.KnowledgeBaseTypes);
        Assert.Equal(["MCP", "BuiltIn", "CustomScript", "HttpTool"], WorkspaceResourceText.SkillTypes);
        Assert.Equal(["Draft", "Active", "Paused"], WorkspaceResourceText.WorkflowStatuses);
    }

    [Fact]
    public void UnknownValuesAreShownVerbatimRatherThanRenamed()
    {
        Assert.Contains("向量库", WorkspaceResourceText.DescribeKbType("vectorstore"), StringComparison.Ordinal);
        Assert.Contains("内置", WorkspaceResourceText.DescribeSkillType("builtin"), StringComparison.Ordinal);
        Assert.Contains("已暂停", WorkspaceResourceText.DescribeWorkflowStatus("paused"), StringComparison.Ordinal);
        Assert.Equal("未设置", WorkspaceResourceText.DescribeKbType(null));
        Assert.Equal("SomethingNew", WorkspaceResourceText.DescribeSkillType("SomethingNew"));
        Assert.False(WorkspaceResourceText.IsKnownSkillType("SomethingNew"));
        Assert.True(WorkspaceResourceText.IsKnownWorkflowStatus("draft"));
    }

    [Fact]
    public void KnowledgeBaseFormRejectsWhatCoreRejects()
    {
        var valid = new KnowledgeBaseEdit("team-a-space", "", "Docs", "notes", "VectorStore", true);
        Assert.Empty(WorkspaceResourceText.Validate(valid));
        Assert.Contains("工作区", WorkspaceResourceText.Validate(valid with { WorkspaceId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("名称", WorkspaceResourceText.Validate(valid with { Name = " " }).Single(), StringComparison.Ordinal);
        Assert.Contains("kbType", WorkspaceResourceText.Validate(valid with { KbType = "Sqlite" }).Single(), StringComparison.Ordinal);
        // 空 ID 表示新建，非空表示更新；两种都通过校验。
        Assert.Empty(WorkspaceResourceText.Validate(valid with { KbId = "kb-1" }));
    }

    [Fact]
    public void McpConfigIsPreCheckedForJsonShapeButCoreStaysAuthoritative()
    {
        var mcp = new WorkspaceSkillEdit("team-a-space", "", "Srv", "", "MCP", "{\"command\":\"node\"}", true);
        Assert.Empty(WorkspaceResourceText.Validate(mcp));
        Assert.Contains("不是合法 JSON", WorkspaceResourceText.Validate(mcp with { ConfigJson = "{broken" }).Single(), StringComparison.Ordinal);

        // 非 MCP 技能不预检配置：Core 原样保存，界面不替它下结论。
        var builtIn = mcp with { SkillType = "BuiltIn", ConfigJson = "{broken" };
        Assert.Empty(WorkspaceResourceText.Validate(builtIn));
        // 空配置对 MCP 也不拦：留给 Core 给出真实原因。
        Assert.Empty(WorkspaceResourceText.Validate(mcp with { ConfigJson = "" }));
        Assert.Contains("skillType", WorkspaceResourceText.Validate(mcp with { SkillType = "Shell" }).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowDefinitionMayBeEmptyButMustBeJsonWhenPresent()
    {
        var flow = new WorkspaceWorkflowEdit("team-a-space", "", "Flow", "", "", "Draft", true);
        Assert.Empty(WorkspaceResourceText.Validate(flow));
        Assert.Empty(WorkspaceResourceText.Validate(flow with { DefinitionJson = "{\"steps\":[]}" }));
        Assert.Contains("definitionJson", WorkspaceResourceText.Validate(flow with { DefinitionJson = "nope" }).Single(), StringComparison.Ordinal);
        Assert.Contains("状态", WorkspaceResourceText.Validate(flow with { Status = "Running" }).Single(), StringComparison.Ordinal);
        Assert.Contains("名称", WorkspaceResourceText.Validate(flow with { Name = "" }).Single(), StringComparison.Ordinal);
        Assert.False(WorkspaceResourceText.IsJson("nope"));
        Assert.True(WorkspaceResourceText.IsJson("[]"));
    }

    [Fact]
    public void NoticesStateTheRealBoundaries()
    {
        Assert.Contains("由 Core 统计", WorkspaceResourceText.KnowledgeBaseNotice, StringComparison.Ordinal);
        Assert.Contains("Core 自己的解析器", WorkspaceResourceText.McpConfigNotice, StringComparison.Ordinal);
        Assert.Contains("允许为空", WorkspaceResourceText.WorkflowNotice, StringComparison.Ordinal);

        var skill = new WorkspaceSkillSummary("id", "N", "", "mcp", "{}", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.True(skill.IsMcp, "MCP 判定必须大小写无关");
        Assert.Contains("MCP", skill.TypeText, StringComparison.Ordinal);
        Assert.Equal("已启用", skill.StateText);
        var kb = new KnowledgeBaseSummary("id", "N", "", "Graph", 3, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Equal("已停用", kb.StateText);
        Assert.Contains("图谱", kb.TypeText, StringComparison.Ordinal);
    }
}