using System.Text.RegularExpressions;
using Pudding.Contracts.Desktop;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// **权威状态机的公开入口必须与对账表一一对应**：
/// 入口是"谁能改变/读取授权状态"的全部面，新增一个入口就必须在
/// <c>Docs/14_reports/2026-10-09-Authority入口对账.md</c> 里补一行（写明语义与验证）。
/// 没有这条门禁时，公开面与对账表会各自漂移，最后没人知道哪个入口还没被审过。
/// </summary>
[TestClass]
public sealed class AuthorityEntryAuditTests
{
    [TestMethod]
    public void EveryAuthorityEntryPointIsListedInTheAuditTable()
    {
        var path = Path.Combine(
            RepoRoot(), "Docs", "14_reports", "2026-10-09-Authority入口对账.md");
        Assert.IsTrue(File.Exists(path), $"找不到对账表：{path}");

        // 表行形如：| `Admit` | 读 | ... |
        var documented = File.ReadAllLines(path)
            .Select(line => Regex.Match(line, @"^\|\s*`(?<name>[A-Za-z_][A-Za-z0-9_]*)`\s*\|"))
            .Where(match => match.Success)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in new[] { typeof(IBrowserAutomationAuthority), typeof(IBrowserAutomationControl) })
        {
            foreach (var method in type.GetMethods())
            {
                // 属性在反射里是 get_Xxx 访问器；对账表写的是属性名，所以在这里归一化。
                declared.Add(Normalize(method.Name));
            }

            foreach (var property in type.GetProperties())
            {
                declared.Add(property.Name);
            }
        }

        var missing = declared.Except(documented).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var extra = documented.Except(declared).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.IsEmpty(
            missing,
            "这些公开入口还没进对账表（新增入口必须补一行：语义要点 + 验证方式）："
            + string.Join(", ", missing));
        Assert.IsEmpty(
            extra,
            "对账表里有已不存在的入口（删掉该行或改回正确名字）：" + string.Join(", ", extra));

        // 表里必须写明判定口径，且必须保留"仍缺的边界"这一**诚实小节**（否则表格会退化成"全绿清单"）。
        var text = File.ReadAllText(path);
        StringAssert.Contains(text, "已核对并取红");
        StringAssert.Contains(text, "仍缺的边界");
    }

    private static string Normalize(string memberName) =>
        memberName.StartsWith("get_", StringComparison.Ordinal)
            ? memberName["get_".Length..]
            : memberName;

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }
}
