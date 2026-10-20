namespace PuddingDesktop.FoundationTests;

using PuddingDesktop.Foundation;

/// <summary>
/// 面板本地图片通道的**安全边界**契约。
///
/// 这层挡的是：Agent 生成的任意 HTML 借 <c>pudding-image://</c> 读磁盘。
/// 三条断言任何一条破了都不会让编译失败，只会让「面板能读不该读的文件」变成一个
/// 无人知道的既成事实：
/// ① 白名单按路径段比较（否则 D:\Data2 冒充 D:\Data）；
/// ② 扩展名受限（否则任意文件改名 .png 就能读出内容）；
/// ③ 非本 scheme / 相对路径一律拒绝。
/// </summary>
public sealed class PanelImageSourceTests
{
    private static readonly string[] Roots = { @"D:\Data", @"D:\Shots" };

    // ── BuildUri ────────────────────────────────────────────────

    [Fact]
    public void Builds_a_readable_uri_for_a_plain_path()
    {
        Assert.Equal(
            "pudding-image:///D:/Data/shots/dog.jpg",
            PanelImageSource.BuildUri(@"D:\Data\shots\dog.jpg"));
    }

    [Fact]
    public void Escapes_spaces_and_non_ascii_instead_of_emitting_them_raw()
    {
        var uri = PanelImageSource.BuildUri(@"D:\Data\小狗 的照片.png");

        Assert.StartsWith("pudding-image:///D:/Data/", uri);
        Assert.Contains("%20", uri);
        Assert.DoesNotContain(" ", uri);
    }

    [Fact]
    public void Round_trips_a_path_that_needs_escaping()
    {
        var original = @"D:\Data\小狗 的照片.png";

        Assert.True(PanelImageSource.TryResolve(
            PanelImageSource.BuildUri(original), Roots, out var resolved, out var rejection));

        Assert.Equal(PanelImageRejection.None, rejection);
        Assert.Equal(Path.GetFullPath(original), resolved);
    }

    [Fact]
    public void Rejects_a_relative_path()
    {
        Assert.Throws<ArgumentException>(() => PanelImageSource.BuildUri(@"shots\dog.jpg"));
    }

    // ── TryResolve：白名单 ───────────────────────────────────────

    [Fact]
    public void Accepts_a_file_under_an_allowed_root()
    {
        Assert.True(PanelImageSource.TryResolve(
            "pudding-image:///D:/Data/vision-artifacts/a.jpg", Roots, out var path, out _));

        Assert.Equal(@"D:\Data\vision-artifacts\a.jpg", path);
    }

    [Fact]
    public void Rejects_a_file_outside_every_allowed_root()
    {
        Assert.False(PanelImageSource.TryResolve(
            "pudding-image:///C:/Windows/System32/secret.png", Roots, out _, out var rejection));

        Assert.Equal(PanelImageRejection.OutsideAllowedRoots, rejection);
    }

    [Fact]
    public void Does_not_let_a_sibling_directory_ride_on_the_root_prefix()
    {
        // 字符串前缀比较会把 "D:\Data2" 当成 "D:\Data" 之下 —— 这是最典型的白名单绕过。
        Assert.False(PanelImageSource.TryResolve(
            "pudding-image:///D:/Data2/leak.jpg", Roots, out _, out var rejection));

        Assert.Equal(PanelImageRejection.OutsideAllowedRoots, rejection);
    }

    [Fact]
    public void Rejects_a_traversal_that_climbs_out_of_the_root()
    {
        // Uri 会把 ../ 规范化掉，所以这里验证的是「规范化之后确实在白名单外」。
        Assert.False(PanelImageSource.TryResolve(
            "pudding-image:///D:/Data/../Windows/System32/secret.png", Roots, out _, out var rejection));

        Assert.Equal(PanelImageRejection.OutsideAllowedRoots, rejection);
    }

    // ── TryResolve：其它拒绝路径 ─────────────────────────────────

    [Fact]
    public void Rejects_an_unsupported_extension()
    {
        // 改名成 .png 就能随便读的思路在这里被挡住。
        Assert.False(PanelImageSource.TryResolve(
            "pudding-image:///D:/Data/id_rsa.txt", Roots, out _, out var rejection));

        Assert.Equal(PanelImageRejection.UnsupportedExtension, rejection);
    }

    [Fact]
    public void Rejects_a_different_scheme()
    {
        Assert.False(PanelImageSource.TryResolve(
            "file:///D:/Data/a.jpg", Roots, out _, out var rejection));

        Assert.Equal(PanelImageRejection.MalformedUri, rejection);
    }

    [Fact]
    public void Rejects_a_relative_path_passed_as_uri()
    {
        Assert.False(PanelImageSource.TryResolve("pudding-image:///a.jpg", Roots, out _, out var rejection));

        Assert.Equal(PanelImageRejection.MalformedUri, rejection);
    }

    [Fact]
    public void Rejects_null_or_empty_input()
    {
        Assert.False(PanelImageSource.TryResolve(null, Roots, out _, out var r1));
        Assert.Equal(PanelImageRejection.MalformedUri, r1);

        Assert.False(PanelImageSource.TryResolve("", Roots, out _, out var r2));
        Assert.Equal(PanelImageRejection.MalformedUri, r2);
    }

    [Fact]
    public void Rejects_everything_when_no_root_is_allowed()
    {
        // 空白名单必须是「什么都不给」，不能退化成「什么都不限」。
        Assert.False(PanelImageSource.TryResolve(
            "pudding-image:///D:/Data/a.jpg", Array.Empty<string>(), out _, out var rejection));

        Assert.Equal(PanelImageRejection.OutsideAllowedRoots, rejection);
    }

    [Fact]
    public void Ignores_blank_roots_instead_of_treating_them_as_a_match()
    {
        Assert.False(PanelImageSource.TryResolve(
            "pudding-image:///D:/Data/a.jpg", new[] { "", "   " }, out _, out var rejection));

        Assert.Equal(PanelImageRejection.OutsideAllowedRoots, rejection);
    }

    [Fact]
    public void Accepts_a_path_that_is_inside_the_root_with_trailing_separator()
    {
        Assert.True(PanelImageSource.TryResolve(
            "pudding-image:///D:/Shots/a.webp", new[] { @"D:\Shots\" }, out var path, out _));

        Assert.Equal(@"D:\Shots\a.webp", path);
    }

    [Fact]
    public void Rejects_a_UNC_path_instead_of_building_an_unusable_uri()
    {
        // \\nas\photos\dog.jpg 的 file URI 是 file://nas/photos/dog.jpg，
        // 不以 file:/// 开头。直接拼出来会得到
        // pudding-image:///file://nas/photos/dog.jpg —— 既还不了原，也不报错，
        // 面板上只会出现一张坏图。宁可在构造时 fail closed。
        Assert.Throws<ArgumentException>(() => PanelImageSource.BuildUri(@"\\nas\photos\dog.jpg"));
    }
}
