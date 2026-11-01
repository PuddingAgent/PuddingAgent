namespace PuddingToolGuidance.Rules;

/// <summary>
/// 默认规则集：按「治什么」分组组合。
/// <para>
/// 分开定义的意义：新加一组规则只动一个文件，不必回头改 <see cref="ShellDialectRules"/>
/// 这种以方言命名的类 —— 名字与内容对不上，是这类清单最常见的腐烂方式。
/// </para>
/// </summary>
public static class DefaultRules
{
    public static IReadOnlyList<IGuidanceRule> All { get; } =
    [
        // ① 方言误用：cmd 里的 Unix 命令 / pwsh cmdlet / Bash 变量 / 嵌套 shell / & 串行
        .. ShellDialectRules.All,
        // ② 构建与测试效率：重复测试未跳过构建、连续超时未用挂起检测
        .. BuildEfficiencyRules.All,
        // ③ Git 安全：add -A 收进临时件、stash 与恢复同链
        .. GitSafetyRules.All,
        // ④ 本机工具链别名：python / python3 是商店占位符
        .. HostToolingRules.All
    ];
}
