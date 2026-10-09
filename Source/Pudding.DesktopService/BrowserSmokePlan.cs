using PuddingBrowser.Automation;

namespace Pudding.DesktopService;

/// <summary>
/// 一条**外部 smoke 观察项**：谁（Gate）、在哪儿看（Area）、怎么触发（Action）、看到什么才算过（Expected）。
///
/// 存在的理由：本会话里"真机未验证"的结论散落在二十多条 changelog 里，交接时无法执行。
/// 收拢成清单后，外部控制器可以逐条做、逐条勾。
/// </summary>
public sealed record BrowserSmokeCheck(
    string Id,
    string Gate,
    string Area,
    string Action,
    string Expected,
    bool RequiresRealDevice = true,
    string? AutomatedCoverage = null);

/// <summary>
/// **浏览器自动化的外部 smoke 清单**（设计方案的门禁 G1–G11 → 可执行观察项）。
///
/// 两条硬规则：
/// <list type="number">
///   <item><b>覆盖全部门禁</b>：任何 G1–G11 缺失都算清单不完整（避免"没测的项静默消失"）；</item>
///   <item><b>不许把"进程内能测的"混进来冒充真机</b>：每条要么 <c>RequiresRealDevice=true</c>（真机观察），
///         要么明确标出已被自动化测试覆盖（<c>AutomatedCoverage</c> = 测试名）。</item>
/// </list>
/// </summary>
public static class BrowserSmokePlan
{
    /// <summary>设计文档里的门禁编号（清单必须全覆盖）。</summary>
    public static readonly IReadOnlyList<string> RequiredGates =
        ["G1", "G2", "G3", "G4", "G5", "G6", "G7", "G8", "G9", "G10", "G11"];

    /// <summary>清单本体（每条都是**真机观察**：本环境证明不了的结论）。</summary>
    public static IReadOnlyList<BrowserSmokeCheck> Checks { get; } =
    [
        new("S-01", "G1", "控制权", "在测试页上让用户接管，然后尝试一次写入（click/fill）",
            "驱动调用数 = 0；只读观察仍可用；恢复后旧写租约不复活",
            AutomatedCoverage: "AutomationTests.BrowserControlAuthority*"),
        new("S-02", "G2", "页面授权",
            "对同 context 的非目标页发起写入；跨 frame/跨身份再试一次；撤销 grant 后重试；"
            + "再由父代理签发的子代理尝试扩大授权；最后建页并关闭",
            "全部拒绝（帧/身份不匹配也拒绝）；撤销后驱动调用数 = 0；子代理不得扩大父授权；"
            + "建关页后无残留（标签项/订阅/注册项）",
            // 帧/身份/撤销/子代理授权都已有进程内用例；"无残留"只能真机看（UI/运行时/注册表）。
            AutomatedCoverage: "AutomationTests.BrowserPageGrant* / ReadOnlyDelegationTests"),
        new("S-03", "G3", "回执", "click 一个纯 SPA 菜单项（不导航、不推进版本）",
            "不被判失败；回执的 execution/side-effects 与事实一致"),
        new("S-04", "G4", "元素引用", "同一节点连续 20 次只读 + 一次 fill + 普通滚动",
            "无需重复定位；读操作不推进 PageVersion；文档导航后旧 ref 被拒且不会点到同名替代节点",
            AutomatedCoverage: "AutomationTests.BrowserNodeIdentity*"),
        new("S-05", "G5", "阅读预算", "中文与英文 SERP fixture 各一次 extract",
            "一次调用返回前 10 条标题；UTF-8 序列化 ≤ 1536 字节；无额外 locate；"
            + "**另记录进入模型后的文本 token 与调用次数**（文本与视觉分别记，压缩字节 ≠ token 节省）"),
        new("S-06", "G6", "正文与分页", "含标题/列表/表格/代码/链接的结构化 fixture；再跨 TTL 取 cursor",
            "聚合内容覆盖 100%、不重不漏；超预算仍是合法输出；过期 cursor 明确 cursor_expired；"
            + "**全文模式必须显式选择**（默认不是全文）"),
        new("S-07", "G7", "定位与输入", "十种 locator 矩阵（含 exact/重复/隐藏）；受控输入重渲染；密码字段",
            "受控值一致、密码不回显；0×0/禁用/遮挡被拒；一次 fill 回执即可确认非敏感值（**无二次 snapshot**）"),
        new("S-08", "G8", "滚动", "window 与嵌套容器的非边界滚动 + 滚到边界 + 无限滚动页",
            "非边界有真实位移；边界是 no-op（不假称成功）；to_top/to_bottom/to_ref 能停且回报停止原因；"
            + "无限滚动等新条目后 contentChanged=true"),
        new("S-09", "G9", "视觉与坐标", "整页/视口截图；导航过程中再次截图；缩放后使用旧坐标",
            "截图与目标一致、导航中不返回旧页；"
            + $"**detail=low 基准 ≤{BrowserScreenshotTransfer.LowDetailLongestEdge}px/"
            + $"{BrowserScreenshotTransfer.LowDetailBaselineBytes / 1024} KiB（超预算明确 budget_exceeded 或要求局部截图，"
            + "不无限压缩后仍称可读）；单张输入 ≤"
            + $"{BrowserScreenshotTransfer.MaxImageBytes / (1024 * 1024)} MiB，超限明确说明原因**"
            + "（不静默截断/降级）；元数据含源/输出尺寸、设备缩放、CSS viewport、scroll offset、裁剪变换与三代修订号；"
            + "缩放后旧坐标被拒；**实际模型收到类型化图片**（不是 base64 长串；文本模型得到明确限制）；"
            + "**窗口隐藏（托盘）后再截一次**：必须给出可判定结果（可用，或明确报不可用），"
            + "**不得**返回黑图/旧帧冒充成功（§12 要求先核验「截图对隐藏表面的支持」，未核验前不许默认宣告可用）",
            // 采集本身要真机（CapturePreviewAsync），但**载荷/变换/拒绝语义**已由进程内用例覆盖 —— 两件事分开说清。
            AutomatedCoverage: "AutomationTests.BrowserScreenshotCoordinate* / BrowserScreenshotTransferTests / BrowserScreenshotPlannerTests"),
        new("S-10", "G10", "委派", "父代理持有页面写租约时委派子代理写同一页；再取消/接管/超时",
            "父子写租约不并存；子代理**真正**发现并调用 browser_*；父结果 ≤2 KiB 且不含整页；"
            + "取消/接管/超时都回收许可",
            AutomatedCoverage: "AutomationTests.SubAgentDelegation*"),
        new("S-11", "G11", "生命周期", "启停新通道；迟到 CoreReady；断连；桌面隐藏/恢复；跨标签；重启/崩溃/退出",
            "订阅不增长；旧会话不复活；断连不双执行；重启/崩溃/退出有外部证据",
            AutomatedCoverage: null),
        new("S-12", "G7", "输入路径·文件选择器",
            "在含 `input[type=file]` 的页面上：① 用工具上传一个真实文件；② 让页面脚本自行触发文件选择框",
            "① 上传按 DOM.setFileInputFiles 真实完成（元素收到文件）；"
            + "② **记录为不支持**——WebView2 SDK 无文件选择器 API，我们不接管页面自弹的选择框，"
            + "也不假装接管（见驱动里的缺口说明）",
            AutomatedCoverage: "AutomationTests.FileUploadPlanTests"),
        new("S-13", "G3", "页面事件接管与上报",
            "同一会话内依次触发：脚本对话框（alert/confirm/prompt）、权限请求、新窗口、下载",
            "流程**不被卡住**（对话框被应答、权限被拒、新窗口被抑制、下载被取消）；"
            + $"快照的 `pageEvents` 如实列出这些事实——**有界**（最多保留最近 {PageEventLog.DefaultCapacity} 条，"
            + "按条数截断而非截断文本）、连续重复**合并为 (xN)**、总次数另计；"
            + "且**不用于继续交互**（它是事实说明，不是新的定位依据）",
            AutomatedCoverage: "AutomationTests.ScriptDialogPolicyTests / PageEventLogTests"),
    ];

