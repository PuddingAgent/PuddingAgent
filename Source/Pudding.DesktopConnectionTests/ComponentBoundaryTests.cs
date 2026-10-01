using System.Reflection;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;

namespace DesktopConnectionTests;

/// <summary>
/// S4 边界断言：适配器只依赖 Contracts + Rpc.Protocol + Grpc.Net.Client；
/// UI/ASP.NET Core/宿主绝不能进入 Desktop 进程的这条依赖路径；
/// 执行器接缝（UI 侧）不得泄漏 proto/WinUI 类型。
/// </summary>
public sealed class ComponentBoundaryTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "PuddingHost",
        "PuddingRuntime",
        "PuddingAgent",
        "PuddingDesktop",
        "Pudding.Browser",
        "Microsoft.AspNetCore",
        "Microsoft.UI",
        "Microsoft.Web.WebView2",
        "WinRT",
    ];

    private static string ProjectDirectory => Path.Combine(RepoLayout.Root, "Source", "Pudding.DesktopConnection");

    [Fact]
    public void ConnectionAssembly_ReferencesOnlyItsDeclaredClosure()
    {
        var referenced = typeof(DesktopConnection).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        var offending = referenced
            .Where(name => ForbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(offending);
        Assert.Contains("Pudding.Contracts", referenced);
        Assert.Contains("Pudding.Rpc.Protocol", referenced);
        Assert.Contains("Grpc.Net.Client", referenced);
    }

    [Fact]
    public void TestProcess_DoesNotLoadUiOrServerAssemblies()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name ?? string.Empty)
            .ToArray();

        var offending = loaded
            .Where(name => ForbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(offending);
    }

    [Fact]
    public void ConnectionCsproj_DeclaresExpectedReferencesAndKeepsItsBoundaryTarget()
    {
        var content = File.ReadAllText(Path.Combine(ProjectDirectory, "Pudding.DesktopConnection.csproj"));

        var projectReferences = ReadAttributes(content, "<ProjectReference", "Include=\"");
        Assert.Equal(
            [@"..\Pudding.Contracts\Pudding.Contracts.csproj", @"..\Pudding.Rpc.Protocol\Pudding.Rpc.Protocol.csproj"],
            projectReferences);

        var packageReferences = ReadAttributes(content, "<PackageReference", "Include=\"");
        Assert.Equal(["Grpc.Net.Client"], packageReferences);

        Assert.Contains("EnforceConnectionBoundary", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionTestsCsproj_ReferencesExactlyOneProject()
    {
        var content = File.ReadAllText(
            Path.Combine(RepoLayout.Root, "Source", "Pudding.DesktopConnectionTests", "Pudding.DesktopConnectionTests.csproj"));

        Assert.Equal([@"..\Pudding.DesktopConnection\Pudding.DesktopConnection.csproj"], ReadAttributes(content, "<ProjectReference", "Include=\""));
    }

    [Fact]
    public void ExecutorSeam_ExposesNoTransportOrUiTypes()
    {
        var offending = new List<string>();

        Inspect(typeof(IDesktopCapabilityExecutor), offending);
        Inspect(typeof(DesktopConnectionOptions), offending);
        Inspect(typeof(DesktopCapabilityRequest), offending);
        Inspect(typeof(DesktopCapabilityResponse), offending);

        Assert.Empty(offending);
    }

    [Fact]
    public void ConnectionComponent_HasNoHandWrittenGrpcServerCode()
    {
        var sources = Directory
            .EnumerateFiles(ProjectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(sources);

        // Desktop 侧不托管服务、不映射 gRPC 服务端基类：只有客户端拨入。
        var offenders = sources
            .Where(path => File.ReadAllText(path).Contains(": DesktopCapability.DesktopCapabilityBase", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static void Inspect(Type type, List<string> offending)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            Check(method.ReturnType, type, method.Name, offending);
            foreach (var parameter in method.GetParameters())
            {
                Check(parameter.ParameterType, type, method.Name, offending);
            }
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            Check(property.PropertyType, type, property.Name, offending);
        }
    }

    private static void Check(Type? type, Type owner, string member, List<string> offending)
    {
        while (type is not null)
        {
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    Check(argument, owner, member, offending);
                }

                type = type.GetGenericTypeDefinition();
            }

            var ns = type.Namespace ?? string.Empty;
            if (ns.StartsWith("Pudding.Rpc.Protocol", StringComparison.Ordinal)
                || ns.StartsWith("Grpc", StringComparison.Ordinal)
                || ns.StartsWith("Google.Protobuf", StringComparison.Ordinal)
                || ns.StartsWith("Microsoft.UI", StringComparison.Ordinal)
                || ns.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            {
                offending.Add($"{owner.FullName}.{member}: {type.FullName}");
            }

            type = type.IsArray ? type.GetElementType() : null;
        }
    }

    private static string[] ReadAttributes(string content, string element, string attributePrefix) =>
        content
            .Split(element, StringSplitOptions.None)
            .Skip(1)
            .Select(chunk => chunk.Split(attributePrefix, StringSplitOptions.None)[1].Split('"')[0])
            .ToArray();
}
