using System.Runtime.CompilerServices;
using System.Xml.Linq;
using PuddingContextHandoff;

namespace PuddingContextHandoffTests;

[TestClass]
public sealed class HandoffComponentBoundaryTests
{
    [TestMethod]
    public void ComponentAndTests_HaveOnlyAllowedDependencies()
    {
        var component = XDocument.Load(Project("PuddingContextHandoff"));
        Assert.IsFalse(component.Descendants().Any(e => e.Name.LocalName is "ProjectReference" or "PackageReference"));
        var refs = XDocument.Load(Project("PuddingContextHandoffTests")).Descendants("ProjectReference").ToArray();
        Assert.HasCount(1, refs);
        StringAssert.Contains(refs[0].Attribute("Include")!.Value, "PuddingContextHandoff.csproj");
        var names = typeof(HandoffStore).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.Contains("System.Runtime", names);
        Assert.IsFalse(names.Any(n => n.StartsWith("Pudding", StringComparison.Ordinal)));
        Assert.IsFalse(AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "PuddingRuntime" or "PuddingHost"));
    }

    private static string Project(string name, [CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", name, name + ".csproj"));
}
