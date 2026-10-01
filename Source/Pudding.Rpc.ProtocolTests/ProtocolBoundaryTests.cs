using System.Reflection;
using Pudding.Rpc.Protocol.V1;

namespace Pudding.Rpc.ProtocolTests;

/// <summary>
/// S4 边界断言：wire 程序集只含 proto 生成物，
/// 不引用 Contracts/宿主/UI，且工程目录内没有手写 C#。
/// </summary>
public sealed class ProtocolBoundaryTests
{
    private static readonly string[] AllowedPackages = ["Google.Protobuf", "Grpc.Core.Api", "Grpc.Tools"];

    private static string ProjectDirectory => Path.Combine(RepoLayout.Root, "Source", "Pudding.Rpc.Protocol");

    [Fact]
    public void ProtocolAssembly_ReferencesOnlyProtobufRuntimeAndGrpcApi()
    {
        var referenced = typeof(DesktopFrame).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        var offending = referenced
            .Where(name => !name.StartsWith("System", StringComparison.Ordinal)
                && name != "Google.Protobuf"
                && name != "Grpc.Core.Api"
                && name != "netstandard")
            .ToArray();

        Assert.Empty(offending);
    }

    [Fact]
    public void ProtocolCsproj_HasNoProjectReferenceAndOnlyAllowedPackages()
    {
        var content = File.ReadAllText(Path.Combine(ProjectDirectory, "Pudding.Rpc.Protocol.csproj"));

        Assert.DoesNotContain("<ProjectReference", content, StringComparison.Ordinal);
        Assert.Contains("EnforceProtocolLeafBoundary", content, StringComparison.Ordinal);

        var packages = content
            .Split("<PackageReference", StringSplitOptions.None)
            .Skip(1)
            .Select(chunk => chunk.Split("Include=\"", StringSplitOptions.None)[1].Split('"')[0])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(AllowedPackages.OrderBy(name => name, StringComparer.Ordinal).ToArray(), packages);
    }

    [Fact]
    public void ProtocolProject_ContainsProtoSourcesAndNoHandWrittenCSharp()
    {
        var protoFiles = Directory.GetFiles(Path.Combine(ProjectDirectory, "Protos"), "*.proto");
        Assert.Single(protoFiles);
        Assert.Equal("desktop_capability.proto", Path.GetFileName(protoFiles[0]));

        var handWritten = Directory
            .EnumerateFiles(ProjectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}temp{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(handWritten);
    }

    [Fact]
    public void ProtoSource_DeclaresVersionedPackage()
    {
        var proto = File.ReadAllText(Path.Combine(ProjectDirectory, "Protos", "desktop_capability.proto"));

        Assert.Contains("syntax = \"proto3\";", proto, StringComparison.Ordinal);
        Assert.Contains("package pudding.capability.v1;", proto, StringComparison.Ordinal);
        Assert.Contains("option csharp_namespace = \"Pudding.Rpc.Protocol.V1\";", proto, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtocolTestsCsproj_ReferencesExactlyOneProject()
    {
        var content = File.ReadAllText(Path.Combine(RepoLayout.Root, "Source", "Pudding.Rpc.ProtocolTests", "Pudding.Rpc.ProtocolTests.csproj"));

        var includes = content
            .Split("<ProjectReference", StringSplitOptions.None)
            .Skip(1)
            .Select(chunk => chunk.Split("Include=\"", StringSplitOptions.None)[1].Split('"')[0])
            .ToArray();

        Assert.Single(includes);
        Assert.Equal(@"..\Pudding.Rpc.Protocol\Pudding.Rpc.Protocol.csproj", includes[0]);
    }
}
