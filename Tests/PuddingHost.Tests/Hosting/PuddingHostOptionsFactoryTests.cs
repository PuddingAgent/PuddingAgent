using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

public sealed class PuddingHostOptionsFactoryTests
{
    [Theory]
    [InlineData(80)]
    [InlineData(8080)]
    [InlineData(65535)]
    public void ForDesktopChild_AcceptsFixedIpv4WildcardPort(int port)
    {
        var options = PuddingHostOptionsFactory.ForDesktopChild(
        [
            "--desktop-child",
            "--desktop-parent-pid", "1234",
            "--data-root", "D:\\data",
            "--urls", $"http://0.0.0.0:{port}",
        ]);

        Assert.Equal(PuddingHostMode.DesktopChild, options.Mode);
        Assert.Equal($"http://0.0.0.0:{port}", Assert.Single(options.Urls));
    }

    [Fact]
    public void ForDesktopChild_UsesFixedWildcardDefault()
    {
        var options = PuddingHostOptionsFactory.ForDesktopChild(
        [
            "--desktop-child",
            "--desktop-parent-pid", "1234",
            "--data-root", "D:\\data",
        ]);

        Assert.Equal("http://0.0.0.0:8080", Assert.Single(options.Urls));
    }

    [Theory]
    [InlineData("http://0.0.0.0:0")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://192.168.1.2:8080")]
    [InlineData("http://user@0.0.0.0:8080")]
    [InlineData("https://0.0.0.0:8080")]
    public void ForDesktopChild_RejectsDynamicOrNonWildcardAddress(string url)
    {
        Assert.Throws<InvalidOperationException>(() =>
            PuddingHostOptionsFactory.ForDesktopChild(
            [
                "--desktop-child",
                "--desktop-parent-pid", "1234",
                "--data-root", "D:\\data",
                "--urls", url,
            ]));
    }
}