    /// <summary>校验清单：覆盖全部门禁、Id 唯一、字段非空、"真机项 vs 自动化覆盖"必须二选一。</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<BrowserSmokeCheck>? checks)
    {
        if (checks is not { Count: > 0 })
        {
            return ["a smoke plan must contain at least one check"];
        }

        var problems = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var coveredGates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var check in checks)
        {
            if (!seenIds.Add(check.Id))
            {
                problems.Add($"duplicate smoke check id '{check.Id}'");
            }

            if (string.IsNullOrWhiteSpace(check.Id)
                || string.IsNullOrWhiteSpace(check.Gate)
                || string.IsNullOrWhiteSpace(check.Area)
                || string.IsNullOrWhiteSpace(check.Action)
                || string.IsNullOrWhiteSpace(check.Expected))
            {
                problems.Add($"smoke check '{check.Id}' must state gate, area, action and expected outcome");
            }

            coveredGates.Add(check.Gate);

            // 每条要么是真机观察，要么明确说清"已被哪个自动化测试覆盖"——不许两头都不占。
            if (!check.RequiresRealDevice && string.IsNullOrWhiteSpace(check.AutomatedCoverage))
            {
                problems.Add(
                    $"smoke check '{check.Id}' claims it needs no real device but names no automated coverage");
            }

            if (check.RequiresRealDevice && !string.IsNullOrWhiteSpace(check.AutomatedCoverage))
            {
                // 允许"真机为主 + 有部分自动化"，但必须显式：RequiresRealDevice 仍为 true（默认）即表示
                // 结论仍只能由真机给出。这里是刻意保留的：
                // 例如 G10 的租约不变量已有自动化，但"子代理真的发现了工具"只有真机能证明。
            }
        }

        var missing = RequiredGates.Where(gate => !coveredGates.Contains(gate)).ToArray();
        if (missing.Length > 0)
        {
            problems.Add("the smoke plan does not cover every gate: " + string.Join(", ", missing));
        }

        return problems;
    }

    /// <summary>渲染成 markdown（交给外部控制器逐条执行与勾选）。</summary>
    public static string ToMarkdown(IReadOnlyList<BrowserSmokeCheck>? checks = null)
    {
        var plan = checks ?? Checks;

        var builder = new System.Text.StringBuilder();
        builder.Append("| 项 | 门禁 | 区域 | 怎么触发 | 看到什么才算过 |\n|---|---|---|---|---|\n");

        foreach (var check in plan)
        {
            builder.Append($"| {check.Id} | {check.Gate} | {check.Area} | {check.Action} | {check.Expected} |\n");
        }

        var automated = plan.Where(check => !string.IsNullOrWhiteSpace(check.AutomatedCoverage)).ToArray();
        if (automated.Length > 0)
        {
            builder.Append("\n**已有自动化覆盖的部分**（仍需真机确认的部分见上表）：\n\n");
            foreach (var check in automated)
            {
                builder.Append($"- {check.Id}：`{check.AutomatedCoverage}`\n");
            }
        }

        return builder.ToString();
    }
}
