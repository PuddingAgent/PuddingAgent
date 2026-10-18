using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Execution;
using PuddingSsh.Transport;
using PuddingSshTests;
using Renci.SshNet;

var root = Path.GetFullPath("temp/test-out/ssh-review-counterexamples");
Directory.CreateDirectory(root);
var local = Path.Combine(root, "source.bin");
File.WriteAllText(local, "our-data");
var records = new List<object>();
var confirmed = 0;
void Record(string id, bool reproduced, object evidence)
{
    if (reproduced) confirmed++;
    records.Add(new { id, reproduced, evidence });
}
SshUploadPlan Up() => new(local, "/srv/upload/target", "/srv/upload/.target.tmp", "/srv/upload", SshOverwritePolicy.Fail, 1024);
SshTransferLimits Limits() => new() { LocalRoot = root, UploadRoot = "/srv/upload", DownloadRoot = "/srv/download" };
SshDownloadRequest Down(string name) => new(FakeSsh.Target(), "/srv/download/source", Path.Combine(root, name));

// R1: another uploader creates the destination after Exists but before our EXCL open.
{
    var channel = new ReviewChannel { RaceUploadCreate = true };
    await using var session = new SshSftpTransferSession(channel, "SHA256:test", "/srv/upload");
    var result = await session.UploadAsync(Up(), TimeSpan.FromSeconds(2), default);
    var deletedOtherFile = channel.Inner.ContentOf(Up().RemotePath) is null && channel.RaceTriggered;
    Record("R1_upload_deletes_race_winner", deletedOtherFile,
        new { status = result.Status.ToString(), result.ErrorCode, channel.RaceTriggered, deletedOtherFile, deleted = channel.Inner.DeletedPaths });
}

// R2: metadata reports a small regular file; content grows before OPEN/read.
{
    var channel = new ReviewChannel { ReportedLength = 1 };
    channel.Inner.Seed("/srv/download/source", new byte[4096]);
    await using var session = new SshSftpTransferSession(channel, "SHA256:test", "/srv/download");
    var path = Path.Combine(root, "grew.bin");
    var plan = new SshDownloadPlan("/srv/download/source", path, path + ".tmp", "/srv/download", SshOverwritePolicy.Overwrite, 1024);
    var result = await session.DownloadAsync(plan, TimeSpan.FromSeconds(2), default);
    Record("R2_download_exceeds_limit", result.Status == SshExecutionStatus.Completed && new FileInfo(path).Length == 4096,
        new { status = result.Status.ToString(), reportedSize = 1, limit = plan.MaxBytes, result.BytesTransferred, publishedSize = new FileInfo(path).Length });
}

// R3: capabilities are awaited without the operation lifetime or worker pool.
{
    var session = new GatedCapabilitiesSession();
    var factory = new FakeTransferFactory { OnConnect = (_, _) => Task.FromResult<ISshTransferSession>(session) };
    await using var coordinator = new SshOperationCoordinator(new FakeTransportFactory(), new FakeIdentityProvider(),
        new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(50) }, transferFactory: factory);
    var op = coordinator.DownloadAsync(FakeSsh.Host(transfer: Limits(), deadline: DateTimeOffset.UtcNow.AddMilliseconds(150)), Down("gated.bin"), default);
    await session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await Task.Delay(400);
    var blocked = !op.IsCompleted;
    var activeWorkers = coordinator.ActiveStageWorkers;
    var stop = await coordinator.StopAsync();
    session.Release.TrySetResult(new SshTransferCapabilities(true, false, true));
    var result = await op.WaitAsync(TimeSpan.FromSeconds(2));
    Record("R3_capability_wait_outlives_deadline", blocked && activeWorkers == 0,
        new { deadlineMs = 150, observedAfterMs = 400, blocked, activeWorkers, stop, returnedStatus = result.Status.ToString() });
}

// R4: a readable remote source does not grant permission to create scratch files.
{
    var channel = new ReviewChannel { DenyWrites = true };
    channel.Inner.Seed("/srv/download/source", "payload");
    var session = new SshSftpTransferSession(channel, "SHA256:test", "/srv/download");
    var factory = new FakeTransferFactory { OnConnect = (_, _) => Task.FromResult<ISshTransferSession>(session) };
    await using var coordinator = new SshOperationCoordinator(new FakeTransportFactory(), new FakeIdentityProvider(), transferFactory: factory);
    var request = Down("readonly.bin") with { Overwrite = SshOverwritePolicy.Overwrite };
    string? exception = null;
    try { _ = await coordinator.DownloadAsync(FakeSsh.Host(transfer: Limits()), request, default); }
    catch (Exception ex) { exception = ex.GetType().Name; }
    Record("R4_download_published_then_capability_exception", exception is not null && File.ReadAllText(request.LocalPath) == "payload",
        new { exception, published = File.Exists(request.LocalPath), channel.WriteAttempts });
}

