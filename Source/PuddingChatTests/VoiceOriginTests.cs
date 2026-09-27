using PuddingChat;
namespace PuddingChatTests;

public class VoiceOriginTests
{
    private static ChatSelection Create()
    { var state = new ChatSelection(); state.Select(new("workspace", "author")); state.Draft = "spoken"; state.SetVoiceOrigin(new("recording", "provider", "model")); return state; }
    [Fact]
    public void OriginIsRoleScopedAndClearingDraftRemovesIt()
    {
        var state = Create(); state.Select(new("workspace", "reviewer")); Assert.Null(state.VoiceOrigin);
        state.Select(new("workspace", "author")); Assert.Equal("recording", state.VoiceOrigin!.SessionId);
        state.Draft = ""; state.Draft = "typed"; Assert.Null(state.VoiceOrigin);
    }
    [Fact]
    public void RetryPreservesOriginalVoiceSnapshotAndReceiptDoesNotClearNewRecording()
    {
        var state = Create(); var pending = state.Prepare("session"); state.SetVoiceOrigin(new("new-recording", "other", "new-model"));
        Assert.Same(pending, state.Prepare("session")); Assert.Equal("provider", pending.VoiceOrigin!.Provider);
        state.Accept(pending); Assert.Equal("spoken", state.Draft); Assert.Equal("new-recording", state.VoiceOrigin!.SessionId);
        var next = state.Prepare("session"); state.Accept(next); Assert.Equal("", state.Draft); Assert.Null(state.VoiceOrigin);
    }
    [Fact]
    public void CapturedTypedDraftDoesNotAcquireLaterVoiceMetadata()
    {
        var state = Create(); state.Draft = ""; state.Draft = "typed"; var text = state.Draft;
        state.SetVoiceOrigin(new("later"));
        Assert.Null(state.Prepare("session", capturedDraft: text, capturedVoice: null).VoiceOrigin);
    }
    [Fact]
    public void UnknownProviderAndLanguageAreOmittedInsteadOfInvented()
    {
        var metadata = new VoiceInputOrigin("recording").ToMetadata();
        Assert.Equal(2, metadata.Count); Assert.Equal("voice", metadata["inputMode"]); Assert.Equal("recording", metadata["voiceSessionId"]);
    }
}
