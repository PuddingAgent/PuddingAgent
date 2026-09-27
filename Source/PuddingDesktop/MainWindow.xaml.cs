using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using PuddingDesktop.Foundation;
using Windows.Graphics;

namespace PuddingDesktop;

public sealed partial class MainWindow : Window
{
    private readonly ShellState _state = new();
    private readonly IDesktopKernel _kernel;
    private readonly SkeletonSettingsStore _settingsStore = new(App.StateRoot);
    private ShellLayout _layout = new();
    private string _material = "Mica";
    private bool _loaded;
    private bool _rendering;
    private bool _demo;
    private HostingProbeWindow? _probe;

    public MainWindow(Func<IDesktopServices, (IDesktopKernel Kernel, Func<PuddingChat.IChatClient> ChatClient)> createKernel)
    {
        InitializeComponent();
        _desktopServices = new Kernel.WinUiDesktopServices(DispatcherQueue, ShowFromCore, OpenDocumentFromCore);
        (_kernel, _createChatClient) = createKernel(_desktopServices);
        _kernel.StateChanged += OnKernelStateChanged;
        AppWindow.Closing += OnWindowClosing;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        SystemBackdrop = new MicaBackdrop();
        Root.ActualThemeChanged += (_, _) => UpdateCaptionColors();
        AppWindow.Resize(new SizeInt32(1540, 960));
        RoleList.ItemsSource = _state.Roles;
        _state.PropertyChanged += OnStateChanged;
        Closed += (_, _) => { _state.PropertyChanged -= OnStateChanged; _probe?.Close(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded) return;
        var result = await _settingsStore.LoadAsync();
        _layout = result.Settings.Layout;
        ThemePicker.SelectedIndex = result.Settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        Root.RequestedTheme = ParseTheme(result.Settings.Theme);
        MaterialPicker.SelectedIndex = result.Settings.Material switch { "MicaAlt" => 1, "Acrylic" => 2, _ => 0 };
        ApplyMaterial(result.Settings.Material);
        UpdateCaptionColors();
        NavigationWidthSlider.Value = _layout.NavigationWidth;
        WorkspaceWidthSlider.Value = _layout.WorkspaceWidth;
        SettingsPath.Text = _settingsStore.FilePath;
        DiagnosticPath.Text = Path.Combine(App.StateRoot, "desktop.log");
        KernelStatus.Title = _kernel.Snapshot.Description;
        if (result.Warning is { } warning) { SettingsNotice.Message = warning; SettingsNotice.Severity = InfoBarSeverity.Warning; }
        _loaded = true;
        ApplyLayout();
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Contains("--demo")) LoadDemo();
        var smokeIndex = Array.IndexOf(arguments, "--smoke-report");
        if (smokeIndex >= 0 && smokeIndex + 1 < arguments.Length)
        { await RunSmokeAsync(Path.GetFullPath(arguments[smokeIndex + 1])); return; }
        await InitializeKernelAsync(arguments);
    }

    private void OnLoadDemo(object sender, RoutedEventArgs args) => LoadDemo();

    private void LoadDemo()
    {
        _demo = true;
        _state.ReplaceRoles([
            new(new("demo-project", "builder"), "demo/code", "代码工程师", "实现功能 · 重构与修复", "demo-builder-main", "布局示例 · 未运行"),
            new(new("demo-project", "reviewer"), "demo/review", "代码审阅者", "检查边界 · 分析变更", "demo-reviewer-main", "布局示例 · 未运行"),
            new(new("demo-project", "tester"), "demo/test", "测试工程师", "设计验证 · 追踪证据", "demo-tester-main", "布局示例 · 未运行")
        ]);
        ProjectLabel.Text = "示例项目 / 不关联真实仓库";
        RoleCount.Text = _state.Roles.Count.ToString();
        EmptyRoles.Visibility = Visibility.Collapsed;
        DemoButton.Visibility = Visibility.Collapsed;
        RoleList.SelectedIndex = 0;
        PreviewNotice.Title = "布局示例";
        PreviewNotice.Message = "角色与文档均为示例。草稿仅保存在本次进程内，不会提交给 Agent。";
    }

