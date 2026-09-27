using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PuddingChat.WinUI;

/// <summary>A message-scoped action attached to the workspace's shared playback lane.</summary>
public sealed class SpeechPlaybackButton : UserControl, IDisposable
{
    private readonly SpeechPlaybackSession _session;
    private SpeechRequest _request;
    private readonly Button _action = new() { Content = "朗读", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, MaxWidth = 360 };
    private bool _subscribed, _disposed;
    public SpeechPlaybackButton(SpeechPlaybackSession session, SpeechRequest request)
    {
        _session = session; _request = request;
        var panel = new StackPanel { Spacing = 6 }; panel.Children.Add(_action); panel.Children.Add(_error); Content = panel;
        _action.Click += async (_, _) => await ToggleAsync();
        Loaded += (_, _) => { if (!_disposed && !_subscribed) { _session.Changed += OnChanged; _subscribed = true; Refresh(); } };
        Unloaded += (_, _) => Detach();
        Refresh();
    }
    private bool OwnsCurrent => _session.State.Request is { } request && request.Role == _request.Role && request.MessageId == _request.MessageId;
    public void Update(SpeechRequest request)
    {
        if (_disposed) return;
        if ((request.Role != _request.Role || request.MessageId != _request.MessageId) && OwnsCurrent) _session.Stop();
        _request = request; Refresh();
    }
    public async Task ToggleAsync()
    {
        if (_disposed) return;
        if (OwnsCurrent && _session.State.Phase is SpeechPlaybackPhase.Synthesizing or SpeechPlaybackPhase.Playing) { _session.Stop(); return; }
        try { await _session.SpeakAsync(_request); }
        catch (ArgumentException) { _error.Text = "没有可朗读的文本，或内容超过 10,000 个字符。"; _error.Visibility = Visibility.Visible; }
        catch (ObjectDisposedException) { _action.IsEnabled = false; }
    }
    private void OnChanged(object? sender, EventArgs args) => Refresh();
    private void Refresh()
    {
        var phase = OwnsCurrent ? _session.State.Phase : SpeechPlaybackPhase.Idle;
        _action.Content = phase switch
        {
            SpeechPlaybackPhase.Synthesizing => "取消合成", SpeechPlaybackPhase.Playing => "停止朗读",
            SpeechPlaybackPhase.Failed => "重试朗读", _ => "朗读"
        };
        _action.IsEnabled = !_disposed && (phase is SpeechPlaybackPhase.Synthesizing or SpeechPlaybackPhase.Playing
            || !string.IsNullOrWhiteSpace(_request.Text) && _request.Text.Length <= SpeechRequest.MaxCharacters);
        ToolTipService.SetToolTip(_action, _request.Text.Length > SpeechRequest.MaxCharacters ? "内容超过 10,000 个字符，无法整段朗读。" : "朗读这条消息");
        _error.Text = phase == SpeechPlaybackPhase.Failed ? _session.State.Error ?? "朗读失败，请重试。" : "";
        _error.Visibility = string.IsNullOrEmpty(_error.Text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Detach()
    {
        if (_subscribed) { _session.Changed -= OnChanged; _subscribed = false; }
        if (OwnsCurrent && _session.State.Phase is SpeechPlaybackPhase.Synthesizing or SpeechPlaybackPhase.Playing) _session.Stop();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Detach(); _action.IsEnabled = false; }
}