// R5: cancellation wins first, actual cleanup then reports a leftover in the grace period.
{
    using var cancel = new CancellationTokenSource();
    var channel = new ReviewChannel();
    var session = new SshSftpTransferSession(channel, "SHA256:test", "/srv/upload");
    _ = await session.GetCapabilitiesAsync(default);
    channel.Inner.FailDelete = true;
    channel.OnUpload = async (_, path, _, token) =>
    {
        channel.Inner.Seed(path, "our-");
        cancel.Cancel();
        await Task.Delay(80);
        throw new OperationCanceledException(token);
    };
    var factory = new FakeTransferFactory { OnConnect = (_, _) => Task.FromResult<ISshTransferSession>(session) };
    await using var coordinator = new SshOperationCoordinator(new FakeTransportFactory(), new FakeIdentityProvider(),
        new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(500) }, transferFactory: factory);
    var result = await coordinator.UploadAsync(FakeSsh.Host(transfer: Limits()), new SshUploadRequest(FakeSsh.Target(), local, "/srv/upload/target"), cancel.Token);
    var leftOnServer = channel.Inner.ContentOf("/srv/upload/target");
    Record("R5_cancel_discards_leftover_evidence", result.ErrorCode == SshErrorCodes.Cancelled && !result.TemporaryLeftBehind && leftOnServer is not null,
        new { status = result.Status.ToString(), result.TemporaryLeftBehind, result.TemporaryPath, result.BytesTransferred, cleanup = result.CleanupState.ToString(), actualLeftOnServer = leftOnServer });
}

// R6: the real exec transport starts closing with the default budget before the coordinator passes its budget.
{
    using var client = new SshClient(new ConnectionInfo("127.0.0.1", "test", new NoneAuthenticationMethod("test")));
    using var key = RSA.Create(2048);
    var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(key.ExportRSAPrivateKeyPem()));
    var privateKey = new PrivateKeyFile(keyStream);
    var session = new SshNetTransportSession(client, FakeSsh.Target(), FakeSsh.Material(), privateKey, keyStream, "SHA256:test", "ssh-ed25519");
    var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };
    await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(),
        new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(50) });
    var result = await coordinator.TestAsync(FakeSsh.Host(), FakeSsh.Test(), default);
    Record("R6_real_exec_close_ignores_narrower_budget", session.CloseBudget == TimeSpan.FromSeconds(2),
        new { configuredCleanupMs = 50, actualSessionBudgetMs = session.CloseBudget.TotalMilliseconds, status = result.Status.ToString() });
}

var json = JsonSerializer.Serialize(new { confirmed, count = records.Count, cases = records }, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(root, "results.json"), json);
Console.WriteLine(json);
return confirmed == records.Count ? 0 : 1;

sealed class ReviewChannel : ISshSftpChannel
{
    public FakeSftpChannel Inner { get; } = new();
    public bool RaceUploadCreate { get; init; }
    public bool RaceTriggered { get; private set; }
    public bool DenyWrites { get; init; }
    public int WriteAttempts { get; private set; }
    public long? ReportedLength { get; init; }
    public Func<Stream, string, long, CancellationToken, Task>? OnUpload { get; set; }
    public bool IsConnected => Inner.IsConnected;
    public bool Exists(string path) => Inner.Exists(path);
    public bool IsRegularFile(string path) => Inner.IsRegularFile(path);
    public long GetLength(string path) => ReportedLength ?? Inner.GetLength(path);
    public string GetRealDirectory(string path) => Inner.GetRealDirectory(path);
    public Stream OpenRead(string path) => Inner.OpenRead(path);
    public Stream OpenWriteNew(string path)
    {
        WriteAttempts++;
        if (DenyWrites) throw new UnauthorizedAccessException("remote scratch directory is read-only");
        return Inner.OpenWriteNew(path);
    }
    public Task UploadAsync(Stream source, string path, long maxBytes, CancellationToken token)
    {
        if (OnUpload is not null) return OnUpload(source, path, maxBytes, token);
        if (RaceUploadCreate)
        {
            Inner.Seed(path, "other-client-data");
            RaceTriggered = true;
            using var stream = Inner.OpenWriteNew(path); // faithful EXCL failure: this operation never owned the target
        }
        return Inner.UploadAsync(source, path, maxBytes, token);
    }
    public Task DownloadAsync(string path, Stream target, CancellationToken token) => Inner.DownloadAsync(path, target, token);
    public void Rename(string from, string to) => Inner.Rename(from, to);
    public void RenamePosix(string from, string to) => Inner.RenamePosix(from, to);
    public void Delete(string path) => Inner.Delete(path);
    public void Dispose() => Inner.Dispose();
}

sealed class GatedCapabilitiesSession : ISshTransferSession
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<SshTransferCapabilities> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string VerifiedHostKeySha256 => "SHA256:test";
    public bool Closed { get; private set; }
    public void RequestAbort() { }
    public Task<SshTransferCapabilities> GetCapabilitiesAsync(CancellationToken token) { Entered.TrySetResult(); return Release.Task; }
    public Task<SshTransferOutcome> UploadAsync(SshUploadPlan plan, TimeSpan timeout, CancellationToken token) => Task.FromResult(SshTransferOutcome.Completed(7));
    public Task<SshTransferOutcome> DownloadAsync(SshDownloadPlan plan, TimeSpan timeout, CancellationToken token) => Task.FromResult(SshTransferOutcome.Completed(7));
    public ValueTask DisposeAsync(TimeSpan budget) { Closed = true; return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() => DisposeAsync(TimeSpan.Zero);
}
