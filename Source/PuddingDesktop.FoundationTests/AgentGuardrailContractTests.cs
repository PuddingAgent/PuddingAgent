using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-04 guardrail slice: budget validation and honest duration/description rendering.</summary>
public sealed class AgentGuardrailContractTests
{
    [Fact]
    public void DefaultsMatchCoresOwnDefaults()
    {
        Assert.Equal(200, AgentGuardrailPolicy.Default.MaxRounds);
        Assert.Equal(86400, AgentGuardrailPolicy.Default.MaxElapsedSeconds);
        // AgentTemplateFileService 没有提交值时写入 400；DTO 上的 100 只是过时字面量。
        Assert.Equal(400, AgentGuardrailPolicy.Default.MaxToolCallsTotal);
        Assert.False(AgentGuardrailPolicy.Default.HasContainerImage);
    }

    [Fact]
    public void OnlyValuesNoBudgetCouldHonourAreRejected()
    {
        Assert.Empty(AgentGuardrailText.Validate(AgentGuardrailPolicy.Default));
        // Large budgets are legitimate: Core applies no ceiling, so neither does the form.
        Assert.Empty(AgentGuardrailText.Validate(new AgentGuardrailPolicy(1_000_000, 31_536_000, 10_000_000, "")));

        Assert.Contains("轮次", AgentGuardrailText.Validate(AgentGuardrailPolicy.Default with { MaxRounds = 0 }).Single(), StringComparison.Ordinal);
        Assert.Contains("轮次", AgentGuardrailText.Validate(AgentGuardrailPolicy.Default with { MaxRounds = -1 }).Single(), StringComparison.Ordinal);
        Assert.Contains("时长", AgentGuardrailText.Validate(AgentGuardrailPolicy.Default with { MaxElapsedSeconds = 0 }).Single(), StringComparison.Ordinal);
        Assert.Contains("工具调用", AgentGuardrailText.Validate(AgentGuardrailPolicy.Default with { MaxToolCallsTotal = 0 }).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void ContainerImageIsOptionalButNeverSilentlyRewritten()
    {
        Assert.Equal("", AgentGuardrailText.NormalizeContainerImage(null));
        Assert.Equal("", AgentGuardrailText.NormalizeContainerImage("   "));
        Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", AgentGuardrailText.NormalizeContainerImage("  mcr.microsoft.com/dotnet/sdk:10.0 "));
        Assert.Empty(AgentGuardrailText.Validate(new AgentGuardrailPolicy(200, 86400, 100, "alpine")));
        Assert.Contains("空白", AgentGuardrailText.Validate(new AgentGuardrailPolicy(200, 86400, 100, "al pine")).Single(), StringComparison.Ordinal);
        Assert.Contains("容器镜像", AgentGuardrailText.Validate(
            new AgentGuardrailPolicy(200, 86400, 100, new string('x', 513))).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeRendersDurationsInReadableUnits()
    {
        Assert.Contains("轮次 200", AgentGuardrailPolicy.Default.Describe(), StringComparison.Ordinal);
        Assert.Contains("1 天", AgentGuardrailPolicy.Default.Describe(), StringComparison.Ordinal);
        Assert.Contains("无容器镜像覆盖", AgentGuardrailPolicy.Default.Describe(), StringComparison.Ordinal);
        Assert.Contains("alpine", new AgentGuardrailPolicy(1, 30, 1, "alpine").Describe(), StringComparison.Ordinal);
        Assert.Contains("30 秒", new AgentGuardrailPolicy(1, 30, 1, "").Describe(), StringComparison.Ordinal);
        Assert.Contains("5 分钟", new AgentGuardrailPolicy(1, 300, 1, "").Describe(), StringComparison.Ordinal);
        Assert.Contains("2 小时", new AgentGuardrailPolicy(1, 7200, 1, "").Describe(), StringComparison.Ordinal);
    }
}