    private void OnRoleSelected(object sender, SelectionChangedEventArgs args)
    {
        if (RoleList.SelectedItem is RoleSummary role) _state.SelectRole(role.Identity);
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_loaded) return;
        if (args.PropertyName == nameof(ShellState.SelectedDocument)) { RefreshDocuments(); return; }
        _rendering = true;
        try
        {
            var role = _state.SelectedRole;
            RoleTitle.Text = role?.Name ?? "从一个角色，开始新的工作";
            RoleSubtitle.Text = role is null ? "角色承担工作 · 会话承载沟通 · 结果可追溯" : $"示例项目  /  {role.Responsibility}";
            WelcomeTitle.Text = role is null ? "让合适的角色，\n把想法变成代码。" : $"与{role.Name}一起，\n开始下一项工作。";
            WelcomeDescription.Text = role is null ? "选择你的角色和项目，在同一个工作台中查看思路、工具调用、代码变更与交付物。" : "主会话与草稿跟随当前角色。切换角色后，已打开文档仍保留原有来源，不会把工作悄悄交给另一个角色。";
            if (DraftEditor.Text != _state.Draft) DraftEditor.Text = _state.Draft;
            DraftEditor.IsEnabled = _demo && role is not null;
            DraftHint.Text = role is null ? "先选择角色 · 草稿不会发送" : $"{role.Name}的草稿 · 未连接内核";
            foreach (var button in new[] { FileButton, DiffButton, TerminalButton, BrowserButton, ArtifactButton }) button.IsEnabled = _demo && role is not null;
            WorkbenchPane.Visibility = _state.Page == ShellPage.Workbench && _kernel.Snapshot.State != DesktopKernelState.Ready ? Visibility.Visible : Visibility.Collapsed;
            NativeChatPane.Visibility = _state.Page == ShellPage.Workbench && _kernel.Snapshot.State == DesktopKernelState.Ready ? Visibility.Visible : Visibility.Collapsed;
            _nativeChat?.SetActive(NativeChatPane.Visibility == Visibility.Visible);
            NavigationPane.Visibility = NativeChatPane.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            SettingsPane.Visibility = _state.Page == ShellPage.Settings ? Visibility.Visible : Visibility.Collapsed;
            RuntimePane.Visibility = _state.Page == ShellPage.RuntimeCenter ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _rendering = false; }
    }

    private void OnDraftChanging(TextBox sender, TextBoxTextChangingEventArgs args)
    {
        if (!_loaded || _rendering) return;
        _state.TrySetDraft(_state.SelectionGeneration, DraftEditor.Text);
    }

    private void OnOpenDocument(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string kind } && Enum.TryParse<WorkspaceDocumentKind>(kind, out var parsed)) OpenDemoDocument(parsed);
    }

    private void OpenDemoDocument(WorkspaceDocumentKind kind)
    {
        if (!_demo || _state.ActiveContext is not { } owner) return;
        var (title, content) = kind switch
        {
            WorkspaceDocumentKind.File => ("Hello.cs", "// 只读布局示例；不是项目文件\nnamespace Sample;\n\npublic sealed class Greeting\n{\n    public string Hello(string role)\n        => $\"Hello, {role}!\";\n}\n"),
            WorkspaceDocumentKind.Diff => ("变更示例", "只读 Diff 示例 · 未执行 Git 操作\n\n- return \"Hello\";\n+ return $\"Hello, {role}!\";\n\n真实接入后显示明确的 base/head 与来源 Run。"),
            WorkspaceDocumentKind.Terminal => ("终端输出", "终端输出占位\n\n未执行任何命令。\n真实 terminal session 将由 Core 内核创建和授权。\n此面板目前不是交互终端。"),
            WorkspaceDocumentKind.Browser => ("浏览器", "浏览器工作区占位\n\nAgent 浏览器尚未接入。\n可前往「运行中心」执行隔离双 WebView2 宿主验证。\n可见标签与 Agent 执行目标将分别管理。"),
            _ => ("交付物", "交付物预览占位\n\n真实文件、测试结果和报告将保留来源角色与 Run。\n当前内容仅用于检查文档标签和来源导航。")
        };
        var id = $"demo:{owner.Agent.AgentId}:{kind}";
        _state.OpenDocument(new(id, kind, title, id, owner, content));
        _layout = _layout with { WorkspaceVisible = true };
        ApplyLayout();
    }

    private void RefreshDocuments()
    {
        foreach (var tab in DocumentTabs.TabItems.OfType<TabViewItem>().ToArray())
            if (!_state.Documents.Any(document => document.Id == (string)tab.Tag)) DocumentTabs.TabItems.Remove(tab);
        foreach (var document in _state.Documents)
        {
            if (DocumentTabs.TabItems.OfType<TabViewItem>().Any(tab => (string)tab.Tag == document.Id)) continue;
            var panel = new StackPanel { Spacing = 18, Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = $"{document.Kind}  /  只读示例", FontSize = 12, Opacity = .6 });
            panel.Children.Add(new TextBlock { Text = document.Content, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            var source = new Button { Content = "返回来源角色", Tag = document.Owner.Agent };
            source.Click += OnRevealSource;
            panel.Children.Add(source);
            DocumentTabs.TabItems.Add(new TabViewItem { Header = document.Title, Tag = document.Id, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        }
        DocumentTabs.SelectedItem = DocumentTabs.TabItems.OfType<TabViewItem>().FirstOrDefault(tab => (string)tab.Tag == _state.SelectedDocument?.Id);
        EmptyDocuments.Visibility = _state.Documents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DocumentSource.Text = _state.SelectedDocument is { } selected
            ? $"来源角色：{selected.Owner.Agent.AgentId}  ·  项目：{selected.Owner.Agent.WorkspaceId}\n{selected.Owner.SessionId}  ·  没有真实 Run"
            : "没有活动文档";
    }

    private void OnDocumentSelected(object sender, SelectionChangedEventArgs args)
    {
        if (DocumentTabs.SelectedItem is TabViewItem { Tag: string id } && _state.SelectedDocument?.Id != id) _state.SelectDocument(id);
    }

    private void OnDocumentClosed(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Tag is string id) _state.CloseDocument(id);
    }

    private void OnRevealSource(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: AgentIdentity agent })
        {
            RoleList.SelectedItem = _state.Roles.FirstOrDefault(role => role.Identity == agent);
            _state.Navigate(ShellPage.Workbench);
        }
    }

    private void OnNewWork(object sender, RoutedEventArgs args)
    {
        _state.Navigate(ShellPage.Workbench);
        if (_nativeChat is not null) { _nativeChat.Composer.FocusEditor(); return; }
        if (_state.SelectedRole is null) { _layout = _layout with { NavigationVisible = true }; ApplyLayout(); RoleList.Focus(FocusState.Programmatic); }
        else DraftEditor.Focus(FocusState.Programmatic);
    }
    private void OnSettings(object sender, RoutedEventArgs args) => _state.Navigate(ShellPage.Settings);
    private void OnRuntime(object sender, RoutedEventArgs args) => _state.Navigate(ShellPage.RuntimeCenter);
    private void OnWorkbench(object sender, RoutedEventArgs args) => _state.Navigate(ShellPage.Workbench);
    private async void OnExit(object sender, RoutedEventArgs args) => await RequestExitAsync();
    private void OnToggleNavigation(object sender, RoutedEventArgs args) { _layout = _layout with { NavigationVisible = !_layout.NavigationVisible }; ApplyLayout(); }
    private void OnToggleWorkspace(object sender, RoutedEventArgs args) { _layout = _layout with { WorkspaceVisible = !_layout.WorkspaceVisible }; ApplyLayout(); }
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs args) { if (_loaded) ApplyLayout(); }
    private void ApplyLayout()
    {
        var allocation = _layout.Allocate(Root.ActualWidth);
        NavigationColumn.Width = new(allocation.NavigationWidth);
        WorkspaceColumn.Width = new(allocation.WorkspaceWidth);
        NavigationPane.Visibility = allocation.NavigationWidth > 0 && NativeChatPane.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        _nativeChat?.SetNavigationWidth(allocation.NavigationWidth);
        WorkspacePane.Visibility = allocation.WorkspaceWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnLayoutSliderChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_loaded) return;
        _layout = _layout with { NavigationWidth = NavigationWidthSlider.Value, WorkspaceWidth = WorkspaceWidthSlider.Value };
        ApplyLayout();
    }
    private void OnThemeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loaded && ThemePicker.SelectedItem is ComboBoxItem { Tag: string theme }) Root.RequestedTheme = ParseTheme(theme);
    }
    private void OnMaterialChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loaded && MaterialPicker.SelectedItem is ComboBoxItem { Tag: string material }) ApplyMaterial(material);
    }
    private void ApplyMaterial(string material)
    {
        _material = material;
        SystemBackdrop = material switch
        {
            "Acrylic" => new DesktopAcrylicBackdrop(),
            "MicaAlt" => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
            _ => new MicaBackdrop()
        };
    }
    private static ElementTheme ParseTheme(string theme) => theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    private void UpdateCaptionColors()
    {
        var highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        var foreground = highContrast
            ? new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground)
            : Root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = foreground;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
    }
    private async void OnSaveSettings(object sender, RoutedEventArgs args)
    {
        try
        {
            await _settingsStore.SaveAsync(new(_layout, Root.RequestedTheme.ToString(), _material));
            SettingsNotice.Title = "布局已保存"; SettingsNotice.Message = "仅更新预览程序的外观配置。"; SettingsNotice.Severity = InfoBarSeverity.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            App.WriteDiagnostic(exception); SettingsNotice.Title = "保存失败"; SettingsNotice.Message = "无法写入预览配置，请检查目录权限。"; SettingsNotice.Severity = InfoBarSeverity.Error;
        }
    }
    private async void OnAbout(object sender, RoutedEventArgs args)
    {
        await new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Pudding · WinUI 3", Content = "角色优先的 Coding 工作台\n\n这是可独立运行的骨架。\nCore 已通过 DLL 装配；原生角色导航与 Agent 浏览器仍在迁移。", CloseButtonText = "知道了" }.ShowAsync();
    }
    private async void OnHostingProbe(object sender, RoutedEventArgs args)
    {
        ProbeButton.IsEnabled = false;
        try
        {
            _probe?.Close(); _probe = new HostingProbeWindow(); _probe.Activate();
            ProbeResult.Text = await _probe.RunAsync();
        }
        catch (Exception exception) { App.WriteDiagnostic(exception); ProbeResult.Text = "宿主验证失败：" + exception.Message; }
        finally { ProbeButton.IsEnabled = true; }
    }

    private async Task RunSmokeAsync(string reportPath)
    {
        var checks = new List<string>();
        try
        {
            void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
            Check(!_state.Roles.Any() && !DraftEditor.IsEnabled, "offline shell does not impersonate a connected Agent");
            _state.Navigate(ShellPage.Settings); Check(SettingsPane.Visibility == Visibility.Visible, "settings without Core");
            _state.Navigate(ShellPage.RuntimeCenter); Check(RuntimePane.Visibility == Visibility.Visible, "runtime center without Core");
            LoadDemo(); DraftEditor.Text = "builder draft";
            OpenDemoDocument(WorkspaceDocumentKind.File);
            RoleList.SelectedIndex = 1; Check(DraftEditor.Text == "", "role drafts isolated");
            Check(_state.SelectedDocument?.Owner.Agent.AgentId == "builder", "document ownership stable");
            RoleList.SelectedIndex = 0; Check(DraftEditor.Text == "builder draft", "draft restored");
            foreach (var kind in Enum.GetValues<WorkspaceDocumentKind>()) OpenDemoDocument(kind);
            Check(DocumentTabs.TabItems.Count == 5, "five typed document tabs");
            _state.CloseDocument(_state.SelectedDocument!.Id); Check(DocumentTabs.TabItems.Count == 4, "close document updates UI");
            _layout = new(); ApplyLayout();
            Root.RequestedTheme = ElementTheme.Dark; Root.UpdateLayout();
            Root.RequestedTheme = ElementTheme.Light; Root.UpdateLayout(); checks.Add("theme resources resolve");
            ApplyMaterial("Acrylic"); Check(SystemBackdrop is DesktopAcrylicBackdrop, "desktop acrylic selected");
            ApplyMaterial("MicaAlt"); Check(SystemBackdrop is MicaBackdrop { Kind: Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt }, "mica alt selected");
            ApplyMaterial("Mica"); Check(SystemBackdrop is MicaBackdrop, "mica selected");
            Check(Root.Background is SolidColorBrush { Color.A: 0 }, "root exposes system backdrop");
            await _settingsStore.SaveAsync(new(_layout, "Light", "Acrylic"));
            Check((await _settingsStore.LoadAsync()).Settings.Material == "Acrylic", "material persistence");
            Check((await _settingsStore.LoadAsync()).Settings.Theme == "Light", "settings persistence");
            _probe = new HostingProbeWindow(); _probe.Activate();
            checks.Add(await _probe.RunAsync()); _probe.Close(); _probe = null;
            _state.Navigate(ShellPage.Workbench);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { success = true, checks, processId = Environment.ProcessId }, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
        catch (Exception exception)
        {
            App.WriteDiagnostic(exception); Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { success = false, checks, error = exception.ToString() }));
            Environment.ExitCode = 1; Close();
        }
    }
}
