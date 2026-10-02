using System.Text.Json;
using Pudding.DesktopService;
using PuddingDesktop.Configuration;

namespace PuddingDesktop.Tests.Configuration;

/// <summary>
/// 能力通道在 `desktop.json` 里的文件形态（切片 C-3）。
///
/// 关键判据：
/// ① 段缺席 = 关闭（用户没配过就不该被当成配错）；
/// ② 段名与 Core 的 `system.json` 同名（`desktop.capabilityChannel`），可原样复制；
/// ③ 文件形态的缺省值**必须与组件缺省值一致**——两边各写一份就有漂移风险，
///    而漂移的表现是「什么都没配却握手失败」，最难查。
/// </summary>
public sealed class DesktopCapabilityChannelSettingsJsonTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AbsentSection_MeansNeverConfigured()
    {
        var settings = Deserialize("""{ "dataRoot": "D:\\data" }""");

        Assert.Null(settings.Desktop);
    }

    [Fact]
    public void SectionPresent_MapsEveryField()
    {
        var settings = Deserialize("""
            {
              "desktop": {
                "capabilityChannel": {
                  "enabled": true,
                  "desktopId": "workstation-a",
                  "controlTokenHeader": "X-Pudding-Desktop-Token",
                  "handshakeTimeoutSeconds": 30
                }
              }
            }
            """);

        var section = Assert.IsType<DesktopCapabilityChannelFileSettings>(settings.Desktop!.CapabilityChannel);
        Assert.True(section.Enabled);
        Assert.Equal("workstation-a", section.DesktopId);
        Assert.Equal("X-Pudding-Desktop-Token", section.ControlTokenHeader);
        Assert.Equal(30, section.HandshakeTimeoutSeconds);
    }

    [Fact]
    public void PartialSection_FallsBackToTheSameDefaultsAsTheComponent()
    {
        var settings = Deserialize("""{ "desktop": { "capabilityChannel": { "enabled": true } } }""");

        var section = settings.Desktop!.CapabilityChannel!;
        var component = new DesktopCapabilityChannelSettings();

        // 只写 enabled 就等于"其余全部采用组件缺省值"——不能各自维护一套默认。
        Assert.Equal(component.DesktopId, section.DesktopId);
        Assert.Equal(component.ControlTokenHeader, section.ControlTokenHeader);
        Assert.Equal(component.HandshakeTimeoutSeconds, section.HandshakeTimeoutSeconds);
    }

    [Fact]
    public void FileDefaults_MatchComponentDefaultsExactly()
    {
        // 跨侧一致性：文件形态的缺省值 == 组件设置的缺省值（本工程因此引用 Pudding.DesktopService）。
        var file = new DesktopCapabilityChannelFileSettings();
        var component = new DesktopCapabilityChannelSettings();

        Assert.False(file.Enabled);
        Assert.False(component.Enabled);
        Assert.Equal(component.DesktopId, file.DesktopId);
        Assert.Equal(component.ControlTokenHeader, file.ControlTokenHeader);
        Assert.Equal(component.HandshakeTimeoutSeconds, file.HandshakeTimeoutSeconds);

        // 组件的"关闭"单例也必须仍是关闭状态：它是回滚路径上的常驻值。
        Assert.False(DesktopCapabilityChannelSettings.Disabled.Enabled);
    }

    [Fact]
    public void ExplicitlyDisabledSection_IsNotTheSameAsAbsent()
    {
        // 「缺席」与「显式 false」在行为上等价（都不启用），但语义不同：
        // 前者是"从未配置"，后者是"用户明确关闭"。两者不应互相改写。
        var settings = Deserialize("""{ "desktop": { "capabilityChannel": { "enabled": false } } }""");

        Assert.NotNull(settings.Desktop);
        Assert.NotNull(settings.Desktop!.CapabilityChannel);
        Assert.False(settings.Desktop.CapabilityChannel!.Enabled);
    }

    [Fact]
    public void SectionRoundTripsThroughTheSameShapeTheStoreWrites()
    {
        // 形状对称性：写出去再读回来必须在同一层级（段名写错会让"配了却不生效"）。
        var original = new DesktopBootstrapSettings
        {
            DataRoot = @"D:\data",
            Desktop = new DesktopSectionSettings
            {
                CapabilityChannel = new DesktopCapabilityChannelFileSettings
                {
                    Enabled = true,
                    DesktopId = "default",
                },
            },
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        Assert.Contains("\"desktop\"", json, StringComparison.Ordinal);
        Assert.Contains("\"capabilityChannel\"", json, StringComparison.Ordinal);

        var restored = JsonSerializer.Deserialize<DesktopBootstrapSettings>(json, JsonOptions)!;
        Assert.True(restored.Desktop!.CapabilityChannel!.Enabled);
        Assert.Equal("default", restored.Desktop.CapabilityChannel.DesktopId);
    }

    [Fact]
    public void AbsentSection_StaysAbsentOnSave()
    {
        // 安全相关开关：用户没写过的段不应因为"保存了一次设置"就出现（可空 + 默认序列化即可区分）。
        var settings = new DesktopBootstrapSettings { DataRoot = @"D:\data" };

        var json = JsonSerializer.Serialize(settings, JsonOptions);

        Assert.Null(settings.Desktop);
        Assert.DoesNotContain("capabilityChannel", json, StringComparison.OrdinalIgnoreCase);
    }

    private static DesktopBootstrapSettings Deserialize(string json) =>
        JsonSerializer.Deserialize<DesktopBootstrapSettings>(json, JsonOptions)!;
}
