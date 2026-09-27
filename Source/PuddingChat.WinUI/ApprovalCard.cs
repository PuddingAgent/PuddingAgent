using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PuddingChat.WinUI;

/// <summary>Independent native human-decision card; no optimistic approval and no execution authority.</summary>
public sealed class ApprovalCard : UserControl, IDisposable
{
    private readonly IChatApprovals _client;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _heading = new() { FontSize = 18, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _arguments = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240 };
    private readonly TextBox _reason = new() { Header = "决定理由（可选）", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 100 };
    private readonly Button _allow = new() { Content = "允许本次操作" };
    private readonly Button _deny = new() { Content = "拒绝" };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private ApprovalSubmission? _pending;
    private bool _submitting, _disposed;
    public ChatApproval Snapshot { get; private set; }
    public string Reason { get => _reason.Text; set => _reason.Text = value; }
    public bool CanAllow => _allow.IsEnabled;
    public bool CanDeny => _deny.IsEnabled;
    public string Error => _error.IsOpen ? _error.Message : "";
    public ApprovalCard(ChatApproval snapshot, IChatApprovals client, TimeProvider? clock = null)
    {
        Snapshot = snapshot; _client = client; _clock = clock ?? TimeProvider.System;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_allow); actions.Children.Add(_deny);
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(_heading); body.Children.Add(_description); body.Children.Add(_status);
        body.Children.Add(new Expander { Header = "查看完整操作参数", Content = _arguments,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        body.Children.Add(_reason); body.Children.Add(actions); body.Children.Add(_error);
        var surface = Surfaces.Card("CardBackgroundFillColorDefaultBrush"); surface.Padding = new Thickness(16);
        surface.CornerRadius = new CornerRadius(12); surface.BorderThickness = new Thickness(1); surface.Child = body; Content = surface;
        _allow.Click += async (_, _) => await DecideAsync(ApprovalChoice.AllowOnce);
        _deny.Click += async (_, _) => await DecideAsync(ApprovalChoice.Deny);
        _timer.Tick += (_, _) => RefreshAvailability();
        Loaded += (_, _) => { if (!_disposed) { _timer.Start(); RefreshAvailability(); } };
        Unloaded += (_, _) => _timer.Stop();
        Render();
    }
    public void Update(ChatApproval snapshot)
    {
        if (snapshot.Role != Snapshot.Role || snapshot.SessionId != Snapshot.SessionId || snapshot.ApprovalId != Snapshot.ApprovalId)
            throw new ArgumentException("An approval card cannot be reused for another request.");
        if (_disposed || snapshot.Version < Snapshot.Version) return;
        if (snapshot.Version == Snapshot.Version && snapshot.Status != Snapshot.Status)
            throw new ArgumentException("Approval state changes require a new authoritative version.");
        if (snapshot.Version > Snapshot.Version || snapshot.Status != ChatApprovalStatus.Pending) _pending = null;
        Snapshot = snapshot; Render();
    }
    public async Task DecideAsync(ApprovalChoice choice)
    {
        RefreshAvailability();
        if (choice == ApprovalChoice.AllowOnce ? !CanAllow : choice != ApprovalChoice.Deny || !CanDeny) return;
        _pending ??= new(Snapshot.Role, Snapshot.SessionId, Snapshot.ApprovalId, Snapshot.Version,
            Guid.NewGuid().ToString("N"), choice, string.IsNullOrWhiteSpace(Reason) ? null : Reason.Trim());
        _submitting = true; _error.IsOpen = false; RefreshAvailability();
        try
        {
            var result = await _client.DecideAsync(_pending, _lifetime.Token).WaitAsync(_lifetime.Token);
            if (!_disposed) Update(result);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { if (!_disposed) { _error.Message = e.Message; _error.IsOpen = true; } }
        finally { _submitting = false; if (!_disposed) RefreshAvailability(); }
    }
    private void Render()
    {
        _heading.Text = $"{(Snapshot.Status == ChatApprovalStatus.Pending ? "需要你的决定" : "操作审批")} · {Snapshot.ToolName}";
        _description.Text = $"{Snapshot.Description}\n评估：{Snapshot.RiskDescription ?? "未提供评估"}\n有效期至 {Snapshot.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        _arguments.Text = Snapshot.Arguments; RefreshAvailability();
    }
    public void RefreshAvailability()
    {
        var expired = Snapshot.Status == ChatApprovalStatus.Pending && Snapshot.ExpiresAt <= _clock.GetUtcNow();
        var enabled = !_disposed && !_submitting && !expired && Snapshot.Status == ChatApprovalStatus.Pending;
        _allow.IsEnabled = enabled && Snapshot.AllowedChoices.Contains(ApprovalChoice.AllowOnce) && (_pending is null || _pending.Choice == ApprovalChoice.AllowOnce);
        _deny.IsEnabled = enabled && Snapshot.AllowedChoices.Contains(ApprovalChoice.Deny) && (_pending is null || _pending.Choice == ApprovalChoice.Deny);
        _reason.IsEnabled = enabled && _pending is null;
        _allow.Content = _pending?.Choice == ApprovalChoice.AllowOnce ? "重试原决定" : "允许本次操作";
        _deny.Content = _pending?.Choice == ApprovalChoice.Deny ? "重试原决定" : "拒绝";
        _status.Text = _submitting ? "正在提交决定…" : expired ? "审批已过期" : Snapshot.Status switch
        {
            ChatApprovalStatus.Pending => "待审批 · 仅针对本次操作", ChatApprovalStatus.Approved => "已批准 · 等待内核执行",
            ChatApprovalStatus.Denied => "已拒绝", ChatApprovalStatus.Expired => "审批已过期",
            ChatApprovalStatus.Consumed => "许可已消费 · 执行结果以任务记录为准",
            ChatApprovalStatus.DispatchUnknown => "执行结果待核实", ChatApprovalStatus.DeferredDependency => "等待审批依赖恢复", _ => "不可审批"
        };
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _timer.Stop(); _lifetime.Cancel(); _lifetime.Dispose(); RefreshAvailability(); }
}
