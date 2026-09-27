using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-13 runtime nodes: the registry Core reports, with freeze/unfreeze. Freezing refuses a node's native
/// capability calls, so the page requires a reason (Core writes it to the audit trail only).
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<RuntimeNode> _rnNodes = [];
    private RuntimeNode? _rnNode;
    private bool _rnBuilt;
    private bool _rnSwitching;

    private TextBlock _rnSummary = null!, _rnDetail = null!;
    private ComboBox _rnPicker = null!;
    private TextBox _rnReason = null!;
    private StackPanel _rnNodeList = null!, _rnCapabilities = null!;
    private Button _rnFreeze = null!, _rnUnfreeze = null!;
    private InfoBar _rnNotice = null!;

    private void BuildRuntimeNodePanel()
    {
        if (_rnBuilt) return;
        _rnBuilt = true;

        var refresh = new Button { Content = "刷新节点" };
        refresh.Click += async (_, _) => await LoadRuntimeNodesAsync();
        _rnSummary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _rnNodeList = new StackPanel { Spacing = 2 };
        _rnPicker = new ComboBox { Header = "节点", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_rnPicker, "选择运行时节点");
        _rnPicker.SelectionChanged += (_, _) => { if (!_rnSwitching) ApplyRuntimeNodeSelection(); };
        _rnDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        _rnCapabilities = new StackPanel { Spacing = 2 };
        _rnReason = Field("冻结/解冻原因", "必填：会写入审计记录，节点模型本身不保存原因");
        _rnFreeze = new Button { Content = "冻结节点" };
        _rnFreeze.Click += async (_, _) => await FreezeRuntimeNodeAsync();
        _rnUnfreeze = new Button { Content = "解冻节点" };
        _rnUnfreeze.Click += async (_, _) => await UnfreezeRuntimeNodeAsync();
        _rnNotice = new InfoBar { IsOpen = false, IsClosable = true };

        RuntimeNodesSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("运行时节点", RuntimeNodeText.HostNotice + " " + RuntimeNodeText.ReasonNotice,
                    Row(refresh), _rnSummary, _rnNodeList,
                    _rnPicker, _rnDetail,
                    new TextBlock { Text = "原生能力", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, _rnCapabilities),
                Card("冻结与解冻", RuntimeNodeText.FreezeEffectNotice,
                    _rnReason, Row(_rnFreeze, _rnUnfreeze), _rnNotice)
            }
        };
        SetRuntimeNodeEnabled(false);
    }

    internal async Task LoadRuntimeNodesAsync()
    {
        if (!_rnBuilt) return;
        try
        {
            _rnNodes = await _runtimeNodes.ListNodesAsync();
            var summary = RuntimeNodeSummary.Of(_rnNodes);
            _rnSummary.Text = summary.HeadlineText + "\n" + summary.ExtraText;

            _rnNodeList.Children.Clear();
            if (_rnNodes.Count == 0)
                _rnNodeList.Children.Add(Muted("Core 还没有注册任何运行时节点（独立 Runtime 或嵌入节点都会在这里出现）。"));
            foreach (var node in _rnNodes)
                _rnNodeList.Children.Add(Muted(
                    $"{node.NodeId} · {node.StatusText} · {node.ModeText} · {node.HostText} · " +
                    $"{node.HeartbeatText} · 会话 {node.ActiveSessionCount}" +
                    (node.IsFrozen ? " · 已冻结" : "") +
                    (node.HostType.Length == 0 ? "" : $" · 宿主 {node.HostType}")));

            _rnSwitching = true;
            try
            {
                var previous = AgentSelected(_rnPicker);
                _rnPicker.Items.Clear();
                foreach (var node in _rnNodes)
                    _rnPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{node.NodeId} · {node.StatusText}" + (node.IsFrozen ? " · 已冻结" : ""),
                        Tag = node.NodeId
                    });
                var index = _rnNodes.ToList().FindIndex(node => node.NodeId == previous);
                _rnPicker.SelectedIndex = _rnNodes.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _rnSwitching = false; }
            ApplyRuntimeNodeSelection();
            SetRuntimeNodeEnabled(true);
            _rnNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportRuntimeNodeFailure(exception); }
    }

    private void ApplyRuntimeNodeSelection()
    {
        _rnNode = AgentSelected(_rnPicker) is { } nodeId
            ? _rnNodes.FirstOrDefault(node => node.NodeId == nodeId)
            : null;
        var node = _rnNode;
        _rnDetail.Text = node is null
            ? (_rnNodes.Count == 0 ? "没有可显示的节点。" : "请选择一个节点。")
            : $"{node.NodeId} · {node.StatusText}" + (node.IsFrozen ? " · 已冻结" : "") + "\n" +
              $"Endpoint {node.Endpoint} · 主机 {node.HostText}\n" +
              $"模式 {node.ModeText} · 宿主类型 {(node.HostType.Length == 0 ? "未上报" : node.HostType)}\n" +
              $"{node.HeartbeatText} · 活跃会话 {node.ActiveSessionCount} · 能力 {node.Capabilities.Count} 项\n" +
              (node.IsFrozen ? RuntimeNodeText.ReasonNotice : RuntimeNodeText.FreezeEffectNotice);

        _rnCapabilities.Children.Clear();
        if (node is null || node.Capabilities.Count == 0)
            _rnCapabilities.Children.Add(Muted(node is null ? "请选择节点。" : "该节点没有上报原生能力。"));
        foreach (var capability in node?.Capabilities ?? [])
            _rnCapabilities.Children.Add(Muted(capability.Display));

        _rnFreeze.IsEnabled = node is { IsFrozen: false };
        _rnUnfreeze.IsEnabled = node is { IsFrozen: true };
    }

    private async Task FreezeRuntimeNodeAsync()
    {
        if (_rnNode is not { } node) { WarnRuntimeNode("请先选择节点"); return; }
        var errors = RuntimeNodeText.Validate(node.NodeId, _rnReason.Text);
        if (errors.Count > 0) { ShowNotice(_rnNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "冻结运行时节点",
            Content = $"将冻结 {node.NodeId}。{RuntimeNodeText.FreezeEffectNotice} 原因：{_rnReason.Text.Trim()}",
            PrimaryButtonText = "冻结", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunRuntimeNodeAsync("节点已冻结", RuntimeNodeText.ReasonNotice,
            async () => { await _runtimeNodes.FreezeAsync(node.NodeId, _rnReason.Text.Trim()); await LoadRuntimeNodesAsync(); });
    }

    private async Task UnfreezeRuntimeNodeAsync()
    {
        if (_rnNode is not { } node) { WarnRuntimeNode("请先选择节点"); return; }
        var errors = RuntimeNodeText.Validate(node.NodeId, _rnReason.Text);
        if (errors.Count > 0) { ShowNotice(_rnNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunRuntimeNodeAsync("节点已解冻", "解冻即刻恢复该节点的原生能力调用，并写入审计记录。",
            async () => { await _runtimeNodes.UnfreezeAsync(node.NodeId, _rnReason.Text.Trim()); await LoadRuntimeNodesAsync(); });
    }

    private void WarnRuntimeNode(string message) => ShowNotice(_rnNotice, InfoBarSeverity.Warning, "请先选择", message);

    private async Task RunRuntimeNodeAsync(string title, string message, Func<Task> action)
    {
        SetRuntimeNodeEnabled(false);
        try
        {
            await action();
            SetRuntimeNodeEnabled(true);
            ShowNotice(_rnNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportRuntimeNodeFailure(exception); }
    }

    private void ReportRuntimeNodeFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            ArgumentException argument => (InfoBarSeverity.Warning, "请求被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "Core 拒绝了该操作", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败", "请查看诊断日志后重试。")
        };
        SetRuntimeNodeEnabled(!unavailable);
        ShowNotice(_rnNotice, severity, title, message);
    }

    private void SetRuntimeNodeEnabled(bool enabled)
    {
        foreach (var control in new Control[] { _rnPicker, _rnReason }) control.IsEnabled = enabled;
        if (!enabled)
        {
            _rnFreeze.IsEnabled = false;
            _rnUnfreeze.IsEnabled = false;
            return;
        }
        var node = _rnNode;
        _rnFreeze.IsEnabled = node is { IsFrozen: false };
        _rnUnfreeze.IsEnabled = node is { IsFrozen: true };
    }
}
