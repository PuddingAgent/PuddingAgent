using System.Globalization;
using System.Text;

namespace Pudding.DesktopService;

/// <summary>
/// **把交付清单渲染成交付报告（markdown + 标准 Front Matter）**。
///
/// 存在的理由：清单本身只写在 <c>temp/</c>（会被清理），而交付证据需要留痕。
/// 手工誊写必然漏项（round-63 的报告就是手写的）——因此把渲染做成纯函数，
/// 由 CLI 的 <c>--report</c> 直接落盘，并且**逐字带上清单里的未验证项**（不留"摘要时被删掉"的口子）。
/// </summary>
public static class DeliveryManifestReport
{
    /// <summary>渲染报告。<paramref name="slug"/> 用于 Front Matter（文档站内标识）。</summary>
    public static string Render(
        DeliveryManifest manifest,
        string title,
        DateOnly date,
        string slug,
        string relatedDesignDoc = "Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md")
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var passed = manifest.Checks.Sum(check => check.Passed);
        var failed = manifest.Checks.Sum(check => check.Failed);

        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append($"title: {title}\n");
        builder.Append("author: hyfree\n");
        builder.Append($"date: {iso}\n");
        builder.Append($"last_reviewed: {iso}\n");
        builder.Append("description: \"由 Pudding.DeliveryManifest.Cli 自动渲染的交付清单：")
            .Append($"{manifest.Checks.Count} 个套件 {passed} 通过 / {failed} 失败、{manifest.Artifacts.Count} 个产物、")
            .Append(manifest.KnownUnverifiedIsTruncated
                ? $"{manifest.KnownUnverified?.Count ?? 0} 条内部证明不了的结论（**共 {manifest.KnownUnverifiedTotal} 条，清单只展示前 {manifest.KnownUnverified?.Count ?? 0} 条**）"
                : $"{manifest.KnownUnverified?.Count ?? 0} 条内部证明不了的结论")
            .Append($"{manifest.KnownUnverified?.Count ?? 0} 条内部证明不了的结论；阶段 {DesktopAcceptance.NameOf(manifest.Stage)}。\"\n");
        builder.Append("status: active\n");
        builder.Append("categories: [docs, reports]\n");
        builder.Append("tags: [desktop, acceptance, delivery-manifest, b7]\n");
        builder.Append($"related_docs: [{relatedDesignDoc}]\n");
        builder.Append("related_files: [Source/Pudding.DeliveryManifest.Cli/Program.cs, TestScripts/delivery-manifest.ps1]\n");
        builder.Append($"slug: {slug}\n");
        builder.Append("draft: false\n");
        builder.Append("---\n\n");

        builder.Append($"# {title}\n\n");
        builder.Append("## 这是什么\n\n")
            .Append("由 `TestScripts/delivery-manifest.ps1` 跑套件生成 TRX，再由 `Pudding.DeliveryManifest.Cli` ")
            .Append("汇总产出并**自检**的交付清单（本文件由该 CLI 自动渲染，不是手工誊写）。\n\n")
            .Append($"**阶段：`{DesktopAcceptance.NameOf(manifest.Stage)}`** —— 内部一侧能达到的上限：")
            .Append("进程内的 Agent 不能证明新代码已被加载，也不能独立判定自身生命周期（见 `Agents.md`「验证边界」）。\n\n");

        builder.Append("## 事实\n\n");
        builder.Append("| 项 | 值 |\n|---|---|\n");
        builder.Append($"| 构建标识 | `{manifest.BuildId}` |\n");
        builder.Append($"| 提交 | `{manifest.Commit}` |\n");
        builder.Append($"| 套件 | {manifest.Checks.Count} 个 |\n");
        builder.Append($"| 通过 / 失败 | **{passed} / {failed}** |\n");
        builder.Append($"| 产物 | {manifest.Artifacts.Count} 个 |\n");
        builder.Append(manifest.KnownUnverifiedIsTruncated
            ? $"| 未验证项 | **显示 {manifest.KnownUnverified?.Count ?? 0} 条 / 共 {manifest.KnownUnverifiedTotal} 条（已截断）** |\n"
            : $"| 未验证项 | {manifest.KnownUnverified?.Count ?? 0} 条（全部列出） |\n");
        builder.Append($"| 外部必做步骤 | {string.Join("、", manifest.RequiredExternalSteps.Select(step => $"`{step}`"))} |\n\n");

        builder.Append("### 检查（来自 TRX，非人工填写）\n\n");
        builder.Append("| 套件 | 通过 | 失败 |\n|---|---|---|\n");
        foreach (var check in manifest.Checks)
        {
            builder.Append($"| {check.Name} | {check.Passed} | {check.Failed} |\n");
        }

        builder.Append("\n### 产物\n\n");
        foreach (var artifact in manifest.Artifacts)
        {
            builder.Append($"- `{artifact}`\n");
        }

        builder.Append("\n## 内部证明不了的结论\n\n");
        if (manifest.KnownUnverified is { Count: > 0 } unverified)
        {
            builder.Append("以下条目由 `KnownUnverifiedExtractor` **从修改日志自动提取**（不靠人回忆）：\n\n");
            foreach (var item in unverified)
            {
                builder.Append($"- {item}\n");
            }
        }
        else
        {
            builder.Append("清单已**显式**声明：没有已知的未验证项。\n");
        }

        builder.Append("\n## 外部控制器需要做的事\n\n");
        var ordered = manifest.RequiredExternalSteps.ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            builder.Append($"{index + 1}. `{ordered[index]}`");
            builder.Append(ordered[index] switch
            {
                "startup" => " —— 部署到上述提交对应的**明确新构建**并启动，确认加载的是该构建（而不是被单实例转发到旧进程）\n",
                "restart" => " —— 重启并确认新进程仍是新构建\n",
                "crash-recovery" => " —— 制造崩溃并确认自动恢复\n",
                "exit-reclamation" => " —— 退出并确认子进程与资源被回收\n",
                _ => "\n",
            });
        }

        builder.Append("\n内部一侧的 `DesktopAcceptance` 会**拒绝**内部主体作出生命周期结论。\n\n");
        builder.Append("## 关联\n\n")
            .Append($"- 设计：[浏览器自动化可靠性与渐进阅读设计方案]({RelativeToReports(relatedDesignDoc)})\n")
            .Append("- 规程：`Agents.md`「验证边界（两段式验收，强制）」\n");

        return builder.ToString();
    }

    /// <summary>把仓库相对路径改写成相对 <c>Docs/14_reports/</c> 的形式（报告固定落在这里）。</summary>
    private static string RelativeToReports(string repositoryRelativePath) =>
        repositoryRelativePath.StartsWith("Docs/", StringComparison.Ordinal)
            // Docs/14_reports/ → 上一级是 Docs/，因此只需一个 "../"（两个会跳出 Docs 变成死链）。
            ? "../" + repositoryRelativePath["Docs/".Length..]
            : repositoryRelativePath;
}
