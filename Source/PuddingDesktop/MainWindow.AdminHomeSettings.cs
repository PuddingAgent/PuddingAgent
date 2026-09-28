using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-15 admin-home card: a compact summary plus navigation shortcuts. It composes the settings surfaces
/// that are already wired (workspaces, teams, runtime nodes, storage snapshot) instead of adding a data path,
/// and it does not copy the full workbench - which is what the card asks for.
/// </summary>
public sealed partial class MainWindow
{
    private AdminHomeSummary _ahSummary = AdminHomeSummary.Empty;
    private bool _ahBuilt;

    private TextBlock _ahCore = null!, _ahWorkspaces = null!, _ahTeams = null!, _ahNodes = null!;
    private TextBlock _ahFootprint = null!, _ahDisk = null!, _ahWarnings = null!;
    private InfoBar _ahNotice = null!;

    private void BuildAdminHomePanel()
    {
        if (_ahBuilt) return;
        _ahBuilt = true;

        var refresh = new Button { Content = "刷新摘要" };
        refresh.Click += async (_, _) => await LoadAdminHomeAsync();
        _ahCore = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        _ahWorkspaces = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _ahTeams = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _ahNodes = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _ahFootprint = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _ahDisk = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _ahWarnings = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .85, FontSize = 12 };
        _ahNotice = new InfoBar { IsOpen = false, IsClosable = true };

        // 快捷入口只做导航（切页/切分类），首页不直接执行写操作。
        var startChat = new Button { Content = "开始对话" };
        startChat.Click += (_, _) =>
        {
            _state.Navigate(ShellPage.Workbench);
            ShowNotice(_ahNotice, InfoBarSeverity.Informational, "已切到工作台", AdminHomeText.ShortcutNotice);
        };
        var workspaces = new Button { Content = "工作空间" };
        workspaces.Click += (_, _) => OpenSettingsCategory("workspaces", "basic");
        var models = new Button { Content = "模型服务" };
        models.Click += (_, _) => OpenSettingsCategory("llm", "providers");
        var diagnostics = new Button { Content = "系统诊断" };
        diagnostics.Click += (_, _) => OpenSettingsCategory("diagnostics", "overview");

        AdminHomeSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("管理概览", AdminHomeText.ScopeNotice,
                    Row(refresh), _ahCore, _ahWorkspaces, _ahTeams, _ahNodes, _ahFootprint, _ahDisk, _ahWarnings),
                Card("快捷入口", AdminHomeText.ShortcutNotice + " " + AdminHomeText.DiskSourceNotice + " " + AdminHomeText.FootprintNotice,
                    Row(startChat, workspaces, models, diagnostics), _ahNotice)
            }
        };
        SetAdminHomeEnabled(false);
    }

    internal async Task LoadAdminHomeAsync()
    {
        if (!_ahBuilt) return;
        SetAdminHomeEnabled(false);
        var warnings = new List<string>();
        var workspaceNames = new List<string>();
        var teamNames = new List<string>();
        var nodeCount = 0;
        var onlineNodeCount = 0;
        long? footprint = null;
        var coreReady = false;
        var coreState = AdminHomeText.DescribeKernelState(DesktopKernelState.NotConfigured);

        try
        {
            coreState = AdminHomeText.DescribeKernelState(_kernel.Settings.State);
            coreReady = AdminHomeText.IsReady(_kernel.Settings.State);
        }
        catch (Exception exception) { App.WriteDiagnostic(exception); warnings.Add("Core 状态读取失败"); }

        // 每个来源独立失败：一个来源不可用不影响其余摘要（并把失败原因写进警告行）。
        try { workspaceNames.AddRange((await _workspaces.ListAsync()).Select(item => item.WorkspaceId)); }
        catch (Exception exception) { App.WriteDiagnostic(exception); warnings.Add("工作区读取失败"); }

        try { teamNames.AddRange((await _teams.ListAsync()).Select(item => item.TeamId)); }
        catch (Exception exception) { App.WriteDiagnostic(exception); warnings.Add("团队读取失败"); }

        try
        {
            var nodes = await _runtimeNodes.ListNodesAsync();
            nodeCount = nodes.Count;
            onlineNodeCount = nodes.Count(node => node.IsOnline);
        }
        catch (Exception exception) { App.WriteDiagnostic(exception); warnings.Add("运行时节点读取失败"); }

        try { footprint = (await _storage.ReadSnapshotAsync()).TotalBytes; }
        catch (Exception exception) { App.WriteDiagnostic(exception); warnings.Add("存储快照读取失败"); }

        var disk = DiskSpaceProbe.TryRead(DataRootEditor.Text);
        var summary = AdminHomeSummary.Compose(coreReady, coreState, workspaceNames, teamNames,
            nodeCount, onlineNodeCount, footprint, disk, warnings);
        _ahSummary = summary;

        _ahCore.Text = $"Core 状态：{summary.CoreStateText}" + (summary.CoreReady ? "" : "（设置页仍可浏览，写操作会被拒绝）");
        _ahWorkspaces.Text = summary.WorkspaceText;
        _ahTeams.Text = summary.TeamText;
        _ahNodes.Text = summary.NodeText;
        _ahFootprint.Text = summary.FootprintText;
        _ahDisk.Text = summary.DiskText;
        _ahWarnings.Text = summary.HasWarnings ? "⚠ " + summary.Warnings : "";

        SetAdminHomeEnabled(true);
        _ahNotice.IsOpen = false;
        // Core 未就绪不是错误：摘要照常显示，只是数据可能为空。
        if (!summary.CoreReady)
            ShowNotice(_ahNotice, InfoBarSeverity.Informational, "Core 未就绪", "摘要已按当前可读到的数据显示；写操作会被拒绝。");
    }

    private void SetAdminHomeEnabled(bool enabled)
    {
        // 摘要区是只读文本；没有 Core 时也允许刷新（刷新只是重新读取摘要）。
        AdminHomeSettings.IsHitTestVisible = enabled;
    }
}
