using System.Text.RegularExpressions;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 能力「声明 ⇒ 分发」覆盖守卫（B4 教训固化）：
/// ① 目录里登记的每个能力都必须有 <c>DesktopService</c> 的 <c>case DesktopCapability.X:</c> 分发臂；
/// ② **不得出现相邻 <c>case</c> 共用分支体**——本阶段曾因 <c>BrowserScreenshot</c> 与
/// <c>BrowserSnapshot</c> 两个 case 相邻且共用同一分支体，导致桌面侧**永远走快照**，
/// 而契约/线缆/表面/工具各层用例都覆盖了，**只有分发级看不出来**，数百轮未被发现。
/// 该缺陷的形态是机械可检的，因此固化为门禁，而不是靠人记得。
/// </summary>
public sealed partial class CapabilityDispatchCoverageTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoLayout.Root }.Concat(parts).ToArray()));

    private static string Catalog => Read("Source", "Pudding.Contracts", "Capabilities.cs");

    private static string Service => Read("Source", "Pudding.DesktopService", "DesktopService.cs");

    [Fact]
    public void EveryCatalogCapability_HasADispatchArm()
    {
        var entries = CatalogEntry().Matches(Catalog);

        // 目录解析必须真的取到条目：格式一变就红，而不是"零条目 ⇒ 全过"。
        Assert.NotEmpty(entries);

        var missing = new List<string>();
        foreach (Match entry in entries)
        {
            var arm = new Regex(@"case\s+DesktopCapability\." + entry.Groups[1].Value + @"\s*:");
            if (!arm.IsMatch(Service))
            {
                missing.Add(entry.Groups[2].Value);
            }
        }

        Assert.True(
            missing.Count == 0,
            "以下目录能力在 DesktopService 中缺少分发臂：" + string.Join(", ", missing));
    }

    [Fact]
    public void NoDispatchArm_IsImmediatelyFollowedByAnotherCaseLabel()
    {
        var lines = Service.Split('\n');
        var shared = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var label = CaseLabel().Match(lines[i].TrimEnd('\r'));
            if (!label.Success)
            {
                continue;
            }

            var j = i + 1;
            while (j < lines.Length && lines[j].Trim().Length == 0)
            {
                j++;
            }

            if (j < lines.Length && CaseLabel().IsMatch(lines[j].TrimEnd('\r')))
            {
                shared.Add(label.Groups[1].Value);
            }
        }

        Assert.True(
            shared.Count == 0,
            "以下能力的分发臂与相邻 case 共用分支体（请求会走错分支）：" + string.Join(", ", shared));
    }

    [GeneratedRegex(@"new\(DesktopCapability\.([A-Za-z]+),\s*""([^""]+)""")]
    private static partial Regex CatalogEntry();

    [GeneratedRegex(@"^\s*case\s+DesktopCapability\.([A-Za-z]+)\s*:")]
    private static partial Regex CaseLabel();
}
