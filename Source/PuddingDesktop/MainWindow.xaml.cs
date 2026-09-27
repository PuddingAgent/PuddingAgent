using System.ComponentModel;
using System.Reflection;
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
    private readonly DesktopPreferencesStore _settingsStore = new(App.StateRoot);
    private ShellLayout _layout = new();
    private string _material = "Mica";
    private string _language = DesktopLanguages.Default;
    private bool _loaded;
    private bool _rendering;
    private bool _demo;
    private HostingProbeWindow? _probe;

    public MainWindow(Func<IDesktopServices, (IDesktopKernel Kernel, Func<PuddingChat.IChatClient> ChatClient, ILlmResourceSettings LlmSettings, IVoiceResourceSettings VoiceSettings, IAgentDirectorySettings AgentDirectory, IToolPluginSettings ToolPlugins, ISkillHubSettings SkillHub, ISkillPackageSettings SkillPackages, IWorkspaceSettings Workspaces, IChannelSettings Channels, IWorkspaceResourceSettings WorkspaceResources, IMemoryLibrarySettings MemoryLibrary, IStorageSettings Storage, ISecuritySettings Security, IAccessTokenSettings AccessTokens, IRoleSettings Roles, IUserSettings Users, ITeamSettings Teams, IRuntimeNodeSettings RuntimeNodes, IDiagnosticsSettings Diagnostics, ISessionDirectorySettings Sessions)> createKernel)
    {
        InitializeComponent();
        _desktopServices = new Kernel.WinUiDesktopServices(DispatcherQueue, ShowFromCore, OpenDocumentFromCore);
        (_kernel, _createChatClient, _llmSettings, _voiceSettings, _agentDirectory, _toolPlugins, _skillHub, _skillPackages, _workspaces, _channels, _workspaceResources, _memoryLibrary, _storage, _security, _accessTokens, _roles, _users, _teams, _runtimeNodes, _diagnostics, _sessions) = createKernel(_desktopServices);
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
        _layout = result.Preferences.Layout;
        _language = result.Preferences.Language;
        ThemePicker.SelectedIndex = result.Preferences.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        Root.RequestedTheme = ParseTheme(result.Preferences.Theme);
        MaterialPicker.SelectedIndex = result.Preferences.Material switch { "MicaAlt" => 1, "Acrylic" => 2, _ => 0 };
        ApplyMaterial(result.Preferences.Material);
        UpdateCaptionColors();
        NavigationWidthSlider.Value = _layout.NavigationWidth;
        WorkspaceWidthSlider.Value = _layout.WorkspaceWidth;
        SettingsPath.Text = _settingsStore.FilePath;
        DiagnosticPath.Text = Path.Combine(App.StateRoot, "desktop.log");
        InitializePreferencesSettings();
        BuildLlmPanels();
        BuildVoicePanels();
        BuildAgentDirectoryPanel();
        BuildAgentDocumentPanel();
        BuildAgentModelPanel();
        BuildAgentSmartPanel();
        BuildAgentGuardrailPanel();
        BuildToolPluginPanels();
        BuildSkillHubPanels();
        BuildSkillLibraryPanel();
        BuildSkillEvolutionPanels();
        BuildSkillPackagePanel();
        BuildAgentGrantPanel();
        BuildWorkspacePanel();
        BuildChannelPanel();
        BuildWorkspaceResourcePanel();
        BuildMemoryLibraryPanel();
        BuildMemorySearchPanel();
        BuildStoragePanel();
        BuildStorageCleanupPanel();
        BuildSecurityPanel();
        BuildApprovalPanel();
        BuildAccessTokenPanel();
        BuildRolePanel();
        BuildUserPanel();
        BuildTeamPanel();
        BuildRuntimeNodePanel();
        BuildTimelinePanel();
        BuildDiagnosticsOverviewPanel();
        BuildSessionDirectoryPanel();
        RefreshAbout();
        KernelStatus.Title = _kernel.Snapshot.Description;
        if (result.Warning is { } warning) { SettingsNotice.Message = warning; SettingsNotice.Severity = InfoBarSeverity.Warning; }
        _loaded = true;
        InitializeSettingsNavigation();
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
        var role = RoleList.SelectedItem as RoleSummary;
        if (role is not null) _state.SelectRole(role.Identity);
        BindSettingsSelection(role);
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
            var showWorkbench = _state.Page is ShellPage.Workbench or ShellPage.Settings;
            WorkbenchPane.Visibility = showWorkbench && _kernel.Snapshot.State != DesktopKernelState.Ready ? Visibility.Visible : Visibility.Collapsed;
            NativeChatPane.Visibility = showWorkbench && _kernel.Snapshot.State == DesktopKernelState.Ready ? Visibility.Visible : Visibility.Collapsed;
            _nativeChat?.SetActive(_state.Page == ShellPage.Workbench && NativeChatPane.Visibility == Visibility.Visible);
            NavigationPane.Visibility = NativeChatPane.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            SettingsPane.Visibility = _state.Page == ShellPage.Settings ? Visibility.Visible : Visibility.Collapsed;
            RuntimePane.Visibility = _state.Page == ShellPage.RuntimeCenter ? Visibility.Visible : Visibility.Collapsed;
            UpdateSettingsOverlay();
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
            await _settingsStore.SaveAsync(new(_layout, Root.RequestedTheme.ToString(), _material, _language));
            SettingsNotice.Title = "偏好已保存";
            SettingsNotice.Message = DesktopLanguages.RequiresRestart && _language != DesktopLanguages.Default
                ? "外观与布局已保存；语言切换在重新打开 Desktop 后生效。"
                : "外观、布局与语言偏好已保存。";
            SettingsNotice.Severity = InfoBarSeverity.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            App.WriteDiagnostic(exception); SettingsNotice.Title = "保存失败"; SettingsNotice.Message = "无法写入偏好配置，请检查目录权限。"; SettingsNotice.Severity = InfoBarSeverity.Error;
        }
    }
    private void InitializePreferencesSettings()
    {
        LanguagePicker.Items.Clear();
        foreach (var language in DesktopLanguages.Supported)
            LanguagePicker.Items.Add(new ComboBoxItem { Content = language.DisplayName, Tag = language.Tag });
        LanguagePicker.SelectedIndex = Math.Max(0, DesktopLanguages.Supported
            .ToList().FindIndex(language => language.Tag == _language));
        LanguageNotice.Text = DesktopLanguages.Supported.Count == 1
            ? "当前构建只提供简体中文资源；新增语言需要先补齐资源后再出现在此列表。"
            : "语言切换在重新打开 Desktop 后生效。";
        HelpButton.Content = DesktopProductInfo.IsExternalLink(DesktopProductInfo.HelpUrl) ? "打开帮助（外部链接）" : "打开帮助";
        HelpNotice.Text = DesktopProductInfo.HelpUrl + "\n由系统默认浏览器打开，本机不会提交任何凭据。";
    }
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loaded || LanguagePicker.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        _language = DesktopLanguages.Normalize(tag);
        SettingsNotice.Title = "语言已选择";
        SettingsNotice.Message = "重新打开 Desktop 后应用；当前会话继续使用简体中文。";
        SettingsNotice.Severity = InfoBarSeverity.Informational;
    }
    private async void OnOpenHelp(object sender, RoutedEventArgs args)
    {
        if (!DesktopProductInfo.IsExternalLink(DesktopProductInfo.HelpUrl))
        {
            HelpNotice.Text = "帮助入口不可用：未配置有效的说明地址。";
            return;
        }
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(DesktopProductInfo.HelpUrl)))
                HelpNotice.Text = "未能打开帮助页面，请手动访问 " + DesktopProductInfo.HelpUrl;
        }
        catch (Exception exception)
        {
            App.WriteDiagnostic(exception);
            HelpNotice.Text = "打开帮助失败：" + exception.Message;
        }
    }
    private void RefreshAbout()
    {
        var assembly = typeof(MainWindow).Assembly;
        var informational = assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
        AboutProduct.Text = DesktopProductInfo.ProductName;
        AboutVersion.Text = "版本：" + DesktopProductInfo.NormalizeVersion(informational, fileVersion);
        AboutShell.Text = DesktopProductInfo.ShellDescription;
        AboutKernel.Text = DesktopProductInfo.KernelDescription + "\n内核状态：" + _kernel.Snapshot.Description;
        AboutConfig.Text = DesktopProductInfo.DescribeLocations(App.StateRoot, _chatDataRoot ?? DataRootEditor.Text);
        AboutHelp.Text = "帮助：" + DesktopProductInfo.HelpUrl + "（外部链接）";
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
            Check(!ShellInteractionHost.IsEnabled, "settings overlay isolates background input");
            foreach (var category in SettingsCatalog.Categories)
            {
                OpenSettingsCategory(category.Id);
                Check(SettingsTabs.TabItems.Count == category.Tabs.Count, "settings category " + category.Id);
                foreach (var tab in SettingsTabs.TabItems.OfType<TabViewItem>())
                {
                    SettingsTabs.SelectedItem = tab; Root.UpdateLayout();
                    Check(!tab.IsClosable && tab.Content is ScrollViewer, "settings tab " + category.Id + "/" + tab.Tag);
                }
            }
            SettingsSearch.Text = "MAXINPUTTOKENS";
            await WaitForSettingsUiAsync(() => SettingsCategoryTitle.Text == "模型与服务商" && SettingsTabs.TabItems.Count == 1);
            await SaveSettingsSmokeImageAsync(Path.ChangeExtension(reportPath, ".search.png"));
            Check(SettingsCategoryTitle.Text == "模型与服务商" && SettingsTabs.TabItems.Count == 1,
                $"settings field search: title={SettingsCategoryTitle.Text}, tabs={SettingsTabs.TabItems.Count}, query={SettingsSearch.Text}");
            SettingsSearch.Text = "不存在的设置-xyz";
            await WaitForSettingsUiAsync(() => SettingsEmpty.Visibility == Visibility.Visible && SettingsTabs.TabItems.Count == 0);
            Check(SettingsEmpty.Visibility == Visibility.Visible && SettingsTabs.TabItems.Count == 0, "settings search empty state");
            OpenSettingsCategory("general", "appearance");
            Check(AppearanceSettings.Visibility == Visibility.Visible, "existing appearance settings retained");
            await SaveSettingsSmokeImageAsync(Path.ChangeExtension(reportPath, ".settings.png"));
            _state.Navigate(ShellPage.Workbench);
            Check(ShellInteractionHost.IsEnabled, "closing settings restores background input");
            _state.Navigate(ShellPage.RuntimeCenter); Check(RuntimePane.Visibility == Visibility.Visible, "runtime center without Core");
            // DS-00: without Core every category stays browsable, but no settings operation may fake success.
            Check(_kernel.Settings.State == DesktopKernelState.Stopped, "settings availability reads the real kernel state");
            Check(_kernel.Settings.Capture().IsUnbound, "settings are browsable before any Core generation");
            try
            {
                await _kernel.RunSettingsAsync("smoke.offline", (_, _) => Task.FromResult(0));
                throw new InvalidOperationException("a settings operation must not report success without Core");
            }
            catch (SettingsUnavailableException error)
            {
                Check(error.Reason == SettingsUnavailable.KernelStopped, "settings operation refused without Core");
            }
            LoadDemo(); DraftEditor.Text = "builder draft";
            OpenDemoDocument(WorkspaceDocumentKind.File);
            Check(_kernel.Settings.Selection == new SettingsSelection("demo-project", "builder"), "settings selection follows the chosen Agent");
            var boundStamp = _kernel.Settings.Capture();
            RoleList.SelectedIndex = 1;
            Check(!_kernel.Settings.IsCurrent(boundStamp), "Agent switch invalidates earlier settings stamps");
            Check(_kernel.Settings.IsCurrent(_kernel.Settings.Capture()), "new settings stamp is current after the switch");
            Check(DraftEditor.Text == "", "role drafts isolated");
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
            await _settingsStore.SaveAsync(new(_layout, "Light", "Acrylic", "zh-CN"));
            Check((await _settingsStore.LoadAsync()).Preferences.Material == "Acrylic", "material persistence");
            Check((await _settingsStore.LoadAsync()).Preferences.Theme == "Light", "settings persistence");
            Check((await _settingsStore.LoadAsync()).Preferences.Language == "zh-CN", "language persistence");
            // DS-01: language, help and about are real native content instead of migration placeholders.
            OpenSettingsCategory("general", "preferences");
            Check(PreferencesSettings.Visibility == Visibility.Visible, "language and help card is native");
            Check(LanguagePicker.Items.Count == DesktopLanguages.Supported.Count, "language list only offers shipped resources");
            Check(LanguagePicker.SelectedIndex == 0, "current language is selected from the saved preference");
            Check(LanguageNotice.Text.Contains("简体中文", StringComparison.Ordinal), "single-language build states the real limit");
            Check(HelpNotice.Text.Contains(DesktopProductInfo.HelpUrl, StringComparison.Ordinal), "help entry is labelled as an external link");
            OpenSettingsCategory("about", "product");
            Check(AboutSettings.Visibility == Visibility.Visible, "about card is native");
            Check(AboutVersion.Text.StartsWith("版本：", StringComparison.Ordinal), "about shows a real build version field");
            Check(!AboutVersion.Text.Contains(DesktopProductInfo.UnknownVersion, StringComparison.Ordinal), "build version is not a placeholder");
            Check(AboutConfig.Text.Contains("desktop.preferences.json", StringComparison.Ordinal), "about lists read-only configuration locations");
            Check(AboutKernel.Text.Contains(_kernel.Snapshot.Description, StringComparison.Ordinal), "about reads the real kernel state");
            // DS-02: the LLM resource pool is native content that refuses to look ready without Core.
            OpenSettingsCategory("models", "providers");
            await WaitForSettingsUiAsync(() => _llmProviderNotice.IsOpen);
            Check(LlmProvidersSettings.Visibility == Visibility.Visible, "llm providers tab is native");
            Check(LlmProvidersSettings.Content is StackPanel, "llm provider form is built");
            Check(!_llmProviderSave.IsEnabled, "llm provider form stays disabled without Core");
            Check(_llmProviderNotice.Title == "Core 未就绪", "llm providers tab reports the real Core state");
            Check(_llmProviderNotice.Severity == InfoBarSeverity.Informational, "missing Core is not reported as a save failure");
            OpenSettingsCategory("models", "models");
            await WaitForSettingsUiAsync(() => _llmModelNotice.IsOpen);
            Check(LlmModelsSettings.Visibility == Visibility.Visible, "llm models tab is native");
            Check(!_llmModelSave.IsEnabled, "llm model form stays disabled without Core");
            OpenSettingsCategory("models", "quota");
            await WaitForSettingsUiAsync(() => _llmQuotaNotice.IsOpen);
            Check(LlmQuotaSettings.Visibility == Visibility.Visible, "llm quota tab is native");
            Check(LlmQuotaSettings.Content is StackPanel, "llm quota form is built");
            Check(!_llmQuotaSave.IsEnabled, "llm quota form stays disabled without Core");
            Check(_llmQuotaNotice.Title == "Core 未就绪", "llm quota tab reports the real Core state");
            // DS-03: voice providers, TTS and ASR are native content that also refuses to look ready without Core.
            OpenSettingsCategory("voice", "providers");
            await WaitForSettingsUiAsync(() => _vpNotice.IsOpen);
            Check(VoiceProvidersSettings.Visibility == Visibility.Visible, "voice providers tab is native");
            Check(VoiceProvidersSettings.Content is StackPanel, "voice provider form is built");
            Check(!_vpSave.IsEnabled, "voice provider form stays disabled without Core");
            Check(_vpNotice.Title == "Core 未就绪", "voice providers tab reports the real Core state");
            OpenSettingsCategory("voice", "tts");
            await WaitForSettingsUiAsync(() => _vtNotice.IsOpen);
            Check(VoiceTtsSettings.Visibility == Visibility.Visible, "voice tts tab is native");
            Check(!_vtSave.IsEnabled, "voice tts form stays disabled without Core");
            Check(_vtDefaults.Text.Contains("默认 TTS", StringComparison.Ordinal), "voice tts tab shows the effective defaults");
            OpenSettingsCategory("voice", "asr");
            await WaitForSettingsUiAsync(() => _vaNotice.IsOpen);
            Check(VoiceAsrSettings.Visibility == Visibility.Visible, "voice asr tab is native");
            Check(!_vaSave.IsEnabled, "voice asr form stays disabled without Core");
            Check(_vaDefaults.Text.Contains("默认 ASR", StringComparison.Ordinal), "voice asr tab shows the effective defaults");
            // DS-04 directory slice: templates, presets and role instances are native content.
            OpenSettingsCategory("agents", "directory");
            await WaitForSettingsUiAsync(() => _agNotice.IsOpen);
            Check(AgentDirectorySettings.Visibility == Visibility.Visible, "agent directory tab is native");
            Check(AgentDirectorySettings.Content is StackPanel, "agent directory form is built");
            Check(!_agTemplateSave.IsEnabled, "agent directory form stays disabled without Core");
            Check(_agNotice.Title == "Core 未就绪", "agent directory tab reports the real Core state");
            // DS-04 document slice: template documents and instance overrides are native content.
            OpenSettingsCategory("agents", "prompts");
            await WaitForSettingsUiAsync(() => _adNotice.IsOpen);
            Check(AgentDocumentsSettings.Visibility == Visibility.Visible, "agent documents tab is native");
            Check(AgentDocumentsSettings.Content is StackPanel, "agent document form is built");
            Check(!_adTemplateText.IsEnabled, "agent document form stays disabled without Core");
            Check(_adNotice.Title == "Core 未就绪", "agent documents tab reports the real Core state");
            // DS-04 model & memory slice: provider/model pairs plus memory mode and reasoning effort.
            OpenSettingsCategory("agents", "models");
            await WaitForSettingsUiAsync(() => _amNotice.IsOpen);
            Check(AgentModelsSettings.Visibility == Visibility.Visible, "agent models tab is native");
            Check(AgentModelsSettings.Content is StackPanel, "agent model form is built");
            Check(!_amEffort.IsEnabled, "agent model form stays disabled without Core");
            Check(_amNotice.Title == "Core 未就绪", "agent models tab reports the real Core state");
            // DS-04 Smart slice: the seven sub-agent routes on a role instance.
            OpenSettingsCategory("agents", "smart");
            await WaitForSettingsUiAsync(() => _asNotice.IsOpen);
            Check(AgentSmartSettings.Visibility == Visibility.Visible, "agent smart tab is native");
            Check(AgentSmartSettings.Content is StackPanel, "agent smart form is built");
            Check(!_asWorkspace.IsEnabled, "agent smart form stays disabled without Core");
            Check(_asNotice.Title == "Core 未就绪", "agent smart tab reports the real Core state");
            // DS-04 guardrail slice: execution budgets and the container image override.
            OpenSettingsCategory("agents", "guardrails");
            await WaitForSettingsUiAsync(() => _ag2Notice.IsOpen);
            Check(AgentGuardrailsSettings.Visibility == Visibility.Visible, "agent guardrails tab is native");
            Check(AgentGuardrailsSettings.Content is StackPanel, "agent guardrail form is built");
            Check(!_agTemplateRounds.IsEnabled, "agent guardrail form stays disabled without Core");
            Check(_ag2Notice.Title == "Core 未就绪", "agent guardrails tab reports the real Core state");
            // DS-06 read-only tool registry and plugin catalogue.
            OpenSettingsCategory("tools", "registry");
            await WaitForSettingsUiAsync(() => _tpNotice.IsOpen);
            Check(ToolRegistrySettings.Visibility == Visibility.Visible, "tool registry tab is native");
            Check(ToolRegistrySettings.Content is StackPanel, "tool registry form is built");
            Check(!_tpSearch.IsEnabled, "tool registry stays disabled without Core");
            Check(_tpNotice.Title == "Core 未就绪", "tool registry tab reports the real Core state");
            OpenSettingsCategory("tools", "plugins");
            await WaitForSettingsUiAsync(() => _tpNotice.IsOpen);
            Check(ToolsPluginsSettings.Visibility == Visibility.Visible, "plugin catalogue tab is native");
            Check(ToolsPluginsSettings.Content is StackPanel, "plugin catalogue form is built");
            // DS-07 overview + events slice (read-only).
            OpenSettingsCategory("skills", "overview");
            await WaitForSettingsUiAsync(() => _skNotice.IsOpen);
            Check(SkillOverviewSettings.Visibility == Visibility.Visible, "skill overview tab is native");
            Check(SkillOverviewSettings.Content is StackPanel, "skill overview form is built");
            Check(_skNotice.Title == "Core 未就绪", "skill overview tab reports the real Core state");
            OpenSettingsCategory("skills", "events");
            await WaitForSettingsUiAsync(() => _skNotice.IsOpen);
            Check(SkillEventsSettings.Visibility == Visibility.Visible, "skill events tab is native");
            Check(!_skPageSize.IsEnabled, "skill events form stays disabled without Core");
            // DS-07 library slice: skill detail, metadata edit, retire, version publish and install ledger.
            OpenSettingsCategory("skills", "library");
            await WaitForSettingsUiAsync(() => _slNotice.IsOpen);
            Check(SkillLibrarySettings.Visibility == Visibility.Visible, "skill library tab is native");
            Check(SkillLibrarySettings.Content is StackPanel, "skill library form is built");
            Check(!_slQuery.IsEnabled, "skill library form stays disabled without Core");
            Check(_slNotice.Title == "Core 未就绪", "skill library tab reports the real Core state");
            // DS-07 EVO MAP and install ledger slices (read-only).
            OpenSettingsCategory("skills", "evolution");
            await WaitForSettingsUiAsync(() => _seNotice.IsOpen);
            Check(SkillEvolutionSettings.Visibility == Visibility.Visible, "skill evolution tab is native");
            Check(SkillEvolutionSettings.Content is StackPanel, "skill evolution form is built");
            Check(!_seSkill.IsEnabled, "skill evolution form stays disabled without Core");
            OpenSettingsCategory("skills", "installs");
            await WaitForSettingsUiAsync(() => _siNotice.IsOpen);
            Check(SkillInstallsSettings.Visibility == Visibility.Visible, "skill installs tab is native");
            Check(!_siUpdateAgent.IsEnabled, "skill installs form stays disabled without Core");
            // DS-07 legacy skill packages: create/edit/delete/upload/download link.
            OpenSettingsCategory("skills", "legacy");
            await WaitForSettingsUiAsync(() => _spNotice.IsOpen);
            Check(SkillPackagesSettings.Visibility == Visibility.Visible, "skill packages tab is native");
            Check(SkillPackagesSettings.Content is StackPanel, "skill packages form is built");
            Check(!_spPicker.IsEnabled, "skill packages form stays disabled without Core");
            Check(_spNotice.Title == "Core 未就绪", "skill packages tab reports the real Core state");
            // DS-04 capability/skill grants (unblocked by DS-06 and DS-07).
            OpenSettingsCategory("agents", "capabilities");
            await WaitForSettingsUiAsync(() => _grNotice.IsOpen);
            Check(AgentCapabilitiesSettings.Visibility == Visibility.Visible, "agent capabilities tab is native");
            Check(AgentCapabilitiesSettings.Content is StackPanel, "agent capabilities form is built");
            Check(!_grTemplatePicker.IsEnabled, "agent capabilities form stays disabled without Core");
            Check(_grNotice.Title == "Core 未就绪", "agent capabilities tab reports the real Core state");
            // DS-05 workspace tab.
            OpenSettingsCategory("workspaces", "basic");
            await WaitForSettingsUiAsync(() => _wsNotice.IsOpen);
            Check(WorkspaceBasicSettings.Visibility == Visibility.Visible, "workspace basic tab is native");
            Check(WorkspaceBasicSettings.Content is StackPanel, "workspace basic form is built");
            Check(!_wsPicker.IsEnabled, "workspace basic form stays disabled without Core");
            Check(_wsNotice.Title == "Core 未就绪", "workspace basic tab reports the real Core state");
            // DS-05 channel tabs: providers plus channel credentials.
            OpenSettingsCategory("workspaces", "channels");
            await WaitForSettingsUiAsync(() => _chNotice.IsOpen);
            Check(ChannelProvidersSettings.Visibility == Visibility.Visible, "channel providers tab is native");
            Check(ChannelsSettings.Visibility == Visibility.Visible, "channels tab is native");
            Check(ChannelProvidersSettings.Content is StackPanel, "channel providers form is built");
            Check(ChannelsSettings.Content is StackPanel, "channels form is built");
            Check(!_chProviderPicker.IsEnabled, "channel forms stay disabled without Core");
            Check(_chNotice.Title == "Core 未就绪", "channel tab reports the real Core state");
            // DS-05 resource cards.
            OpenSettingsCategory("workspaces", "resources");
            await WaitForSettingsUiAsync(() => _wrNotice.IsOpen);
            Check(WorkspaceResourcesSettings.Visibility == Visibility.Visible, "workspace resources tab is native");
            Check(WorkspaceResourcesSettings.Content is StackPanel, "workspace resources form is built");
            Check(!_wrWorkspacePicker.IsEnabled, "workspace resources form stays disabled without Core");
            Check(_wrNotice.Title == "Core 未就绪", "workspace resources tab reports the real Core state");
            // DS-08 library slice.
            OpenSettingsCategory("memory", "library");
            await WaitForSettingsUiAsync(() => _mlNotice.IsOpen);
            Check(MemoryLibrarySettings.Visibility == Visibility.Visible, "memory library tab is native");
            Check(MemoryLibrarySettings.Content is StackPanel, "memory library form is built");
            Check(!_mlWorkspacePicker.IsEnabled, "memory library form stays disabled without Core");
            Check(_mlNotice.Title == "Core 未就绪", "memory library tab reports the real Core state");
            // DS-08 search and inspector slice.
            OpenSettingsCategory("memory", "search");
            await WaitForSettingsUiAsync(() => _msNotice.IsOpen || _msQuery.IsEnabled);
            Check(MemorySearchSettings.Visibility == Visibility.Visible, "memory search tab is native");
            Check(MemorySearchSettings.Content is StackPanel, "memory search form is built");
            Check(!_msQuery.IsEnabled, "memory search form stays disabled without Core");
            // DS-09 inventory and policy slice.
            OpenSettingsCategory("storage", "overview");
            await WaitForSettingsUiAsync(() => _stNotice.IsOpen);
            Check(StorageOverviewSettings.Visibility == Visibility.Visible, "storage overview tab is native");
            Check(StorageOverviewSettings.Content is StackPanel, "storage overview form is built");
            Check(!_stTrendRange.IsEnabled, "storage overview form stays disabled without Core");
            Check(_stNotice.Title == "Core 未就绪", "storage overview tab reports the real Core state");
            OpenSettingsCategory("storage", "policy");
            await WaitForSettingsUiAsync(() => _stNotice.IsOpen);
            Check(StoragePolicySettings.Visibility == Visibility.Visible, "storage policy tab is native");
            Check(!_stTargetPicker.IsEnabled, "storage policy form stays disabled without Core");
            // DS-09 cleanup slice.
            OpenSettingsCategory("storage", "cleanup");
            await WaitForSettingsUiAsync(() => _scNotice.IsOpen);
            Check(StorageCleanupSettings.Visibility == Visibility.Visible, "storage cleanup tab is native");
            Check(StorageCleanupSettings.Content is StackPanel, "storage cleanup form is built");
            Check(!_scTargetPicker.IsEnabled, "storage cleanup form stays disabled without Core");
            // DS-10 vault and classifier-health slice.
            OpenSettingsCategory("security", "vault");
            await WaitForSettingsUiAsync(() => _secNotice.IsOpen);
            Check(SecurityVaultSettings.Visibility == Visibility.Visible, "security vault tab is native");
            Check(SecurityVaultSettings.Content is StackPanel, "security vault form is built");
            Check(!_secName.IsEnabled, "security vault form stays disabled without Core");
            Check(_secNotice.Title == "Core 未就绪", "security vault tab reports the real Core state");
            OpenSettingsCategory("security", "audit");
            await WaitForSettingsUiAsync(() => _secAuditNotice.IsOpen);
            Check(SecurityAuditSettings.Visibility == Visibility.Visible, "security audit tab is native");
            Check(SecurityAuditSettings.Content is StackPanel, "security audit form is built");
            OpenSettingsCategory("security", "allowlist");
            await WaitForSettingsUiAsync(() => _alNotice.IsOpen);
            Check(AllowlistSettings.Visibility == Visibility.Visible, "allowlist tab is native");
            Check(AllowlistSettings.Content is StackPanel, "allowlist form is built");
            Check(!_alToolId.IsEnabled, "allowlist form stays disabled without Core");
            Check(_alNotice.Title == "Core 未就绪", "allowlist tab reports the real Core state");
            // DS-11 access token slice.
            OpenSettingsCategory("access", "tokens");
            await WaitForSettingsUiAsync(() => _atNotice.IsOpen);
            Check(AccessTokensSettings.Visibility == Visibility.Visible, "access tokens tab is native");
            Check(AccessTokensSettings.Content is StackPanel, "access tokens form is built");
            Check(!_atNewName.IsEnabled, "access tokens form stays disabled without Core");
            Check(_atNotice.Title == "Core 未就绪", "access tokens tab reports the real Core state");
            // DS-12 role slice.
            OpenSettingsCategory("accounts", "roles");
            await WaitForSettingsUiAsync(() => _rbNotice.IsOpen);
            Check(RolesSettings.Visibility == Visibility.Visible, "roles tab is native");
            Check(RolesSettings.Content is StackPanel, "roles form is built");
            Check(!_rbName.IsEnabled, "roles form stays disabled without Core");
            Check(_rbNotice.Title == "Core 未就绪", "roles tab reports the real Core state");
            // DS-12 user slice.
            OpenSettingsCategory("accounts", "users");
            await WaitForSettingsUiAsync(() => _usNotice.IsOpen);
            Check(UsersSettings.Visibility == Visibility.Visible, "users tab is native");
            Check(UsersSettings.Content is StackPanel, "users form is built");
            Check(!_usUsername.IsEnabled, "users form stays disabled without Core");
            Check(_usNotice.Title == "Core 未就绪", "users tab reports the real Core state");
            // DS-12 team slice.
            OpenSettingsCategory("accounts", "teams");
            await WaitForSettingsUiAsync(() => _tmNotice.IsOpen);
            Check(TeamsSettings.Visibility == Visibility.Visible, "teams tab is native");
            Check(TeamsSettings.Content is StackPanel, "teams form is built");
            Check(!_tmName.IsEnabled, "teams form stays disabled without Core");
            Check(_tmNotice.Title == "Core 未就绪", "teams tab reports the real Core state");
            // DS-13 runtime nodes slice.
            OpenSettingsCategory("runtime", "nodes");
            await WaitForSettingsUiAsync(() => _rnNotice.IsOpen);
            Check(RuntimeNodesSettings.Visibility == Visibility.Visible, "runtime nodes tab is native");
            Check(RuntimeNodesSettings.Content is StackPanel, "runtime nodes form is built");
            Check(!_rnReason.IsEnabled, "runtime nodes form stays disabled without Core");
            Check(_rnNotice.Title == "Core 未就绪", "runtime nodes tab reports the real Core state");
            // DS-14 diagnostics slice (two cards).
            OpenSettingsCategory("diagnostics", "timeline");
            await WaitForSettingsUiAsync(() => _tlNotice.IsOpen);
            Check(RuntimeTimelineSettings.Visibility == Visibility.Visible, "timeline tab is native");
            Check(RuntimeTimelineSettings.Content is StackPanel, "timeline form is built");
            Check(!_tlComponent.IsEnabled, "timeline filters stay disabled without Core");
            OpenSettingsCategory("diagnostics", "overview");
            await WaitForSettingsUiAsync(() => _dgNotice.IsOpen);
            Check(DiagnosticsOverviewSettings.Visibility == Visibility.Visible, "diagnostics overview tab is native");
            Check(DiagnosticsOverviewSettings.Content is StackPanel, "diagnostics overview form is built");
            Check(!_dgRefresh.IsEnabled, "diagnostics overview stays disabled without Core");
            // DS-14 session directory card.
            OpenSettingsCategory("diagnostics", "sessions");
            await WaitForSettingsUiAsync(() => _sdNotice.IsOpen);
            Check(SessionDirectorySettings.Visibility == Visibility.Visible, "session directory tab is native");
            Check(SessionDirectorySettings.Content is StackPanel, "session directory form is built");
            Check(!_sdWorkspace.IsEnabled, "session directory filters stay disabled without Core");
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
