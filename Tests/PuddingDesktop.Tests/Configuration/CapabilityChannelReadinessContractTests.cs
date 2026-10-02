using System.Text.Json;
using System.Text.Json.Serialization;
using Pudding.CapabilityBroker.AspNetCore;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using PuddingDesktop.Core;

namespace PuddingDesktop.Tests.Configuration;

/// <summary>
/// Core→Desktop 就绪描述这条**字符串契约**的端到端断言。
///
/// 为什么必须有这一条：本系列的门禁核心是真实端点探针，但探针**自己构造**端点描述 ——
/// 它验证的是**解析器**，不是 Core 真正打到 stdout 的那一行。2026-10-02 因此漏过了
/// 「Core 打成 `|v1|`、Desktop 要求整数」这一缺陷：组件测试全绿、探针 53/53、编译通过，
/// 而 `Enabled=true` 时通道**永远不启动**（只在打开开关后才暴露）。
///
/// 这里把三段真实代码接起来跑：Core 侧产出函数（<see cref="CapabilityChannelReadySignal"/>）
/// → 按入口点同款方式组装就绪行 → Desktop 侧真实解析器（<see cref="CoreReadyMessageParser"/>）
/// → Desktop 侧唯一判定入口（<see cref="DesktopCapabilityChannelPreflight"/>）。
/// </summary>
public sealed class CapabilityChannelReadinessContractTests
{
    private const string PipeName = "pudding-capability-contract";

    [Fact]
    public void CoreEmittedDescription_IsParsedAndAcceptedByTheDesktopPreflight()
    {
        var endpoint = DesktopCapabilityEndpoint.NamedPipe(PipeName, DesktopProtocolVersion.Current, "core-1");

        var ready = ParseReadyLine(CapabilityChannelReadySignal.Describe(endpoint));

        Assert.Equal("named-pipe:pudding-capability-contract|1|core-1", ready.CapabilityEndpoint);

        var preflight = Evaluate(ready.CapabilityEndpoint);

        Assert.True(preflight.ShouldStart, preflight.Summary);
    }

    [Fact]
    public void ReadyLineFieldName_IsTheOneTheDesktopParserReads()
    {
        // 字段名是这条契约里唯一"靠约定"的部分：Core 侧常量与 Desktop 侧大小写不敏感读取必须对上。
        // 用一个只有该字段名不同的 JSON 反证（换成别的名字就取不到描述 ⇒ 预检不启动）。
        var description = CapabilityChannelReadySignal.Describe(
            DesktopCapabilityEndpoint.NamedPipe(PipeName, DesktopProtocolVersion.Current, "core-2"));

        var renamed = ReadyLine(
            new Dictionary<string, object?> { ["capabilityEndpointRenamed"] = description });
        var parsed = CoreReadyMessageParser.TryParse(renamed);

        Assert.NotNull(parsed);
        Assert.Null(parsed!.CapabilityEndpoint);
        Assert.False(Evaluate(parsed.CapabilityEndpoint).ShouldStart);
    }

    [Fact]
    public void DisabledChannel_OmitsTheFieldAndStaysAsToday()
    {
        // 关闭态：Core 不输出该字段（入口点用 WhenWritingNull）⇒ Desktop 侧 null ⇒ 不启动（且不是异常）。
        var line = ReadyLine(new Dictionary<string, object?>
        {
            ["capabilityEndpoint"] = null,
        });

        // "关闭时那一行与今天逐字一致"是可回滚承诺，必须按**原文**钉住，而不能只看解析结果
        // （`"capabilityEndpoint":null` 也会解析成 null，但那一行已经变了）。
        Assert.DoesNotContain("capabilityEndpoint", line, StringComparison.Ordinal);

        var parsed = CoreReadyMessageParser.TryParse(line);

        Assert.NotNull(parsed);
        Assert.Null(parsed!.CapabilityEndpoint);
        Assert.False(Evaluate(parsed.CapabilityEndpoint).ShouldStart);
    }

