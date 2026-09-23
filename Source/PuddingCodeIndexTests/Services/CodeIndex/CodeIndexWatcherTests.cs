using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

[TestClass]
public sealed class CodeIndexWatcherTests
{
    /// <summary>§4.8 — the callback must never let an exception escape onto the watcher thread.</summary>
    [TestMethod]
    public void Watcher_Callback_Does_Not_Throw_On_Illegal_Or_Out_Of_Root_Paths()
    {
        using var temp = new TestDirectory();
        var state = CodeIndexChangeTestFactory.CreateState();
        var queue = new CodeIndexChangeQueue(64);
        using var watcher = new CodeIndexWatcher(CodeIndexChangeTestFactory.CreateScope(temp.Root), queue, state);

        // (1) Escape through ".." — must be rejected, not published.
        Assert.IsFalse(watcher.HandleCreated(Args(temp.Root, @"..\..\escape.cs")));

        // (2) A directory outside the scope root (real watchers only ever supply a leaf name).
        Assert.IsFalse(watcher.HandleCreated(Args(Path.GetTempPath(), "u3a-outside.cs")));

        // (3) The scope root itself — a scope-lifecycle event, not a path-level change.
        Assert.IsFalse(watcher.HandleChanged(Args(temp.Root, string.Empty)));
        Assert.IsFalse(watcher.HandleDeleted(Args(temp.Root, ".")));

        Assert.AreEqual(0, queue.Depth, "rejected observations must never reach the queue");
        Assert.AreEqual(0, state.ObservedVersion, "rejected observations must not allocate a sequence");
        Assert.IsFalse(state.Dirty);

        // (4) Names that may or may not be rejected by path normalisation must still never throw.
        AssertNoThrow(() => watcher.HandleCreated(Args(temp.Root, "bad\u0000name.cs")));
        AssertNoThrow(() => watcher.HandleDeleted(Args(temp.Root, new string('a', 40000))));
        AssertNoThrow(() => watcher.HandleRenamed(new RenamedEventArgs(
            WatcherChangeTypes.Renamed, temp.Root, @"..\..\new.cs", @"..\..\old.cs")));

        // (5) Watcher errors, including the internal-buffer overflow Windows reports through Error.
        AssertNoThrow(() => watcher.HandleWatcherError(new ErrorEventArgs(new IOException("watcher blew up"))));
        AssertNoThrow(() => watcher.HandleWatcherError(new ErrorEventArgs(new InternalBufferOverflowException("buffer overflow"))));

        Assert.IsTrue(state.NeedsReconcile, "a watcher error must never leave the watcher silently degraded");
        Assert.AreEqual(CodeIndexScopeState.ReconcileReasons.WatcherError, state.ReconcileReason);
        Assert.AreEqual(2, watcher.WatcherErrorCount);
        Assert.AreEqual(1, state.OverflowCount, "the internal buffer overflow must be counted as an overflow");
    }

    /// <summary>A rename whose source is outside the root keeps the reindex and drops the removal.</summary>
    [TestMethod]
    public void Watcher_Rename_With_Out_Of_Root_Source_Keeps_Reindex_And_Reports_No_Old_Path()
    {
        using var temp = new TestDirectory();
        var state = CodeIndexChangeTestFactory.CreateState();
        var queue = new CodeIndexChangeQueue(64);
        using var watcher = new CodeIndexWatcher(CodeIndexChangeTestFactory.CreateScope(temp.Root), queue, state);

        Assert.IsTrue(watcher.HandleRenamed(new RenamedEventArgs(
            WatcherChangeTypes.Renamed, temp.Root, "moved-in.cs", @"..\..\somewhere-else.cs")));

        Assert.IsTrue(queue.TryRead(out var change));
        Assert.IsNotNull(change);
        Assert.AreEqual(IndexChangeKind.Renamed, change!.Kind);
        Assert.IsNull(change.OldFullPath, "an out-of-root old path must never be recorded");
        Assert.AreEqual(temp.Combine("moved-in.cs"), change.FullPath);
        Assert.AreEqual(1, change.Sequence);
    }

