using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-06 native tool registry and plugin catalogue. Both are read-only by design: capabilities come
/// from the runtime registry and plugin packages are declared by plugin.json, so this page never
/// installs, enables or executes anything.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<ToolCatalogEntry> _tpTools = [];
    private PluginCatalogReport? _tpPlugins;
    private bool _tpBuilt;
    private bool _tpSwitching;

    private TextBox _tpSearch = null!;
    private ComboBox _tpToolPicker = null!, _tpPluginPicker = null!;
    private TextBlock _tpToolSummary = null!, _tpToolDetail = null!, _tpPluginSummary = null!, _tpPluginDetail = null!;
    private StackPanel _tpParameters = null!, _tpPluginTools = null!, _tpDiagnostics = null!;
    private InfoBar _tpNotice = null!;
    private InfoBar _tpPluginNotice = null!;

    private void BuildToolPluginPanels()
    {
        if (_tpBuilt) return;
        _tpBuilt = true;

        _tpSearch = new TextBox { Header = "搜索工具", PlaceholderText = "按 ID、名称、分类或来源过滤", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_tpSearch, "搜索工具");
        _tpSearch.TextChanged += (_, _) => { if (!_tpSwitching) FillToolPicker(); };
        var refreshTools = new Button { Content = "刷新" };
        refreshTools.Click += async (_, _) => await LoadToolRegistryAsync();
        _tpToolPicker = new ComboBox { Header = "工具", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_tpToolPicker, "选择工具");
        _tpToolPicker.SelectionChanged += (_, _) => ShowToolDetail();
        _tpToolSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        _tpToolDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        _tpParameters = new StackPanel { Spacing = 4 };
        _tpNotice = new InfoBar { IsOpen = false, IsClosable = true };

        ToolRegistrySettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("工具目录",
                    "只读：能力清单由运行时工具注册表推导，产品管理界面不提供新增、编辑或删除。" +
                    "「仅清单声明」的插件工具不可执行，本页不会把它标成可用。",
                    Row(refreshTools), _tpSearch, _tpToolPicker, _tpToolSummary, _tpToolDetail,
                    new TextBlock { Text = "参数", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, _tpParameters,
                    _tpNotice)
            }
        };

        var reloadPlugins = new Button { Content = "重新读取插件清单" };
        reloadPlugins.Click += async (_, _) => await ReloadPluginsAsync();
        _tpPluginPicker = new ComboBox { Header = "插件包", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_tpPluginPicker, "选择插件包");
        _tpPluginPicker.SelectionChanged += (_, _) => ShowPluginDetail();
        _tpPluginSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        _tpPluginDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        _tpPluginNotice = new InfoBar { IsOpen = false, IsClosable = true };
        _tpPluginTools = new StackPanel { Spacing = 4 };
        _tpDiagnostics = new StackPanel { Spacing = 4 };

        ToolsPluginsSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("插件清单与诊断",
                    "只读：插件由数据目录下的 plugin.json 声明，本页不提供安装、启用或卸载动作。" +
                    "无效清单会显示 Core 给出的校验原因；仅清单声明的工具不会被当成可执行。",
                    Row(reloadPlugins), _tpPluginPicker, _tpPluginSummary, _tpPluginDetail,
                    new TextBlock { Text = "该包声明的工具", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, _tpPluginTools,
                    new TextBlock { Text = "最近插件诊断", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, _tpDiagnostics,
                    _tpPluginNotice)
            }
        };
        SetToolPluginEnabled(false);
    }

    internal async Task LoadToolRegistryAsync()
    {
        if (!_tpBuilt) return;
        try
        {
            _tpTools = await _toolPlugins.ListToolsAsync();
            FillToolPicker();
            SetToolPluginEnabled(true);
            _tpNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportToolPluginFailure(exception); }
    }

    internal async Task LoadPluginCatalogAsync()
    {
        if (!_tpBuilt) return;
        try
        {
            _tpPlugins = await _toolPlugins.ReadPluginCatalogAsync();
            _tpSwitching = true;
            try
            {
                var previous = AgentSelected(_tpPluginPicker);
                _tpPluginPicker.Items.Clear();
                foreach (var package in _tpPlugins.Packages)
                    _tpPluginPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{package.Name}（{package.PluginId} {package.Version}）· {ToolPluginText.DescribePluginStatus(package.Status)}",
                        Tag = package.PluginId
                    });
                var index = _tpPlugins.Packages.ToList().FindIndex(package => package.PluginId == previous);
                _tpPluginPicker.SelectedIndex = _tpPlugins.Packages.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _tpSwitching = false; }
            ShowPluginDetail();
            SetToolPluginEnabled(true);
            _tpNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportToolPluginFailure(exception); }
    }

    private void FillToolPicker()
    {
        var previous = AgentSelected(_tpToolPicker);
        var matching = _tpTools.Where(tool => ToolPluginText.Matches(tool, _tpSearch.Text)).ToArray();
        _tpSwitching = true;
        try
        {
            _tpToolPicker.Items.Clear();
            foreach (var tool in matching)
                _tpToolPicker.Items.Add(new ComboBoxItem { Content = $"{tool.Name}（{tool.ToolId}）", Tag = tool.ToolId });
            var index = matching.ToList().FindIndex(tool => tool.ToolId == previous);
            _tpToolPicker.SelectedIndex = matching.Length > 0 ? Math.Max(0, index) : -1;
        }
        finally { _tpSwitching = false; }
        ShowToolDetail();
    }

    private void ShowToolDetail()
    {
        var executable = _tpTools.Count(tool => tool.IsExecutable);
        var plugin = _tpTools.Count(tool => tool.IsFromPlugin);
        _tpToolSummary.Text = string.IsNullOrWhiteSpace(_tpSearch.Text)
            ? $"共 {_tpTools.Count} 个工具 · 可执行 {executable} · 插件来源 {plugin}"
            : $"匹配 {_tpTools.Count(tool => ToolPluginText.Matches(tool, _tpSearch.Text))} / 共 {_tpTools.Count} 个工具";

        var tool = _tpTools.FirstOrDefault(candidate => candidate.ToolId == AgentSelected(_tpToolPicker));
        _tpParameters.Children.Clear();
        if (tool is null)
        {
            _tpToolDetail.Text = string.IsNullOrWhiteSpace(_tpSearch.Text)
                ? "没有可显示的工具。"
                : "没有匹配的工具，请调整关键词。";
            return;
        }
        _tpToolDetail.Text =
            $"{tool.Name}（{tool.ToolId}）\n" +
            $"分类：{tool.Category} · 权限层级：{tool.PermissionLevel} · 排序：{tool.SortOrder}\n" +
            $"来源：{tool.SourceKind}{(tool.SourceId.Length == 0 ? "" : " / " + tool.SourceId)}\n" +
            $"运行状态：{ToolPluginText.DescribeRuntimeStatus(tool.RuntimeStatus)}\n" +
            $"默认启用：{(tool.IsEnabledByDefault ? "是" : "否")}\n\n{tool.Description}";
        foreach (var parameter in tool.Parameters)
            _tpParameters.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Text = $"{(parameter.Required ? "* " : "  ")}{parameter.Name} : {parameter.Type} — {parameter.Description}"
            });
    }

    private void ShowPluginDetail()
    {
        if (_tpPlugins is null) { _tpPluginSummary.Text = ""; return; }
        _tpPluginSummary.Text = $"插件包 {_tpPlugins.Packages.Count} · 无效清单 {_tpPlugins.InvalidManifestCount} · " +
                                $"仅清单声明工具 {_tpPlugins.ManifestOnlyToolCount}";

        var package = _tpPlugins.Packages.FirstOrDefault(candidate => candidate.PluginId == AgentSelected(_tpPluginPicker));
        _tpPluginTools.Children.Clear();
        _tpDiagnostics.Children.Clear();
        if (package is null)
        {
            _tpPluginDetail.Text = _tpPlugins.Packages.Count == 0
                ? "数据目录下还没有插件包（plugins/<id>/plugin.json）。"
                : "请选择一个插件包。";
        }
        else
        {
            _tpPluginDetail.Text =
                $"{package.Name}（{package.PluginId}）\n版本：{package.Version}\n" +
                $"状态：{ToolPluginText.DescribePluginStatus(package.Status)}\n" +
                (package.StatusReason.Length == 0 ? "" : $"校验原因：{package.StatusReason}\n") +
                $"清单：{package.ManifestPath}";
            foreach (var tool in _tpPlugins.DeclaredTools.Where(tool =>
                         string.Equals(tool.PluginId, package.PluginId, StringComparison.OrdinalIgnoreCase)))
                _tpPluginTools.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, FontSize = 12,
                    Text = $"{tool.ToolId} · {ToolPluginText.DescribeRuntimeStatus(tool.RuntimeStatus)}"
                });
            if (_tpPluginTools.Children.Count == 0)
                _tpPluginTools.Children.Add(new TextBlock { Text = "该包没有声明工具。", FontSize = 12, Opacity = .7 });
        }

        if (_tpPlugins.RecentDiagnostics.Count == 0)
            _tpDiagnostics.Children.Add(new TextBlock { Text = "没有插件诊断记录。", FontSize = 12, Opacity = .7 });
        foreach (var entry in _tpPlugins.RecentDiagnostics.Take(20))
            _tpDiagnostics.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Text = $"{entry.OccurredAtUtc.ToLocalTime():MM-dd HH:mm} · {entry.EventType} · " +
                       $"{(entry.PluginId.Length == 0 ? "（无插件 ID）" : entry.PluginId)} · {entry.Message}"
            });
    }

    private async Task ReloadPluginsAsync()
    {
        try
        {
            await _toolPlugins.ReloadPluginsAsync();
            await LoadPluginCatalogAsync();
            ShowNotice(_tpNotice, InfoBarSeverity.Success, "插件清单已重新读取", "只重新读取了 plugin.json 描述；没有安装或启用任何包。");
            _tpNotice.IsOpen = true;
        }
        catch (Exception exception) { ReportToolPluginFailure(exception); }
    }

    private void ReportToolPluginFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            ArgumentException argument => (InfoBarSeverity.Warning, "请求被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "操作未完成", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败", "请查看诊断日志后重试。")
        };
        SetToolPluginEnabled(!unavailable);
        ShowNotice(_tpNotice, severity, title, message);
        ShowNotice(_tpPluginNotice, severity, title, message);
    }

    private void SetToolPluginEnabled(bool enabled)
    {
        foreach (var control in new Control[] { _tpSearch, _tpToolPicker, _tpPluginPicker }) control.IsEnabled = enabled;
    }
}