    [Fact]
    public void LegacyVPrefixedDescription_FailsEndToEnd()
    {
        // 反例固定住这次缺陷的形态：旧格式即使走完整条链也起不来（这条测试在修复前会红）。
        var legacy = $"named-pipe:{PipeName}|v{DesktopProtocolVersion.Current}|core-3";

        var parsed = CoreReadyMessageParser.TryParse(ReadyLine(new Dictionary<string, object?>
        {
            ["capabilityEndpoint"] = legacy,
        }));

        Assert.NotNull(parsed);
        Assert.Equal(legacy, parsed!.CapabilityEndpoint);
        Assert.False(Evaluate(parsed.CapabilityEndpoint).ShouldStart);
    }

    [Fact]
    public void LegacyVPrefixedDescription_IsNotEvenSelfParseable()
    {
        // 产出侧自检（CapabilityChannelReadySignal.Describe 内部用同一套解析器）之所以能兜住：
        // 旧格式本身就不满足契约。
        Assert.False(DesktopCapabilityEndpoint.TryParse($"named-pipe:{PipeName}|v1|core-3", out _));
    }

    [Fact]
    public void ProductCodePublishesTheDescriptionThroughTheSharedFormatter()
    {
        // 上面几条钉住的是**契约**（产出函数 → 就绪行 → 解析 → 判定），但它们无法证明
        // 产品代码真的调用了那个函数：手写回 `|v{n}|` 仍能让契约测试全绿——这正是本次缺陷的复现方式。
        // 因此这里做一次结构断言（本仓库既有的边界测试同此风格）：产品侧的日志行与就绪字段
        // 必须都来自同一个产出函数，且不得再出现手写的版本前缀。
        var host = ReadRepoFile("Source/PuddingHost/Hosting/PuddingApplicationHost.cs");
        var entry = ReadRepoFile("Source/PuddingAgent/Program.cs");

        Assert.Contains("CapabilityChannelReadySignal", host, StringComparison.Ordinal);
        Assert.Contains("CapabilityChannelReadySignal.FieldName", entry, StringComparison.Ordinal);
        Assert.Contains("GetCapabilityEndpointDescription", entry, StringComparison.Ordinal);

        // 反例守卫：手写版本段（`|v`）与手写端点拼接都不允许再出现。
        Assert.DoesNotContain("|v{", host, StringComparison.Ordinal);
        Assert.DoesNotContain("|v{", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime.Description.Kind", host, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "PuddingAgentNetwork.slnx")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        var path = Path.Combine(current!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"repo file not found: {path}");
        return File.ReadAllText(path);
    }

    private static CoreReadyMessage ParseReadyLine(string description)
    {
        var line = ReadyLine(new Dictionary<string, object?>
        {
            [CapabilityChannelReadySignal.FieldName] = description,
        });

        var parsed = CoreReadyMessageParser.TryParse(line);
        Assert.NotNull(parsed);
        return parsed!;
    }

    /// <summary>按入口点同款的序列化选项组装就绪行（含"null 即省略"这一条）。</summary>
    private static string ReadyLine(IReadOnlyDictionary<string, object?> extraFields)
    {
        var payload = new Dictionary<string, object?>
        {
            ["protocolVersion"] = 1,
            ["processId"] = Environment.ProcessId,
            ["baseAddress"] = "http://127.0.0.1:51234",
        };

        foreach (var pair in extraFields)
        {
            // 与入口点同款：**null 即不写这个字段**。
            // 不能用序列化器的 WhenWritingNull 代替——它只作用于属性，不作用于字典项
            //（这条差异正是本测试当场抓出来的：换写法后关闭态那一行多出了 `"capabilityEndpoint":null`）。
            if (pair.Value is null)
            {
                continue;
            }

            payload[pair.Key] = pair.Value;
        }

        return CoreReadyMessageParser.ReadyPrefix + JsonSerializer.Serialize(payload);
    }

    private static DesktopCapabilityChannelPreflightReport Evaluate(string? endpointDescription) =>
        DesktopCapabilityChannelPreflight.Evaluate(
            new DesktopCapabilityChannelSettings { Enabled = true, DesktopId = "default" },
            new DesktopProcessInstanceId("proc-1"),
            authentication: null,
            endpointDescription);
}