    /// <summary>§4.9 — build output, dependency and index-internal directories are filtered per event.</summary>
    [TestMethod]
    public void Watcher_Filters_Noise_Directories_Before_Publishing()
    {
        using var temp = new TestDirectory();

        // Created before the watcher starts so the real watcher cannot add unrelated events.
        Directory.CreateDirectory(temp.Combine("bin"));
        Directory.CreateDirectory(temp.Combine("src/obj"));
        Directory.CreateDirectory(temp.Combine("src/node_modules"));
        Directory.CreateDirectory(temp.Combine(".pudding-code"));
        Directory.CreateDirectory(temp.Combine(".git"));

        var state = CodeIndexChangeTestFactory.CreateState();
        var queue = new CodeIndexChangeQueue(128);
        using var watcher = new CodeIndexWatcher(CodeIndexChangeTestFactory.CreateScope(temp.Root), queue, state);

        // Forged events only: the filter logic is what is under test here.
        watcher.Stop();

        Assert.IsFalse(watcher.HandleCreated(Args(temp.Combine("bin"), "app.dll")));
        Assert.IsFalse(watcher.HandleChanged(Args(temp.Combine("src/obj"), "generated.cs")));
        Assert.IsFalse(watcher.HandleDeleted(Args(temp.Combine("src/node_modules"), "package.js")));
        Assert.IsFalse(watcher.HandleCreated(Args(temp.Root, Path.Combine(".pudding-code", "index.db"))));
        Assert.IsFalse(watcher.HandleCreated(Args(temp.Combine(".git"), "HEAD")));

        Assert.AreEqual(0, queue.Depth, "noise directories must never reach the queue");
        Assert.AreEqual(0, state.ObservedVersion, "a filtered observation must not allocate a sequence");

        // A real source file inside the scope is published as usual.
        Assert.IsTrue(watcher.HandleCreated(Args(temp.Combine("src"), "program.cs")));

        Assert.AreEqual(1, queue.Depth);
        Assert.AreEqual(1, state.ObservedVersion);
        Assert.IsTrue(state.Dirty);

        Assert.IsTrue(queue.TryRead(out var change));
        Assert.IsNotNull(change);
        Assert.AreEqual(IndexChangeKind.Created, change!.Kind);
        Assert.AreEqual(temp.Combine("src/program.cs"), change.FullPath);
        Assert.AreEqual(1, change.Sequence);
    }

    /// <summary>Filtering uses the path relative to the root, so a scope below a "build" directory still works.</summary>
    [TestMethod]
    public void Watcher_Indexes_Files_When_The_Scope_Root_Lives_Under_A_Noise_Named_Directory()
    {
        using var temp = new TestDirectory();
        var projectRoot = temp.Combine("build/project");
        Directory.CreateDirectory(projectRoot);

        var state = CodeIndexChangeTestFactory.CreateState();
        var queue = new CodeIndexChangeQueue(64);
        using var watcher = new CodeIndexWatcher(CodeIndexChangeTestFactory.CreateScope(projectRoot), queue, state);
        watcher.Stop();

        Assert.IsTrue(watcher.HandleCreated(Args(projectRoot, "Program.cs")),
            "the scope root's own ancestors must not poison every event");

        Assert.AreEqual(1, queue.Depth);
        Assert.IsFalse(watcher.HandleCreated(Args(projectRoot, Path.Combine("obj", "Program.g.cs"))));
        Assert.AreEqual(1, queue.Depth);
    }

