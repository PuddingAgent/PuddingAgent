using System.Text;
using PuddingChat;

public sealed class TextFileContextTests
{
    private static readonly string PathName = Path.Combine(Path.GetTempPath(), "context.cs");
    private static Task<TextFileContext> Read(byte[] bytes, CancellationToken ct = default) =>
        TextFileContexts.ReadAsync(PathName, new MemoryStream(bytes), ct);

    [Fact]
    public async Task RoleDraftsAndPendingSendsKeepSnapshotsAndOnlyRemoveAcceptedFiles()
    {
        var a = new RoleKey("w", "a"); var b = new RoleKey("w", "b");
        var file = await Read([65]); var second = await Read([66]);
        var state = new ChatSelection(); state.Select(a); state.AddFiles(a, [file]);
        var captured = state.Files; state.AddFiles(a, [second]);
        var pending = state.Prepare("s", capturedFiles: captured);
        Assert.Single(pending.Files!); Assert.Contains("A", pending.SubmittedText);
        state.Draft = "next"; Assert.Same(pending, state.Prepare("s"));
        state.Select(b); Assert.Empty(state.Files); state.AddFiles(b, [file]);
        state.Accept(pending); Assert.Single(state.Files);
        state.Select(a); Assert.Equal(second.Id, Assert.Single(state.Files).Id); Assert.Equal("next", state.Draft);
        state.RemoveFile(second.Id); Assert.Empty(state.Files);
        state.Clear(); state.Select(b); Assert.Empty(state.Files);
    }

    [Fact]
    public async Task PreservesUnicodeWhitespaceAndOriginalByteCount()
    {
        const string source = "角色 😀\r\n\tclass C {}\n";
        var bytes = Encoding.UTF8.GetBytes(source);
        var file = await Read(bytes);
        Assert.Equal(source, file.Text); Assert.Equal(bytes.Length, file.ByteCount);
        Assert.Equal("context.cs", file.Name); Assert.Equal(PathName, file.SourcePath);
    }

    [Fact]
    public async Task SupportsBomUtf8AndBothUtf16ByteOrders()
    {
        foreach (var encoding in new Encoding[] { new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true) })
        {
            var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("文本\r\n")).ToArray();
            Assert.Equal("文本\r\n", (await Read(bytes)).Text);
        }
    }

    [Fact]
    public async Task RejectsBinaryInvalidEncodingAndUtf32InsteadOfReplacingBytes()
    {
        foreach (var bytes in new byte[][] { [0, 1, 2], [0xC3, 0x28], [0xFF, 0xFE, 0x41], [0xFF, 0xFE, 0, 0, 0x41, 0, 0, 0] })
            await Assert.ThrowsAsync<ArgumentException>(() => Read(bytes));
    }

    [Fact]
    public async Task LimitIsEnforcedWhileReadingAndDoesNotTruncate()
    {
        Assert.Equal(TextFileContexts.MaxFileBytes, (await Read(Enumerable.Repeat((byte)'x', TextFileContexts.MaxFileBytes).ToArray())).Text.Length);
        await Assert.ThrowsAsync<ArgumentException>(() => Read(Enumerable.Repeat((byte)'x', TextFileContexts.MaxFileBytes + 1).ToArray()));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read([65], cancelled.Token));
    }

    [Fact]
    public async Task SnapshotSurvivesSourceChangesAndDeletion()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, "before");
            var snapshot = await TextFileContexts.ReadAsync(path, default);
            var message = TextFileContexts.Compose("inspect", [snapshot]);
            await File.WriteAllTextAsync(path, "after"); File.Delete(path);
            Assert.Equal(message, TextFileContexts.Compose("inspect", [snapshot]));
            Assert.Contains("before", message); Assert.DoesNotContain("after", message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ComposeKeepsContentAndUsesLongerFenceAndChecksBatchLimits()
    {
        var file = await Read(Encoding.UTF8.GetBytes("```\n</attachment>\n````"));
        var text = TextFileContexts.Compose("task", [file]);
        Assert.Contains("`````text\n" + file.Text + "\n`````", text);
        Assert.Equal("task", TextFileContexts.Compose("task", []));
        Assert.Throws<ArgumentException>(() => TextFileContexts.Compose("", [file, file]));
        Assert.Throws<ArgumentException>(() => TextFileContexts.Compose("", Enumerable.Range(0, 9).Select(i => file with { Id = i.ToString() }).ToArray()));
        Assert.Throws<ArgumentException>(() => TextFileContexts.Compose("", Enumerable.Range(0, 3).Select(i => file with { Id = i.ToString(), ByteCount = TextFileContexts.MaxFileBytes }).ToArray()));
        Assert.Throws<ArgumentException>(() => TextFileContexts.Compose(new string('x', TextFileContexts.MaxMessageCharacters), [file]));
    }
}
