using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-17 orchestration and HTTP-hook cards. Both are management entries: graphs, revision structure,
/// validation and publishing, manual runs, and the trigger view. Triggers are part of a revision, so enabling
/// or changing one publishes a new revision - Core has no separate hook CRUD - and configuration values are
/// never echoed.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<OrchestrationGraphSummary> _orGraphs = [];
    private IReadOnlyList<OrchestrationRevisionSummary> _orRevisions = [];
    private IReadOnlyList<WorkspaceSummary> _orWorkspaces = [];
    private OrchestrationGraphDetail? _orDetail;
    private bool _orBuilt;
    private bool _orSwitching;

    private ComboBox _orWorkspace = null!, _orGraph = null!;
    private TextBlock _orDetailText = null!, _orRevisionsText = null!, _orRunText = null!;
    private StackPanel _orStructure = null!, _orHooks = null!;
    private TextBox _orJson = null!, _orExpectedRevision = null!;
    private Button _orValidate = null!, _orPublish = null!, _orRun = null!;
    private InfoBar _orNotice = null!;

    private void BuildOrchestrationPanel()
    {
        if (_orBuilt) return;
        _orBuilt = true;

        _orWorkspace = new ComboBox { Header = "工作区", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_orWorkspace, "选择编排工作区");
        _orWorkspace.SelectionChanged += async (_, _) => { if (!_orSwitching) await LoadOrchestrationGraphsAsync(); };
        _orGraph = new ComboBox { Header = "编排图", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_orGraph, "选择编排图");
        _orGraph.SelectionChanged += async (_, _) => { if (!_orSwitching) await LoadOrchestrationDetailAsync(); };
        var refresh = new Button { Content = "刷新编排图" };
        refresh.Click += async (_, _) => await LoadOrchestrationGraphsAsync();
        _orDetailText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        _orRevisionsText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .85, FontSize = 12 };
        _orStructure = new StackPanel { Spacing = 2 };
        _orHooks = new StackPanel { Spacing = 2 };
        _orRunText = new TextBlock { TextWrapping = TextWrapping.Wrap };

        _orJson = Field("修订定义 JSON", "粘贴完整图定义；先校验再发布");
        _orJson.AcceptsReturn = true;
        _orJson.TextWrapping = TextWrapping.Wrap;
        _orJson.Height = 160;
        _orExpectedRevision = Field("期望当前修订号", "新建图填 0；其余填图当前修订号（CAS）");
        _orValidate = new Button { Content = "校验草稿" };
        _orValidate.Click += async (_, _) => await ValidateOrchestrationDraftAsync();
        _orPublish = new Button { Content = "发布修订（CAS）" };
        _orPublish.Click += async (_, _) => await PublishOrchestrationRevisionAsync();
        _orRun = new Button { Content = "手动运行当前修订" };
        _orRun.Click += async (_, _) => await StartOrchestrationRunAsync();
        _orNotice = new InfoBar { IsOpen = false, IsClosable = true };

        OrchestrationSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("编排图", OrchestrationText.ScopeNotice,
                    _orWorkspace, Row(refresh), _orGraph, _orDetailText, _orRevisionsText, _orStructure),
                Card("人工 HTTP Hook", OrchestrationText.HookNotice + " " + OrchestrationText.HookSecretNotice + " " +
                    OrchestrationText.TriggerSemanticsNotice, _orHooks),
                Card("修订校验与发布", OrchestrationText.ValidateNotice + " " + OrchestrationText.CasNotice,
                    _orJson, _orExpectedRevision, Row(_orValidate, _orPublish)),
                Card("手动运行", "运行详情与画布按独立工作页实施；这里只创建并激活一次运行。",
                    _orRunText, Row(_orRun)),
                _orNotice
            }
        };
        SetOrchestrationEnabled(false);
    }

    internal async Task LoadOrchestrationGraphsAsync()
    {
        if (!_orBuilt) return;
        SetOrchestrationEnabled(false);
        try
        {
            if (_orWorkspaces.Count == 0) _orWorkspaces = await _workspaces.ListAsync();
            _orSwitching = true;
            try
            {
                if (_orWorkspace.Items.Count != _orWorkspaces.Count)
                {
                    var previous = AgentSelected(_orWorkspace);
                    _orWorkspace.Items.Clear();
                    foreach (var workspace in _orWorkspaces)
                        _orWorkspace.Items.Add(new ComboBoxItem
                        {
                            Content = $"{workspace.WorkspaceId} · {workspace.StateText}", Tag = workspace.WorkspaceId
                        });
                    var index = _orWorkspaces.ToList().FindIndex(item => item.WorkspaceId == previous);
                    _orWorkspace.SelectedIndex = _orWorkspaces.Count > 0 ? Math.Max(0, index) : -1;
                }
            }
            finally { _orSwitching = false; }

            var workspaceId = AgentSelected(_orWorkspace) ?? "";
            _orGraphs = workspaceId.Length == 0 ? [] : await _orchestration.ListGraphsAsync(workspaceId);

            _orSwitching = true;
            try
            {
                var previous = AgentSelected(_orGraph);
                _orGraph.Items.Clear();
                foreach (var graph in _orGraphs)
                    _orGraph.Items.Add(new ComboBoxItem { Content = graph.LineText, Tag = graph.GraphId });
                var index = _orGraphs.ToList().FindIndex(graph => graph.GraphId == previous);
                _orGraph.SelectedIndex = _orGraphs.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _orSwitching = false; }

            SetOrchestrationEnabled(true);
            await LoadOrchestrationDetailAsync();
            _orNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportOrchestrationFailure(exception); }
    }

    private async Task LoadOrchestrationDetailAsync()
    {
        var graphId = AgentSelected(_orGraph);
        if (string.IsNullOrEmpty(graphId))
        {
            _orDetail = null;
            _orDetailText.Text = _orGraphs.Count == 0 ? "该工作区还没有编排图。" : "请选择一个编排图。";
            _orRevisionsText.Text = "";
            _orStructure.Children.Clear();
            _orHooks.Children.Clear();
            return;
        }
        try
        {
            _orDetail = await _orchestration.GetLatestAsync(graphId);
            _orRevisions = await _orchestration.ListRevisionsAsync(graphId);
            var detail = _orDetail;
            _orDetailText.Text = detail is null
                ? $"{graphId} 还没有修订。"
                : detail.HeadlineText + "\n" + detail.MetaText + "\n目标：" + detail.Objective;
            _orRevisionsText.Text = _orRevisions.Count == 0
                ? "没有修订记录。"
                : "修订历史：\n" + string.Join("\n", _orRevisions.Select(revision => revision.LineText));
            if (detail is not null) _orExpectedRevision.Text = detail.Revision.ToString();

            _orStructure.Children.Clear();
            if (detail is null || detail.Nodes.Count == 0)
                _orStructure.Children.Add(Muted(detail is null ? "没有可显示的修订。" : "该修订没有节点。"));
            foreach (var node in detail?.Nodes ?? [])
            {
                _orStructure.Children.Add(Muted("节点 " + node.LineText));
                foreach (var edge in (detail?.Edges ?? []).Where(edge => edge.FromNodeId == node.NodeId))
                    _orStructure.Children.Add(Muted("    → " + edge.LineText));
                foreach (var input in (detail?.Inputs ?? []).Where(input => input.LineText.Length > 0))
                    _orStructure.Children.Add(Muted("    输入 " + input.LineText));
            }
            if (detail is not null && detail.Edges.Count > 0)
                _orStructure.Children.Add(Muted($"边合计 {detail.Edges.Count} 条"));

            FillHookList(detail);
            _orRunText.Text = detail is null
                ? "请先选择有修订的编排图。"
                : $"将对 {detail.GraphId} 的修订 {detail.Revision}（{detail.RevisionId}）创建并激活一次运行。" +
                  (detail.RequiresExplicitActivation ? "该图需要显式激活（Core 会在创建后激活）。" : "");
            _orRun.IsEnabled = detail is not null;
            _orNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportOrchestrationFailure(exception); }
    }

    private void FillHookList(OrchestrationGraphDetail? detail)
    {
        _orHooks.Children.Clear();
        if (detail is null)
        {
            _orHooks.Children.Add(Muted("请先选择一个有修订的编排图。"));
            return;
        }
        if (detail.Triggers.Count == 0)
        {
            _orHooks.Children.Add(Muted("该修订没有配置触发器；要新增 HTTP Hook 需要发布一个含触发器的修订。"));
            return;
        }
        _orHooks.Children.Add(Muted(
            $"触发器 {detail.Triggers.Count} 个（启用 {detail.EnabledTriggerCount}）· 启停需发布新修订"));
        foreach (var trigger in detail.Triggers)
        {
            _orHooks.Children.Add(Muted(trigger.LineText));
            _orHooks.Children.Add(Muted("    外部调用路径 " + trigger.InvocationPath(detail.GraphId)));
            foreach (var binding in trigger.InputBindings)
                _orHooks.Children.Add(Muted("    输入映射 " + binding));
        }
    }

    private OrchestrationRevisionDraft ReadDraft() => new(
        AgentSelected(_orGraph) ?? "",
        int.TryParse(_orExpectedRevision.Text.Trim(), out var expected) ? expected : 0,
        _orJson.Text);

    private async Task ValidateOrchestrationDraftAsync()
    {
        var draft = ReadDraft();
        var errors = OrchestrationText.Validate(draft);
        if (errors.Count > 0) { ShowNotice(_orNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunOrchestrationAsync("校验通过", "草稿只编译、不落盘。", async () =>
        {
            var result = await _orchestration.ValidateAsync(draft);
            ShowNotice(_orNotice,
                result.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                result.DescribeText, result.IssuesText);
        }, successNotice: false);
    }

    private async Task PublishOrchestrationRevisionAsync()
    {
        var draft = ReadDraft();
        var errors = OrchestrationText.Validate(draft);
        if (errors.Count > 0) { ShowNotice(_orNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunOrchestrationAsync("修订已发布", "图头已前进；修订不可变。",
            async () =>
            {
                await _orchestration.PublishAsync(draft);
                await LoadOrchestrationGraphsAsync();
            });
    }

    private async Task StartOrchestrationRunAsync()
    {
        if (_orDetail is not { } detail) { ShowNotice(_orNotice, InfoBarSeverity.Warning, "请先选择编排图", "需要先有修订。"); return; }
        await RunOrchestrationAsync("运行已启动", "运行详情与画布在独立工作页查看。",
            async () =>
            {
                var runId = await _orchestration.StartManualRunAsync(detail.GraphId, detail.RevisionId);
                ShowNotice(_orNotice, InfoBarSeverity.Success, "运行已启动", $"RunId {runId}");
            }, successNotice: false);
    }

    private async Task RunOrchestrationAsync(string title, string message, Func<Task> action, bool successNotice = true)
    {
        SetOrchestrationEnabled(false);
        try
        {
            await action();
            SetOrchestrationEnabled(true);
            if (successNotice) ShowNotice(_orNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportOrchestrationFailure(exception); }
    }

    private void ReportOrchestrationFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            SettingsConflictException conflict => (InfoBarSeverity.Warning, "修订冲突，已阻止覆盖", conflict.Message),
            ArgumentException argument => (InfoBarSeverity.Warning, "请求被拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "Core 拒绝了该操作", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败", "请查看诊断日志后重试。")
        };
        SetOrchestrationEnabled(!unavailable);
        ShowNotice(_orNotice, severity, title, message);
    }

    private void SetOrchestrationEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _orWorkspace, _orGraph, _orJson, _orExpectedRevision
        }) control.IsEnabled = enabled;
        foreach (var button in new[] { _orValidate, _orPublish, _orRun }) button.IsEnabled = enabled;
        if (enabled) _orRun.IsEnabled = _orDetail is not null;
    }
}
