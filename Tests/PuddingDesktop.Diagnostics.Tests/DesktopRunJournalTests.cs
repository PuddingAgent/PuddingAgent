using System.Text.Json;
using PuddingDesktop.Diagnostics;
using Xunit;

namespace PuddingDesktop.Diagnostics.Tests;

public sealed class DesktopRunJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pudding-exit-test-" + Guid.NewGuid().ToString("N"));
    private DesktopRunJournal NewRun() => new(_root, new(123, new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc)), "test");

    [Theory]
    [InlineData(false, false, null, "unobserved_termination")]
    [InlineData(true, false, 0, "uncoordinated_process_exit")]
    [InlineData(true, true, 0, "clean_shutdown")]
    [InlineData(false, true, -1073741819, "abnormal_exit_code")]
    public void ClassifiesEvidenceWithoutGuessingInitiator(bool callback, bool complete, int? code, string expected)
    {
        var run = NewRun();
        run.RequestExit("user_exit");
        if (callback) run.ProcessExit();
        if (complete) run.CompleteExit();
        DesktopRunJournal.RecordObservedExit(run.DirectoryPath, code, "observer");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.DirectoryPath, "exit.json")));
        Assert.Equal(expected, json.RootElement.GetProperty("cause").GetString());
        Assert.Equal("unknown", json.RootElement.GetProperty("terminationInitiator").GetString());
        Assert.Equal(expected != "clean_shutdown", File.Exists(Path.Combine(run.DirectoryPath, "incident.json")));
    }

    [Fact]
    public void FatalEvidenceSurvivesLaterHeartbeatAndExitCompletion()
    {
        var run = NewRun();
        run.Exception("AppDomain", new InvalidOperationException("failure"), true);
        run.Capture(new("CoreStopping", "Stopping", 234));
        run.CompleteExit();
        run.ProcessExit();
        var saved = DesktopRunJournal.ReadState(run.DirectoryPath)!;
        Assert.Contains("failure", saved.FatalException);
        Assert.Equal(234, saved.Scene.CoreProcessId);
        Assert.Equal("unhandled_exception", DesktopRunJournal.ClassifyExit(saved, 0));
    }

    [Fact]
    public void RecoveryPreservesLastSceneAndIsIdempotent()
    {
        var run = NewRun();
        run.Capture(new("WorkbenchReady", "Ready", 789, CoreLogTail: "last core line"));
        DesktopRunJournal.RecoverAndPrune(_root, _ => false);
        var path = Path.Combine(run.DirectoryPath, "incident.json");
        var first = File.ReadAllText(path);
        DesktopRunJournal.RecoverAndPrune(_root, _ => false);
        Assert.Equal(first, File.ReadAllText(path));
        Assert.Contains("last core line", first);
        Assert.Contains("next_startup", first);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void RecoveryNeverMarksActiveOrInaccessibleProcessAsDead(bool? alive)
    {
        var run = NewRun();
        DesktopRunJournal.RecoverAndPrune(_root, _ => alive);
        Assert.False(File.Exists(Path.Combine(run.DirectoryPath, "incident.json")));
    }

    [Fact]
    public void RedactsSecretsBeforePersistingAndBoundsPayload()
    {
        var run = NewRun();
        const string secret = "never-keep-this";
        var text = $"Authorization: Bearer {secret}\n\"controlToken\":\"{secret}\"\nX-Control-Token: {secret}\napi_key='{secret}'\nhttps://host/path?token={secret}\n" + new string('x', 60000);
        run.Capture(new(CoreError: text, CoreLogTail: text));
        run.Exception("UI", new Exception(text), true);
        foreach (var path in Directory.GetFiles(run.DirectoryPath)) Assert.DoesNotContain(secret, File.ReadAllText(path));
        var state = DesktopRunJournal.ReadState(run.DirectoryPath)!;
        Assert.Contains("[TRUNCATED]", state.Scene.CoreLogTail);
        Assert.True(new FileInfo(Path.Combine(run.DirectoryPath, "state.json")).Length < 128 * 1024);
    }

    [Fact]
    public void JournalRotatesAndRetentionKeepsActiveRuns()
    {
        var run = NewRun();
        for (var i = 0; i < 60; i++) run.Record("test", new string('x', 16000));
        Assert.True(new FileInfo(Path.Combine(run.DirectoryPath, "lifecycle.jsonl")).Length < 280 * 1024);
        Assert.True(File.Exists(Path.Combine(run.DirectoryPath, "lifecycle.jsonl.previous")));
        NewRun(); NewRun();
        DesktopRunJournal.RecoverAndPrune(_root, _ => false, retain: 2);
        Assert.Equal(2, Directory.GetDirectories(_root).Length);
    }

    [Fact]
    public void WriteFailureDoesNotEscapeIntoProduct()
    {
        Directory.CreateDirectory(_root);
        var blocked = Path.Combine(_root, "file");
        File.WriteAllText(blocked, "not a directory");
        var run = new DesktopRunJournal(blocked, new(123, DateTime.UtcNow), "test");
        run.Exception("fatal", new Exception("original"), true);
        Assert.NotNull(run.LastWriteError);
        Assert.Contains("original", run.State.FatalException);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
