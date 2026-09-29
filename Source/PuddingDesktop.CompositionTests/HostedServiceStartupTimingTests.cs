using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PuddingHost.Hosting;

namespace PuddingDesktop.CompositionTests;

/// <summary>
/// hosted service 计时装饰器必须"不改变被测对象"：没有证据 sink 时连描述符都不许换；
/// 有 sink 时只多一条阶段记录，注册顺序、异常与停止转发都照旧。
/// </summary>
public sealed class HostedServiceStartupTimingTests
{
    [Fact]
    public void WithoutASinkNoDescriptorIsReplaced()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostedService, FakeService>();
        var before = services.ToArray();

        HostedServiceStartupTiming.WrapWhenEvidenceRequested(services);

        Assert.Equal(before.Length, services.Count);
        for (var index = 0; index < before.Length; index++) Assert.Same(before[index], services[index]);
    }

    [Fact]
    public async Task WithASinkEachServiceReportsItsOwnPhase()
    {
        var sink = new FakeSink();
        var services = new ServiceCollection();
        services.AddSingleton<IStartupPhaseSink>(sink);
        services.AddSingleton<IHostedService, FakeService>();
        HostedServiceStartupTiming.WrapWhenEvidenceRequested(services);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken.None);

        var phase = Assert.Single(sink.Phases);
        Assert.Equal("host.start.FakeService", phase.Name);
        Assert.Equal("completed", phase.Settlement);
    }

    [Fact]
    public async Task RegistrationOrderIsPreserved()
    {
        var order = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IStartupPhaseSink>(new FakeSink());
        services.AddSingleton(order);
        services.AddSingleton<IHostedService, FirstService>();
        services.AddSingleton<IHostedService, SecondService>();
        HostedServiceStartupTiming.WrapWhenEvidenceRequested(services);
        await using var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public async Task ThrowingServiceIsReportedAsAbortedAndTheExceptionSurfaces()
    {
        var sink = new FakeSink();
        var services = new ServiceCollection();
        services.AddSingleton<IStartupPhaseSink>(sink);
        services.AddSingleton<IHostedService, ThrowingService>();
        HostedServiceStartupTiming.WrapWhenEvidenceRequested(services);
        await using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken.None));

        var phase = Assert.Single(sink.Phases);
        Assert.Equal("host.start.ThrowingService", phase.Name);
        Assert.Equal("disposed", phase.Settlement);
    }

    [Fact]
    public async Task ServiceRegisteredAsAnInstanceIsStillWrapped()
    {
        var inner = new FakeService();
        var services = new ServiceCollection();
        services.AddSingleton<IStartupPhaseSink>(new FakeSink());
        services.AddSingleton<IHostedService>(inner);
        HostedServiceStartupTiming.WrapWhenEvidenceRequested(services);
        await using var provider = services.BuildServiceProvider();

        var hosted = Assert.Single(provider.GetServices<IHostedService>());
        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal(1, inner.Starts);
        Assert.Equal(1, inner.Stops);
    }

    private class FakeService : IHostedService
    {
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) { Starts++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { Stops++; return Task.CompletedTask; }
    }

    private sealed class FirstService(List<string> order) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) { order.Add("first"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SecondService(List<string> order) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) { order.Add("second"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSink : IStartupPhaseSink
    {
        public List<(string Name, string Settlement)> Phases { get; } = [];
        public IStartupPhaseScope Phase(string name) => new Scope(name, Phases);
        public void Metric(string name, long value) { }

        private sealed class Scope(string name, List<(string, string)> phases) : IStartupPhaseScope
        {
            private int _settled;
            public void Complete(string? detail = null) => Settle("completed");
            public void Skip(string reason) => Settle("skipped");
            public void Dispose() => Settle("disposed");
            private void Settle(string settlement)
            {
                if (Interlocked.Exchange(ref _settled, 1) != 0) return;
                phases.Add((name, settlement));
            }
        }
    }
}
