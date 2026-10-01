using System.Reflection;
using Pudding.Contracts;

namespace Pudding.ContractsTests;

/// <summary>
/// S4 边界断言（机器可验）：
/// ① Contracts 只引用 BCL；
/// ② 测试进程不加载 UI/传输/宿主程序集；
/// ③ csproj 的 ProjectReference/PackageReference 为 0；
/// ④ 公共契约不暴露任何传输或 UI 类型。
/// </summary>
public sealed class ComponentBoundaryTests
{
    private static readonly string[] ForbiddenPrefixes = ["Pudding", "Grpc", "Google", "Microsoft", "AspNetCore", "WinUI", "WebView2"];

    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Grpc",
        "Google.Protobuf",
        "Microsoft.AspNetCore",
        "Microsoft.UI",
        "Microsoft.Web",
        "Microsoft.Extensions",
        "System.Net.Http",
        "System.Text.Json",
    ];

    [Fact]
    public void ContractsAssembly_ReferencesOnlyBcl()
    {
        var referenced = typeof(DesktopInstanceId).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        var offending = referenced
            .Where(name => ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(offending);
        Assert.DoesNotContain(referenced, name => name.Contains("Protobuf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TestProcess_DoesNotLoadTransportOrHostAssemblies()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name ?? string.Empty)
            .ToArray();

        var allowed = new[] { "Pudding.Contracts", typeof(ComponentBoundaryTests).Assembly.GetName().Name };

        var offending = loaded
            .Where(name => name.StartsWith("Pudding", StringComparison.Ordinal) && !allowed.Contains(name, StringComparer.Ordinal)
                || name.StartsWith("PuddingHost", StringComparison.Ordinal)
                || name.StartsWith("PuddingRuntime", StringComparison.Ordinal)
                || name.StartsWith("Grpc", StringComparison.Ordinal)
                || name.StartsWith("Google.Protobuf", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.UI", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.Web.WebView2", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offending);
    }

    [Fact]
    public void ContractsCsproj_DeclaresNoProjectOrPackageReference()
    {
        var csproj = Path.Combine(RepoLayout.Root, "Source", "Pudding.Contracts", "Pudding.Contracts.csproj");
        Assert.True(File.Exists(csproj), $"Expected component project at {csproj}");

        var content = File.ReadAllText(csproj);
        Assert.DoesNotContain("<ProjectReference", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<PackageReference", content, StringComparison.Ordinal);
        // 边界规则本身必须由编译期 Target 强制，而不是靠文档约定。
        Assert.Contains("EnforceContractsBoundary", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ContractsTestsCsproj_ReferencesExactlyOneProject()
    {
        var csproj = Path.Combine(RepoLayout.Root, "Source", "Pudding.ContractsTests", "Pudding.ContractsTests.csproj");
        var content = File.ReadAllText(csproj);

        var references = content.Split("<ProjectReference", StringSplitOptions.None).Length - 1;
        Assert.Equal(1, references);
        Assert.Contains("Pudding.Contracts.csproj", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicContracts_ExposeNoTransportOrUiTypes()
    {
        var assembly = typeof(DesktopInstanceId).Assembly;
        var offending = new List<string>();

        foreach (var type in assembly.GetExportedTypes())
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                Inspect(property.PropertyType, type, property.Name, offending);
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Inspect(method.ReturnType, type, method.Name, offending);
                foreach (var parameter in method.GetParameters())
                {
                    Inspect(parameter.ParameterType, type, method.Name, offending);
                }
            }
        }

        Assert.Empty(offending);
    }

    private static void Inspect(Type? type, Type owner, string member, List<string> offending)
    {
        while (type is not null)
        {
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    Inspect(argument, owner, member, offending);
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
}
