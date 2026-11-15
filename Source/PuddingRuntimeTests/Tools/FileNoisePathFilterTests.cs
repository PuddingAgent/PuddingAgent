using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 噪声路径判定的边界测试（2026-11-15）。
/// 背景：file_search 此前完全不排除 bin/obj，而 list_dir / search_grep 排除 —— 同一目录口径不一。
/// 这个判定是两条工具的共同基础，所以边界必须逐条锁住，尤其是「搜索根就在噪声目录里」这一条：
/// 判错会把整棵树误杀，等于又造一个「拿不到全部信息」的洞。
/// </summary>
[TestClass]
public sealed class FileNoisePathFilterTests
{
    [TestMethod]
    public void Nested_Noise_Directory_Is_Detected()
    {
        var root = Path.Combine(Path.GetTempPath(), "noise-probe");
        var path = Path.Combine(root, "src", "bin", "Debug", "app.csproj");

        Assert.IsTrue(FileNoisePathFilter.IsUnderNoiseDirectory(path, root));
    }

    [TestMethod]
    public void Ordinary_Source_Path_Is_Not_Noise()
    {
        var root = Path.Combine(Path.GetTempPath(), "noise-probe");
        var path = Path.Combine(root, "src", "PuddingCore", "PuddingCore.csproj");

        Assert.IsFalse(FileNoisePathFilter.IsUnderNoiseDirectory(path, root));
    }

    // 关键守卫：显式在 bin 目录里搜索时，结果不得被整体判成噪声。
    [TestMethod]
    public void Search_Root_Inside_Noise_Directory_Is_Not_Dropped()
    {
        var root = Path.Combine(Path.GetTempPath(), "noise-probe", "bin", "Debug");
        var path = Path.Combine(root, "app.dll");

        Assert.IsFalse(FileNoisePathFilter.IsUnderNoiseDirectory(path, root));
    }

    // 最后一段是文件名，不参与目录判定：bin.csproj 不是噪声目录。
    [TestMethod]
    public void File_Named_Like_A_Noise_Directory_Is_Not_Noise()
    {
        var root = Path.Combine(Path.GetTempPath(), "noise-probe");
        var path = Path.Combine(root, "bin.csproj");

        Assert.IsFalse(FileNoisePathFilter.IsUnderNoiseDirectory(path, root));
    }

    [TestMethod]
    public void Noise_Matching_Is_Case_Insensitive()
    {
        var root = Path.Combine(Path.GetTempPath(), "noise-probe");
        var path = Path.Combine(root, "SRC", "OBJ", "generated.cs");

        Assert.IsTrue(FileNoisePathFilter.IsUnderNoiseDirectory(path, root));
    }

    // 宁可不排除，也不要错杀：不在搜索根之下的路径一律不判噪声。
    [TestMethod]
    public void Path_Outside_Search_Root_Is_Not_Noise()
    {
        var root = Path.Combine(Path.GetTempPath(), "noise-probe");
        var path = Path.Combine(Path.GetTempPath(), "elsewhere", "bin", "x.csproj");

        Assert.IsFalse(FileNoisePathFilter.IsUnderNoiseDirectory(path, root));
    }
}
