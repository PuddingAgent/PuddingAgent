using System.Reflection;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// S4 边界断言：DesktopService 只依赖 Contracts + DesktopConnection；
/// UI/ASP.NET Core/宿主不得进入这条依赖路径；公共表面不泄漏 proto/UI 类型。
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

    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Pudding.Rpc.Protocol",
        "Grpc",
        "Google.Protobuf",
        "Microsoft.UI",
        "Microsoft.AspNetCore",
        "Microsoft.Web",
    ];

    private static string ProjectDirectory => Path.Combine(RepoLayout.Root, "Source", "Pudding.DesktopService");

    [Fact]
    public void ServiceAssembly_ReferencesOnlyItsDeclaredClosure()
    {
        var referenced = typeof(DesktopService).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        Assert.Empty(referenced.Where(name =>
            ForbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))));

        Assert.Contains("Pudding.Contracts", referenced);
        Assert.Contains("Pudding.DesktopConnection", referenced);
    }

    [Fact]
    public void TestProcess_DoesNotLoadUiOrServerAssemblies()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name ?? string.Empty)
            .ToArray();

        Assert.Empty(loaded.Where(name =>
            ForbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))));
    }

    [Fact]
    public void ServiceCsproj_DeclaresExpectedReferencesAndKeepsItsBoundaryTarget()
    {
        var content = File.ReadAllText(Path.Combine(ProjectDirectory, "Pudding.DesktopService.csproj"));

        Assert.Equal(
            [
                @"..\Pudding.Contracts\Pudding.Contracts.csproj",
                @"..\Pudding.DesktopConnection\Pudding.DesktopConnection.csproj",
                // S5 接入（2026-10-08）：变更类后置条件与 Bridge 路径共用同一份纯逻辑组件。
                // 它是叶子级纯逻辑（只引用 Pudding.Contracts，无 UI/驱动/WebView2），
                // 不越过 EnforceDesktopServiceBoundary 的禁止项。
                @"..\PuddingBrowser.Automation\PuddingBrowser.Automation.csproj",
            ],
            ReadAttributes(content, "<ProjectReference", "Include=\""));
        Assert.Empty(ReadAttributes(content, "<PackageReference", "Include=\""));
        Assert.Contains("EnforceDesktopServiceBoundary", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceTestsCsproj_ReferencesExactlyOneProject()
    {
        var content = File.ReadAllText(
            Path.Combine(RepoLayout.Root, "Source", "Pudding.DesktopServiceTests", "Pudding.DesktopServiceTests.csproj"));

        Assert.Equal(
            [@"..\Pudding.DesktopService\Pudding.DesktopService.csproj"],
            ReadAttributes(content, "<ProjectReference", "Include=\""));
    }

    [Fact]
    public void PublicSurface_ExposesNoTransportOrUiTypes()
    {
        var offending = new List<string>();

        Inspect(typeof(DesktopService), offending);
        Inspect(typeof(DesktopServiceOptions), offending);
        Inspect(typeof(DesktopCapabilityPolicy), offending);
        Inspect(typeof(DesktopTargetRegistry), offending);
        Inspect(typeof(DesktopTargetState), offending);
        Inspect(typeof(DesktopInteractionState), offending);
        Inspect(typeof(IDesktopUiDispatcher), offending);
        Inspect(typeof(IDesktopUiSurface), offending);

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
