using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace PuddingChat.WinUI;

/// <summary>Explicit recording and draft acceptance. The workspace owns/disposes the shared session.</summary>
public sealed class VoiceInputControl : UserControl, IDisposable
{
    private readonly VoiceInputSession _session;
    private readonly Func<VoiceDraftAnchor> _captureDraft;
    private readonly Func<VoiceDraftResult, bool> _accept;
    private readonly Button _record = new() { Content = "语音输入" };
    private readonly Button _cancel = new() { Content = "取消录音" };
    private readonly Button _insert = new() { Content = "加入草稿", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly StackPanel _result = new() { Spacing = 8 };
    private VoiceDraftResult? _accepted;
    private bool _subscribed, _disposed;

    public VoiceInputControl(VoiceInputSession session, Func<VoiceDraftAnchor> captureDraft, Func<VoiceDraftResult, bool> accept)
    {
        _session = session; _captureDraft = captureDraft; _accept = accept;
        var panel = new StackPanel { Spacing = 8 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_record); actions.Children.Add(_cancel);
        _result.Children.Add(_text); _result.Children.Add(_insert);
        panel.Children.Add(actions); panel.Children.Add(_status); panel.Children.Add(_result); Content = panel;
        AutomationProperties.SetName(_text, "语音转写结果");
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _record.Click += async (_, _) => await ToggleAsync();
        _cancel.Click += (_, _) => _session.Cancel();
        _insert.Click += (_, _) => AcceptResult();
        Loaded += (_, _) => { if (!_disposed && !_subscribed) { _session.Changed += OnChanged; _subscribed = true; Refresh(); } };
        Unloaded += (_, _) => Detach();
        Refresh();
    }

    public async Task ToggleAsync()
    {
        if (_disposed) return;
        if (_session.State.Phase == VoiceInputPhase.Recording) { _session.FinishRecording(); return; }
        if (_session.State.Phase is not (VoiceInputPhase.Idle or VoiceInputPhase.Completed or VoiceInputPhase.Failed)) return;
        try { await _session.StartAsync(_captureDraft()); }
        catch (InvalidOperationException) { _status.Text = "请先选择角色，并等待当前录音结束。"; _status.Visibility = Visibility.Visible; }
    }

    public bool AcceptResult()
    {
        if (_disposed || _session.State.Result is not { } result || ReferenceEquals(_accepted, result)) return false;
        if (!_accept(result))
        {
            _status.Text = "角色或草稿已变化，请复制转写文本后按需粘贴。";
            _status.Visibility = Visibility.Visible; return false;
        }
        _accepted = result; Refresh(); return true;
    }

    private void OnChanged(object? sender, EventArgs args) => Refresh();
    private void Refresh()
    {
        var state = _session.State;
        _record.Content = state.Phase switch { VoiceInputPhase.Recording => "结束录音", VoiceInputPhase.Failed => "重新录音", _ => "语音输入" };
        _record.IsEnabled = !_disposed && state.Phase is VoiceInputPhase.Idle or VoiceInputPhase.Recording or VoiceInputPhase.Completed or VoiceInputPhase.Failed;
        _cancel.Visibility = state.Phase is VoiceInputPhase.Opening or VoiceInputPhase.Recording or VoiceInputPhase.Finalizing or VoiceInputPhase.Transcribing or VoiceInputPhase.Cancelling
            ? Visibility.Visible : Visibility.Collapsed;
        _cancel.IsEnabled = !_disposed && state.Phase != VoiceInputPhase.Cancelling;
        _cancel.Content = state.Phase == VoiceInputPhase.Transcribing ? "取消转写" : "取消录音";
        _status.Text = state.Phase switch
        {
            VoiceInputPhase.Opening => "正在打开麦克风…", VoiceInputPhase.Recording => "正在录音，最长 2 分钟。",
            VoiceInputPhase.Finalizing => "正在结束录音…", VoiceInputPhase.Transcribing => "正在转写…",
            VoiceInputPhase.Cancelling => "正在取消并释放麦克风…", VoiceInputPhase.Failed => state.Error ?? "语音输入失败。",
            VoiceInputPhase.Completed => ReferenceEquals(_accepted, state.Result) ? "已加入草稿，尚未发送。" : "转写完成，请确认后加入草稿。", _ => ""
        };
        _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
        _text.Text = state.Result?.Text ?? "";
        _result.Visibility = state.Result is null ? Visibility.Collapsed : Visibility.Visible;
        _insert.IsEnabled = !_disposed && state.Result is not null && !ReferenceEquals(_accepted, state.Result);
    }
    private void Detach()
    {
        if (_subscribed) { _session.Changed -= OnChanged; _subscribed = false; }
        _session.Cancel();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Detach(); Refresh(); }
}
