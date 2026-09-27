using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-07 legacy skill-package slice: list, create, edit metadata, delete, replace a version and get a
/// download link. Upload and download need object storage; when it is not reachable the real error is
/// shown, never a fake success.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<SkillPackageSummary> _spPackages = [];
    private SkillPackageSummary? _spPackage;
    private bool _spBuilt;
    private bool _spSwitching;

    private ComboBox _spPicker = null!;
    private TextBlock _spDetail = null!, _spUrl = null!;
    private TextBox _spName = null!, _spDescription = null!, _spSort = null!;
    private ToggleSwitch _spEnabled = null!;
    private TextBox _spNewId = null!, _spNewName = null!, _spNewDescription = null!, _spNewVersion = null!;
    private TextBox _spNewSort = null!, _spNewPath = null!, _spReplaceVersion = null!, _spReplacePath = null!;
    private Button _spSave = null!, _spDelete = null!;
    private InfoBar _spNotice = null!;

    private void BuildSkillPackagePanel()
    {
        if (_spBuilt) return;
        _spBuilt = true;

        _spPicker = new ComboBox { Header = "技能包", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_spPicker, "选择技能包");
        _spPicker.SelectionChanged += (_, _) => { if (!_spSwitching) ApplySkillPackageSelection(); };
        var refresh = new Button { Content = "刷新" };
        refresh.Click += async (_, _) => await LoadSkillPackagesAsync();
        _spDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

        _spName = Field("名称");
        _spDescription = Field("描述");
        _spSort = Field("排序", "100");
        _spEnabled = new ToggleSwitch { Header = "启用该技能包", OnContent = "已启用", OffContent = "已停用", IsOn = true };
        _spSave = new Button { Content = "保存元数据" };
        _spSave.Click += async (_, _) => await SaveSkillPackageMetaAsync();
        _spDelete = new Button { Content = "删除技能包" };
        _spDelete.Click += async (_, _) => await DeleteSkillPackageAsync();

        _spNewId = Field("技能包 ID", "小写字母、数字与连字符，例如 my-skill-pack");
        _spNewName = Field("名称");
        _spNewDescription = Field("描述");
        _spNewVersion = Field("版本", "默认 1.0.0");
        _spNewSort = Field("排序", "100");
        _spNewPath = Field("包文件路径", "选择或粘贴 .zip / .tar.gz 文件路径");
        var browseNew = new Button { Content = "浏览…" };
        browseNew.Click += async (_, _) => await BrowseIntoAsync(_spNewPath);
        var upload = new Button { Content = "上传新技能包" };
        upload.Click += async (_, _) => await UploadSkillPackageAsync();

        _spReplaceVersion = Field("新版本号");
        _spReplacePath = Field("新包文件路径", "选择或粘贴 .zip / .tar.gz 文件路径");
        var browseReplace = new Button { Content = "浏览…" };
        browseReplace.Click += async (_, _) => await BrowseIntoAsync(_spReplacePath);
        var replace = new Button { Content = "替换为新版本" };
        replace.Click += async (_, _) => await ReplaceSkillPackageFileAsync();

        _spUrl = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        var download = new Button { Content = "获取下载链接" };
        download.Click += async (_, _) => await ShowDownloadUrlAsync();
        var open = new Button { Content = "用默认浏览器打开" };
        open.Click += async (_, _) => await OpenDownloadUrlAsync();

        _spNotice = new InfoBar { IsOpen = false, IsClosable = true };

        SkillPackagesSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("技能包（旧文件包）",
                    $"与 Hub 技能分开管理：这里是文件包台账。Core 只接受 {string.Join(" 或 ", SkillPackageText.AllowedExtensions)}（卡片提到的 .tgz 不被接受）。",
                    Row(refresh), _spPicker, _spDetail),
                Card("编辑元数据", "只更新名称、描述、启用状态与排序；已上传的文件与版本不受影响。",
                    _spName, _spDescription, _spSort, _spEnabled, Row(_spSave, _spDelete)),
                Card("上传新技能包", "上传依赖对象存储；未配置或不可达时会直接显示 Core 的错误，不会显示成功。",
                    _spNewId, _spNewName, _spNewDescription, _spNewVersion, _spNewSort, _spNewPath,
                    Row(browseNew, upload)),
                Card("上传新版本", "替换文件会先上传新对象再删除旧对象；被拒绝的替换保留当前文件。",
                    _spReplaceVersion, _spReplacePath, Row(browseReplace, replace)),
                Card("下载", "下载链接来自对象存储的预签名 URL，由系统默认浏览器打开；页面不代理文件字节。",
                    Row(download, open), _spUrl),
                _spNotice
            }
        };
        SetSkillPackageEnabled(false);
    }

    internal async Task LoadSkillPackagesAsync()
    {
        if (!_spBuilt) return;
        try
        {
            _spPackages = await _skillPackages.ListAsync();
            _spSwitching = true;
            try
            {
                var previous = AgentSelected(_spPicker);
                _spPicker.Items.Clear();
                foreach (var package in _spPackages)
                    _spPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{package.Name}（{package.SkillPackageId} {package.Version}）· {package.StateText}",
                        Tag = package.SkillPackageId
                    });
                var index = _spPackages.ToList().FindIndex(package => package.SkillPackageId == previous);
                _spPicker.SelectedIndex = _spPackages.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _spSwitching = false; }
            ApplySkillPackageSelection();
            SetSkillPackageEnabled(true);
            _spNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportSkillPackageFailure(exception); }
    }

    private void ApplySkillPackageSelection()
    {
        _spPackage = AgentSelected(_spPicker) is { } id
            ? _spPackages.FirstOrDefault(package => package.SkillPackageId == id)
            : null;
        var package = _spPackage;
        _spName.Text = package?.Name ?? "";
        _spDescription.Text = package?.Description ?? "";
        _spSort.Text = (package?.SortOrder ?? 100).ToString();
        _spEnabled.IsOn = package?.IsEnabled ?? true;
        _spDetail.Text = package is null
            ? (_spPackages.Count == 0 ? "还没有技能包台账记录。" : "请选择一个技能包。")
            : $"{package.Name}（{package.SkillPackageId}）\n版本 {package.Version} · 文件 {package.FileName} · {package.SizeText}\n" +
              $"状态 {package.StateText} · 排序 {package.SortOrder}\n" +
              $"创建 {package.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · 更新 {package.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        _spReplaceVersion.Text = package?.Version ?? "";
        _spDelete.IsEnabled = package is not null;
    }

    private async Task SaveSkillPackageMetaAsync()
    {
        if (_spPackage is null) { WarnSkillPackage("请先选择技能包"); return; }
        var edit = new SkillPackageMetaEdit(_spPackage.SkillPackageId, _spName.Text.Trim(), _spDescription.Text.Trim(),
            _spEnabled.IsOn, VoiceSettingsText.ParseOptionalInt(_spSort.Text) ?? 0);
        var errors = SkillPackageText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_spNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunSkillPackageAsync("元数据已保存", "文件与版本未被改动。",
            async () => { await _skillPackages.SaveMetaAsync(edit); await ReloadSkillPackagesAsync(edit.SkillPackageId); });
    }

    private async Task DeleteSkillPackageAsync()
    {
        if (_spPackage is not { } package) { WarnSkillPackage("请先选择技能包"); return; }
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "删除技能包",
            Content = $"将删除 {package.Name}（{package.SkillPackageId}）的台账记录，并尝试删除对象存储中的 {package.FileName}。" +
                      "引用该包的配置会失效。此操作不可撤销。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunSkillPackageAsync("技能包已删除", "台账记录已移除；对象删除失败只记日志，不影响记录移除。",
            async () => { await _skillPackages.DeleteAsync(package.SkillPackageId); await LoadSkillPackagesAsync(); });
    }

    private async Task UploadSkillPackageAsync()
    {
        var upload = new SkillPackageUploadEdit(_spNewId.Text.Trim(), _spNewName.Text.Trim(), _spNewDescription.Text.Trim(),
            _spNewVersion.Text.Trim(), VoiceSettingsText.ParseOptionalInt(_spNewSort.Text) ?? 100, _spNewPath.Text.Trim());
        var errors = SkillPackageText.Validate(upload);
        if (errors.Count > 0) { ShowNotice(_spNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunSkillPackageAsync("技能包已上传", "台账记录已写入；文件存入对象存储。",
            async () =>
            {
                await _skillPackages.UploadAsync(upload);
                _spNewId.Text = "";
                _spNewName.Text = "";
                _spNewPath.Text = "";
                await LoadSkillPackagesAsync();
            });
    }

    private async Task ReplaceSkillPackageFileAsync()
    {
        if (_spPackage is null) { WarnSkillPackage("请先选择技能包"); return; }
        var edit = new SkillPackageFileEdit(_spPackage.SkillPackageId, _spReplaceVersion.Text.Trim(), _spReplacePath.Text.Trim());
        var errors = SkillPackageText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_spNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunSkillPackageAsync("已替换为新版本", "新对象已上传，旧对象已删除；被拒绝的替换会保留当前文件。",
            async () => { await _skillPackages.ReplaceFileAsync(edit); await ReloadSkillPackagesAsync(edit.SkillPackageId); });
    }

    private async Task ShowDownloadUrlAsync()
    {
        if (_spPackage is null) { WarnSkillPackage("请先选择技能包"); return; }
        try
        {
            _spUrl.Text = await _skillPackages.GetDownloadUrlAsync(_spPackage.SkillPackageId);
            _spNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportSkillPackageFailure(exception); }
    }

    private async Task OpenDownloadUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(_spUrl.Text)) { await ShowDownloadUrlAsync(); return; }
        if (!Uri.TryCreate(_spUrl.Text, UriKind.Absolute, out var uri))
        {
            ShowNotice(_spNotice, InfoBarSeverity.Warning, "下载链接不可用", "请先重新获取下载链接。");
            return;
        }
        try { await Windows.System.Launcher.LaunchUriAsync(uri); }
        catch (Exception exception) { ReportSkillPackageFailure(exception); }
    }

    private async Task BrowseIntoAsync(TextBox target)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".zip");
            // A compound extension cannot be filtered reliably, so allow any file and validate after picking.
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            target.Text = file.Path;
            if (!SkillPackageText.IsAllowedFile(file.Path))
                ShowNotice(_spNotice, InfoBarSeverity.Warning, "文件类型不被接受",
                    $"仅支持 {string.Join(" 或 ", SkillPackageText.AllowedExtensions)}。");
        }
        catch (Exception exception)
        {
            App.WriteDiagnostic(exception);
            ShowNotice(_spNotice, InfoBarSeverity.Warning, "无法打开文件选择器",
                "请直接把包文件的完整路径粘贴到输入框。原因：" + exception.Message);
        }
    }

    private async Task ReloadSkillPackagesAsync(string skillPackageId)
    {
        _spPackages = await _skillPackages.ListAsync();
        _spSwitching = true;
        try
        {
            _spPicker.Items.Clear();
            foreach (var package in _spPackages)
                _spPicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{package.Name}（{package.SkillPackageId} {package.Version}）· {package.StateText}",
                    Tag = package.SkillPackageId
                });
            var index = _spPackages.ToList().FindIndex(package => package.SkillPackageId == skillPackageId);
            _spPicker.SelectedIndex = _spPackages.Count > 0 ? Math.Max(0, index) : -1;
        }
        finally { _spSwitching = false; }
        ApplySkillPackageSelection();
        await Task.CompletedTask;
    }

    private void WarnSkillPackage(string message) =>
        ShowNotice(_spNotice, InfoBarSeverity.Warning, "请先选择技能包", message);

    private async Task RunSkillPackageAsync(string title, string message, Func<Task> action)
    {
        SetSkillPackageEnabled(false);
        try
        {
            await action();
            SetSkillPackageEnabled(true);
            ShowNotice(_spNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportSkillPackageFailure(exception); }
    }

    private void ReportSkillPackageFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            FileNotFoundException missing => (InfoBarSeverity.Warning, "找不到包文件", missing.Message),
            ArgumentException argument => (InfoBarSeverity.Warning, "请求被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "Core 拒绝了该操作", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败",
                "上传与下载依赖对象存储；请确认其已配置且可达。诊断日志中有完整原因。")
        };
        SetSkillPackageEnabled(!unavailable);
        ShowNotice(_spNotice, severity, title, message);
    }

    private void SetSkillPackageEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _spPicker, _spName, _spDescription, _spSort, _spEnabled, _spSave,
            _spNewId, _spNewName, _spNewDescription, _spNewVersion, _spNewSort, _spNewPath,
            _spReplaceVersion, _spReplacePath
        }) control.IsEnabled = enabled;
        if (!enabled) { _spDelete.IsEnabled = false; return; }
        _spDelete.IsEnabled = _spPackage is not null;
    }
}
