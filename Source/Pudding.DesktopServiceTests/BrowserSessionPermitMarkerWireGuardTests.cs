namespace DesktopServiceTests;

/// <summary>
/// I2-c D2 的**上线排除守卫**：`DesktopCallContext.SessionPermitMarker` 是宿主内部字段，
/// 只能由 Desktop 进程内组合根设置；协议映射/线缆编码不得引用它（否则模型或远端可伪造"已准入"）。
///
/// 破坏方式：在 proto 或任一侧 wire 映射里加入该字段/名称 ⇒ 本测试变红。
/// </summary>
public sealed class BrowserSessionPermitMarkerWireGuardTests
{
    [Fact]
    public void SessionPermitMarker_IsNeverReferencedByWireOrProtocolMapping()
    {
        var root = FindRepositoryRoot();
        var guardedFiles = new (string Path, string Role)[]
        {
            ("Source/Pudding.Rpc.Protocol/Protos/desktop_capability.proto", "wire schema"),
            ("Source/Pudding.CapabilityBroker/WireMapping.cs", "Core encoder"),
            ("Source/Pudding.DesktopConnection/Mapping.cs", "Desktop decoder"),
        };

        foreach (var (relativePath, role) in guardedFiles)
        {
            var path = Path.Combine(root.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{role} file not found: {path}");
            var content = File.ReadAllText(path);
            Assert.DoesNotContain("SessionPermitMarker", content, StringComparison.Ordinal);
            Assert.DoesNotContain("session_permit_marker", content, StringComparison.Ordinal);
            Assert.DoesNotContain("sessionPermitMarker", content, StringComparison.Ordinal);
        }
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "PuddingAgentNetwork.slnx")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return current!;
    }
}