    /// <summary>§4.10 — Dispose is idempotent and never throws.</summary>
    [TestMethod]
    public void Watcher_Dispose_Is_Idempotent_And_Does_Not_Throw()
    {
        using var temp = new TestDirectory();
        var state = CodeIndexChangeTestFactory.CreateState();
        var queue = new CodeIndexChangeQueue(8);
        var watcher = new CodeIndexWatcher(CodeIndexChangeTestFactory.CreateScope(temp.Root), queue, state);

        AssertNoThrow(watcher.Dispose);
        AssertNoThrow(watcher.Dispose);
        AssertNoThrow(watcher.Stop);
        AssertNoThrow(watcher.Start);
        AssertNoThrow(watcher.Dispose);

        // Callbacks arriving after disposal are ignored instead of throwing.
        Assert.IsFalse(watcher.HandleCreated(Args(temp.Root, "after-dispose.cs")));
        Assert.IsFalse(watcher.HandleWatcherError(new ErrorEventArgs(new IOException("after dispose"))));
        Assert.AreEqual(0, queue.Depth);
    }

    [TestMethod]
    public void Watcher_Requires_An_Existing_Scope_Root()
    {
        using var temp = new TestDirectory();
        var missingRoot = temp.Combine("does-not-exist");

        var threw = false;
        try
        {
            _ = new CodeIndexWatcher(
                CodeIndexChangeTestFactory.CreateScope(missingRoot),
                new CodeIndexChangeQueue(4),
                CodeIndexChangeTestFactory.CreateState());
        }
        catch (DirectoryNotFoundException)
        {
            threw = true;
        }

        Assert.IsTrue(threw);
    }

    [TestMethod]
    public void Watcher_Uses_The_Large_Internal_Buffer_And_The_Required_Filter()
    {
        using var temp = new TestDirectory();
        using var watcher = new CodeIndexWatcher(
            CodeIndexChangeTestFactory.CreateScope(temp.Root),
            new CodeIndexChangeQueue(4),
            CodeIndexChangeTestFactory.CreateState());

        Assert.AreEqual(64 * 1024, watcher.BufferSizeBytes);
        Assert.AreEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(temp.Root)), watcher.RootPath);
    }

    /// <summary>End-to-end sanity check with real Windows file-system events.</summary>
    [TestMethod]
    public async Task Watcher_Delivers_Real_Events_And_Drops_Noise_Subdirectories()
    {
        using var temp = new TestDirectory();
        Directory.CreateDirectory(temp.Combine("src"));
        Directory.CreateDirectory(temp.Combine("bin"));

        var state = CodeIndexChangeTestFactory.CreateState();
        var queue = new CodeIndexChangeQueue(4096);
        using var watcher = new CodeIndexWatcher(CodeIndexChangeTestFactory.CreateScope(temp.Root), queue, state);

        // Give the watcher thread a moment to start before generating real events.
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        var noisePath = temp.Combine("bin/noise.dll");
        var sourcePath = temp.Combine("src/live.cs");
        await File.WriteAllTextAsync(noisePath, "noise\n");
        await File.WriteAllTextAsync(sourcePath, "class Live { }\n");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        var observed = new List<string>();
        var sawSource = false;

        while (DateTime.UtcNow < deadline && !sawSource)
        {
            while (queue.TryRead(out var change))
            {
                if (change is null)
                    continue;

                observed.Add(change.FullPath);

                if (string.Equals(change.FullPath, sourcePath, StringComparison.OrdinalIgnoreCase))
                    sawSource = true;
            }

            if (!sawSource)
                await Task.Delay(50);
        }

        Assert.IsTrue(sawSource, $"no real watcher event for '{sourcePath}' within 30 s (observed: {string.Join(", ", observed)})");
        Assert.IsFalse(
            observed.Any(path => string.Equals(path, noisePath, StringComparison.OrdinalIgnoreCase)),
            $"a build-output path must never be captured, but observed: {string.Join(", ", observed)}");
    }

    private static FileSystemEventArgs Args(string directory, string name) =>
        new(WatcherChangeTypes.Created, directory, name);

    private static void AssertNoThrow(Action action) => action();
}
