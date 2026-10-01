using System.Reflection;
using Pudding.CapabilityBroker;
using Pudding.Contracts.Desktop;

namespace CapabilityBrokerTests;

/// <summary>
/// S4 边界断言：Core 侧 Broker 只依赖 Contracts + Rpc.Protocol；
/// 不得把 Desktop/宿主/ASP.NET Core/WinUI 拉进依赖闭包（方向是 Core → 契约，不是 Core → Desktop）。
/// </summary>
public sealed class ComponentBoundaryTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "PuddingHost",
        "PuddingRuntime",
        "PuddingAgent",
        "PuddingDesktop",
        "Pudding.DesktopConnection",
        "Pudding.DesktopService",
        "Pudding.Browser",
        "Microsoft.AspNetCore",
        "Microsoft.UI",
        "Microsoft.Web.WebView2",
        "Grpc.AspNetCore",
    ];

    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Microsoft.AspNetCore",
        "Microsoft.UI",
        "Microsoft.Web",
        "Pudding.DesktopConnection",
        "Pudding.DesktopService",
    ];

    private static string ProjectDirectory => Path.Combine(RepoLayout.Root, "Source", "Pudding.CapabilityBroker");

    [Fact]
    public void BrokerAssembly_ReferencesOnlyContractsAndTheWireProtocol()
    {
        var referenced = typeof(CapabilityBroker).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        Assert.Empty(referenced.Where(name =>
            ForbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))));

        Assert.Contains("Pudding.Contracts", referenced);
        Assert.Contains("Pudding.Rpc.Protocol", referenced);
    }

    [Fact]
    public void TestProcess_DoesNotLoadDesktopHostOrServerAssemblies()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name ?? string.Empty)
            .ToArray();

        Assert.Empty(loaded.Where(name =>
            ForbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))));
    }

    [Fact]
    public void BrokerCsproj_DeclaresExpectedReferencesAndKeepsItsBoundaryTarget()
    {
        var content = File.ReadAllText(Path.Combine(ProjectDirectory, "Pudding.CapabilityBroker.csproj"));

        Assert.Equal(
            [@"..\Pudding.Contracts\Pudding.Contracts.csproj", @"..\Pudding.Rpc.Protocol\Pudding.Rpc.Protocol.csproj"],
            ReadAttributes(content, "<ProjectReference", "Include=\""));
        Assert.Empty(ReadAttributes(content, "<PackageReference", "Include=\""));
        Assert.Contains("EnforceCapabilityBrokerBoundary", content, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokerTestsCsproj_ReferencesExactlyOneProject()
    {
        var content = File.ReadAllText(
            Path.Combine(RepoLayout.Root, "Source", "Pudding.CapabilityBrokerTests", "Pudding.CapabilityBrokerTests.csproj"));

        Assert.Equal(
            [@"..\Pudding.CapabilityBroker\Pudding.CapabilityBroker.csproj"],
            ReadAttributes(content, "<ProjectReference", "Include=\""));
    }

    [Fact]
    public void PublicSurface_ExposesNoUiOrHostTypes()
    {
        var offending = new List<string>();

        Inspect(typeof(CapabilityBroker), offending);
        Inspect(typeof(CapabilityBrokerOptions), offending);
        Inspect(typeof(DesktopSession), offending);
        Inspect(typeof(DesktopCapabilityPolicy), offending);
        Inspect(typeof(IDesktopCapabilityAuthorizer), offending);
        Inspect(typeof(ICoreDesktopChannel), offending);

        Assert.Empty(offending);
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
            if (ForbiddenNamespacePrefixes.Any(prefix => ns.StartsWith(prefix, StringComparison.Ordinal)))
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
