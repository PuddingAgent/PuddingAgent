using System.Security.Cryptography;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingDingTalk;
using PuddingRuntime.Services.Tools;

namespace PuddingAgent.Services;

internal sealed class DingTalkTurnTools : IToolInvocationService
{
    private const string Help = "mpcal_public_help";
    private const string Record = "mpcal_record_feedback";
    private readonly PublicQuestion question;
    private readonly string agent;
    private readonly string session;
    private readonly PuddingToolRegistry registry;
    public PuddingToolSchemaService Schemas { get; }
    public CapabilityPolicy Policy { get; } = new() { AllowedToolNames = [Help, Record], DefaultToolNames = [Help, Record] };
    public bool Denied { get; private set; }
    public bool HelpUsed { get; private set; }
    public string? Receipt { get; private set; }

    public DingTalkTurnTools(PublicQuestion question, string agent, string session, string? root, GroupFeedbackInbox inbox)
    {
        this.question = question;
        this.agent = agent;
        this.session = session;
        registry = new PuddingToolRegistry([
            new PublicTool(Help, "查询 MPCAL 经审阅的公开使用说明、报告导入和检查的界面证据，无需参数。", async ct =>
            {
                HelpUsed = true;
                return await EvidenceAsync(root, ct);
            }),
            new PublicTool(Record, "把本条成员消息明确提交的 BUG 或功能需求登记到本机。按原始消息登记，无需参数。", ct =>
            {
                ct.ThrowIfCancellationRequested();
                Receipt = inbox.TryRecord(question, question.MessageId);
                return Task.FromResult(Receipt ?? "未登记。请确认要提交的 BUG 或建议，并用一行文字描述现象或期望。");
            })]);
        Schemas = new(registry);
    }

    public async Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken ct = default)
    {
        var tool = registry.GetTool(request.ToolName);
        var allowed = question.Source.PuddingGroup == "guest" && request.AgentInstanceId == agent &&
            request.SessionId == session && tool is PublicTool;
        if (!allowed) Denied = true;
        // The model has no operation parameters: destination, source and feedback contents are captured by the host.
        var output = allowed ? await ((PublicTool)tool!).Run(ct) : "guest_operation_denied";
        return new() { ToolCallId = request.ToolCallId, ToolName = request.ToolName,
            Success = allowed, Output = allowed ? output : null, Error = allowed ? null : output,
            ExitCode = allowed ? 0 : 1, OutputLength = output.Length };
    }

    private sealed class PublicTool(string name, string description, Func<CancellationToken, Task<string>> run) : IPuddingTool
    {
        public ToolDescriptor Descriptor { get; } = new() { ToolId = name, Name = name, Description = description };
        public Task<string> Run(CancellationToken ct) => run(ct);
        // This catalog is private to a turn. Invocation must first validate its sender and session.
        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("Public tools require a bound guest turn.");
    }

    private static async Task<string> EvidenceAsync(string? root, CancellationToken ct)
    {
        var facts = new List<string> {
            "MPCAL 支持密评量化评分、Word 密评报告、自定义报告模板和整理图片证据。WPF 版适用于 Windows。",
            "群内可以说“登记一个软件 BUG，在……时……”或“功能需求：……”。成功由本机返回登记编号。只登记脱敏信息，最多 400 字符。"
        };
        if (root is null) return string.Join("\n", facts);
        const string views = "Source/MPCAL.ApplicationWPF/";
        async Task<bool> Matches(string relative, string hash)
        {
            var info = new FileInfo(Path.Combine(root, views + relative));
            if (!info.Exists || info.Length > 256 * 1024) return false;
            using var file = info.OpenRead();
            return Convert.ToHexString(await SHA256.HashDataAsync(file, ct)) == hash;
        }
        if (await Matches("Views/MyWindows/Workbench/Tabs/ReportImportTab.xaml",
                "FB1069C349528D5EEC64AE149FE3FEF98BFD9687DB950DC09415752A73140536")
            && await Matches("Navigation/FeatureRegistrations.cs", "BF696C489D4D670B2583005E6DF7101A4532EE4025084724788C049143ECEFEE")
            && await Matches("Views/MyWindows/Workbench/Tabs/ReportImportTab.xaml.cs", "85FE793E0B8465DB7D129B8BCFAA184AD399C1303FFDB5D9B94B85A13651BCF4"))
            facts.Add("WPF 完整报告：先在新项目下建立或选择目标系统。系统面板 → 报告导入 → 导入Word报告（完整报告）。"
                + "选择目标系统，点“浏览...”选原 Word 报告，再点“检查报告”。在“可导入章节”勾选需要的章节，"
                + "点“开始导入”并确认。系统级数据可能替换目标系统的同类数据，项目共享资产合并去重。"
                + "完成后看“检查与导入结果”和人工复核提示。此入口与只导入附录 A 的入口不同。");
        if (await Matches("ViewModels/ReportReviewOptionsViewModel.cs",
                "938037AE808542515BFD44DA2C045281C3A8B6D7BB9D2AB0690937496D8FB4B0"))
            facts.Add("WPF 报告检查按规则包启停，更改勾选会清除旧结果。导出只检查已选中的包；若导出失败，需重新导出后查看。");
        return string.Join("\n", facts);
    }
}
