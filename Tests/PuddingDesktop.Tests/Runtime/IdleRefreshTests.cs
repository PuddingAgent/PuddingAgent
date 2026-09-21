using PuddingDesktop.Core;
using PuddingDesktop.Hosting;
using PuddingDesktop.ViewModels;

namespace PuddingDesktop.Tests.Runtime;

public sealed class IdleRefreshTests
{
    [Fact]
    public async Task UnchangedRefresh_DoesNotInvalidateStatusOrLogBindings()
    {
        await using var coordinator = new DesktopApplicationCoordinator();
        using var model = new RuntimeCenterViewModel(coordinator);
        var changes = new List<string?>();
        model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        for (var i = 0; i < 10; i++)
            model.RefreshTransient();

        Assert.Empty(changes);
        coordinator.CoreLogBuffer.Append("new output");
        model.RefreshTransient();
        Assert.Equal(new[] { nameof(model.CoreLogText) }, changes);
        Assert.Equal("new output", model.CoreLogText);
    }

    [Fact]
    public void LogTail_ReusesUnchangedSnapshotAndInvalidatesAfterAppend()
    {
        var buffer = new CoreProcessLogBuffer(3);
        buffer.Append("one");
        buffer.Append("two");
        var before = buffer.GetTail(3);
        Assert.Same(before, buffer.GetTail(3));
        buffer.Append("three");
        buffer.Append("four");
        Assert.Equal(string.Join(Environment.NewLine, "two", "three", "four"), buffer.GetTail(3));
        Assert.Equal("four", buffer.GetTail(1));
        Assert.Equal(new[] { "two", "three", "four" }, buffer.Snapshot());
    }
}
