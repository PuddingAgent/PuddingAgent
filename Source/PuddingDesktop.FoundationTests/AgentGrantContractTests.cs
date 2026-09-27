using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>
/// DS-04 grants: the page must show an instance's deviation from its template rather than a live
/// inheritance link, and must never conflate "no grants" with "unspecified".
/// </summary>
public sealed class AgentGrantContractTests
{
    private static AgentGrantOptions Options() => new(
        [
            new AgentGrantOption("file_read", "Read File", "File", true, "capability"),
            new AgentGrantOption("shell", "Shell", "Runtime", false, "capability")
        ],
        [new AgentGrantOption("coding", "Coding Pack（1.0.0）", "coding.zip", true, "skillPackage")]);

    [Fact]
    public void NormalizeTrimsDropsBlanksAndSortsCaseInsensitively()
    {
        // 去重保留首次出现的大小写，然后按大小写不敏感排序（"A" 在 "b" 之前）。
        Assert.Equal(["A", "b"], AgentGrantText.Normalize([" b ", "", "A", "a", null!]));
        Assert.Empty(AgentGrantText.Normalize(null));
        Assert.Equal(["coding"], AgentGrantText.Normalize(["coding", "CODING"]));
    }

    [Fact]
    public void ComparisonSeparatesWhatOnlyTheTemplateHasFromWhatOnlyTheInstanceHas()
    {
        var template = new AgentGrantSet(["file_read", "shell"], ["coding"]);
        var same = AgentGrantText.Compare(template, new AgentGrantSet(["shell", "file_read"], ["coding"]));
        Assert.True(same.IsIdentical);
        Assert.Equal("与模板授权一致", AgentGrantText.DescribeComparison(template, new AgentGrantSet(["shell", "file_read"], ["coding"])));

        var deviation = AgentGrantText.Compare(template, new AgentGrantSet(["file_read"], ["other-pack"]));
        Assert.False(deviation.IsIdentical);
        Assert.Equal(["coding", "shell"], deviation.OnlyInTemplate);
        Assert.Equal(["other-pack"], deviation.OnlyInInstance);
        var described = AgentGrantText.DescribeComparison(template, new AgentGrantSet(["file_read"], ["other-pack"]));
        Assert.Contains("比模板多 1 项", described, StringComparison.Ordinal);
        Assert.Contains("比模板少 2 项", described, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyMeansNoGrantsAndUnspecifiedMeansKeepTheStoredValue()
    {
        var options = Options();
        Assert.Equal("没有授权（明确不授权）", AgentGrantText.DescribeSet(AgentGrantSet.Empty, options));
        Assert.Contains("能力 2 项", AgentGrantText.DescribeSet(new AgentGrantSet(["file_read", "shell"], ["coding"]), options), StringComparison.Ordinal);

        // An id the catalogue no longer knows is reported instead of being dropped.
        Assert.Contains("已不在可用目录中", AgentGrantText.DescribeSet(new AgentGrantSet(["ghost"], []), options), StringComparison.Ordinal);

        Assert.True(AgentGrantSelection.UnspecifiedSelection.Unspecified);
        Assert.False(AgentGrantSelection.None.Unspecified);
        Assert.Empty(AgentGrantSelection.None.Ids);
        Assert.Equal(["a", "b"], AgentGrantSelection.Of(["b ", "a", "b"]).Ids);
        Assert.Contains("独立快照", AgentGrantText.CreationInheritanceNotice, StringComparison.Ordinal);
        Assert.Contains("不是同一件事", AgentGrantText.EmptyIsNotUnspecified, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsExposeCapabilityAndPackageOriginAndAvailability()
    {
        var options = Options();
        var capability = options.Find("FILE_READ")!;
        Assert.True(capability.IsCapability);
        Assert.False(capability.IsSkillPackage);
        Assert.True(capability.IsAvailable);
        Assert.False(options.Find("shell")!.IsAvailable, "运行时不可执行的能力不能显示为可用");
        Assert.True(options.Find("coding")!.IsSkillPackage);
        Assert.Null(options.Find("missing"));
    }

    [Fact]
    public void ValidationAndSearchCoverTheFieldsThePageShows()
    {
        var options = Options();
        Assert.Empty(AgentGrantText.Validate(new AgentGrantSet(["file_read"], ["coding"]), options));
        var errors = AgentGrantText.Validate(new AgentGrantSet(["ghost"], ["missing-pack"]), options);
        Assert.Equal(2, errors.Count);

        var option = options.Find("file_read")!;
        Assert.True(AgentGrantText.Matches(option, null));
        Assert.True(AgentGrantText.Matches(option, "  "));
        Assert.True(AgentGrantText.Matches(option, "file_"));
        Assert.True(AgentGrantText.Matches(option, "Read File"));
        Assert.False(AgentGrantText.Matches(option, "nothing-matches-this"));
    }
}
