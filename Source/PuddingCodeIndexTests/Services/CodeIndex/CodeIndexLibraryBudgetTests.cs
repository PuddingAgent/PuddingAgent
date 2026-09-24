using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// M2-a: the per-library capacity budget is a configuration parameter (user ruling 2026-09-24), resolved from
/// the project file over the global file over a conservative default. These tests lock the exact byte count of
/// the default, the SI/IEC unit semantics (the GB/GiB trap), the precedence order, and — most importantly —
/// that a corrupt configuration falls back to the default instead of failing open into an unlimited budget.
/// </summary>
[TestClass]
public sealed class CodeIndexLibraryBudgetTests
{
    private const long OneGiB = 1L << 30;

    [TestMethod]
    public void Default_Budget_Is_One_Binary_Gigabyte_When_No_Configuration_Exists()
    {
        using var fixture = CodeIndexFixture.Create();

        var budget = Resolve(fixture);

        Assert.AreEqual(OneGiB, budget.MaxBytes, "the fallback must be an exact byte count, not a rounded '1GB'");
        Assert.AreEqual(858993459L, budget.SoftBytes, "80% of 1 GiB, rounded away from zero");
        Assert.AreEqual(CodeIndexLibraryBudgetSource.Default, budget.Source);
        Assert.IsNull(budget.ConfigPath);
    }

    [TestMethod]
    public void Project_Configuration_Overrides_Global_Configuration()
    {
        using var fixture = CodeIndexFixture.Create();
        WriteGlobal(fixture, """{ "library": { "maxLibrarySize": "2GiB" } }""");
        WriteProject(fixture, """{ "library": { "maxLibrarySize": "4GiB" } }""");

        var budget = Resolve(fixture);

        Assert.AreEqual(4L * OneGiB, budget.MaxBytes);
        Assert.AreEqual(CodeIndexLibraryBudgetSource.ProjectConfig, budget.Source);
    }

    [TestMethod]
    public void Global_Configuration_Is_Used_When_The_Project_Declares_Nothing()
    {
        using var fixture = CodeIndexFixture.Create();
        WriteGlobal(fixture, """{ "library": { "maxLibraryBytes": 268435456 } }""");

        var budget = Resolve(fixture);

        Assert.AreEqual(268435456L, budget.MaxBytes);
        Assert.AreEqual(CodeIndexLibraryBudgetSource.GlobalConfig, budget.Source);
    }

    /// <summary>
    /// Fields merge across sources: a project may raise the ceiling while the host-wide file still supplies the
    /// soft threshold.
    /// </summary>
    [TestMethod]
    public void Fields_Merge_Across_Sources_Instead_Of_Being_Replaced_Wholesale()
    {
        using var fixture = CodeIndexFixture.Create();
        WriteGlobal(fixture, """{ "library": { "softRatio": 0.5 } }""");
        WriteProject(fixture, """{ "library": { "maxLibrarySize": "8GiB" } }""");

        var budget = Resolve(fixture);

        Assert.AreEqual(8L * OneGiB, budget.MaxBytes, "ceiling comes from the project file");
        Assert.AreEqual(0.5d, budget.SoftRatio, "ratio still comes from the global file");
        Assert.AreEqual(4L * OneGiB, budget.SoftBytes);
    }

    [TestMethod]
    public void Invalid_Json_Falls_Back_To_The_Default_Instead_Of_Unlimited()
    {
        using var fixture = CodeIndexFixture.Create();
        WriteProject(fixture, "{ \"library\": { \"maxLibraryBytes\": 1");

        var budget = Resolve(fixture);

        Assert.AreEqual(OneGiB, budget.MaxBytes, "a typo must never produce an unbounded budget");
        Assert.AreEqual(CodeIndexLibraryBudgetSource.Default, budget.Source);
        Assert.IsNotNull(budget.Warnings);
        Assert.IsTrue(
            budget.Warnings!.Any(w => w.Contains("not valid JSON", StringComparison.OrdinalIgnoreCase)),
            "the fallback must be visible in the warnings");
    }

    [TestMethod]
    public void Non_Positive_Budget_Is_Rejected_And_The_Default_Stays_In_Force()
    {
        using var fixture = CodeIndexFixture.Create();
        WriteProject(fixture, """{ "library": { "maxLibraryBytes": 0, "softRatio": 7 } }""");

        var budget = Resolve(fixture);

        Assert.AreEqual(OneGiB, budget.MaxBytes);
        Assert.AreEqual(CodeIndexLibraryBudgetDefaults.SoftRatio, budget.SoftRatio);
        Assert.IsNotNull(budget.Warnings);
        Assert.AreEqual(2, budget.Warnings!.Count, "both invalid fields must be reported, not silently dropped");
    }

    [TestMethod]
    public void Both_Size_Fields_Present_Prefers_Exact_Bytes_And_Reports_The_Conflict()
    {
        using var fixture = CodeIndexFixture.Create();
        WriteProject(fixture, """{ "library": { "maxLibrarySize": "4GiB", "maxLibraryBytes": 1000000 } }""");

        var budget = Resolve(fixture);

        Assert.AreEqual(1000000L, budget.MaxBytes);
        Assert.IsTrue(budget.Warnings!.Any(w => w.Contains("wins over", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Size_Suffixes_Keep_Si_And_Iec_Semantics_Apart()
    {
        AssertParsed("2GiB", 2L * OneGiB, usedDecimalUnit: false);
        AssertParsed("2GB", 2000000000L, usedDecimalUnit: true);
        AssertParsed("512MiB", 512L * 1024 * 1024, usedDecimalUnit: false);
        AssertParsed("1MB", 1000000L, usedDecimalUnit: true);
        AssertParsed("1073741824", 1073741824L, usedDecimalUnit: false);
        AssertParsed("1024B", 1024L, usedDecimalUnit: false);
    }

    [TestMethod]
    public void Decimal_Unit_Use_Is_Surfaced_Instead_Of_Silently_Reinterpreted()
    {
        using var fixture = CodeIndexFixture.Create();
        WriteProject(fixture, """{ "library": { "maxLibrarySize": "1GB" } }""");

        var budget = Resolve(fixture);

        Assert.AreEqual(1000000000L, budget.MaxBytes);
        Assert.IsTrue(
            budget.Warnings!.Any(w => w.Contains("decimal", StringComparison.OrdinalIgnoreCase)),
            "using 'GB' where 'GiB' was probably meant must be visible");
    }

    [TestMethod]
    public void Unknown_Unit_And_Garbage_Sizes_Are_Rejected()
    {
        Assert.IsFalse(CodeIndexSizes.Parse("4XB").Success);
        Assert.IsFalse(CodeIndexSizes.Parse("").Success);
        Assert.IsFalse(CodeIndexSizes.Parse("GiB").Success);
        Assert.IsFalse(CodeIndexSizes.Parse("-1GiB").Success);
        Assert.IsTrue(CodeIndexSizes.Parse(" 1 gib ").Success, "parsing tolerates case and surrounding space");
    }

    [TestMethod]
    public void Measure_Counts_Every_File_In_The_Library_Directory()
    {
        using var fixture = CodeIndexFixture.Create();
        var library = Path.Combine(fixture.Root, "lib");
        var segments = Path.Combine(library, "segments");
        Directory.CreateDirectory(segments);
        File.WriteAllBytes(Path.Combine(library, "code_index.db"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(library, "code_index.db-wal"), new byte[512]);
        File.WriteAllBytes(Path.Combine(segments, "vectors.bin"), new byte[128]);

        var measured = CodeIndexLibraryCapacity.MeasureDirectoryBytes(library);

        Assert.AreEqual(4096L + 512 + 128, measured, "wal and vector segments are part of the footprint");
        Assert.AreEqual(0L, CodeIndexLibraryCapacity.MeasureDirectoryBytes(Path.Combine(fixture.Root, "nope")));
    }

    [TestMethod]
    public void Evaluate_Reports_Soft_Then_Hard_Exceeded()
    {
        var budget = new CodeIndexLibraryBudget(OneGiB, 0.8d, CodeIndexLibraryBudgetSource.Default);

        Assert.AreEqual(
            CodeIndexLibraryCapacityLevel.Ok,
            CodeIndexLibraryCapacity.Evaluate(budget, 100).Level);
        Assert.AreEqual(
            CodeIndexLibraryCapacityLevel.SoftExceeded,
            CodeIndexLibraryCapacity.Evaluate(budget, budget.SoftBytes).Level);
        Assert.AreEqual(
            CodeIndexLibraryCapacityLevel.HardExceeded,
            CodeIndexLibraryCapacity.Evaluate(budget, OneGiB).Level);
        Assert.IsTrue(CodeIndexLibraryCapacity.Evaluate(budget, OneGiB + 1).IsOverBudget);
        Assert.AreEqual(1d, CodeIndexLibraryCapacity.Evaluate(budget, OneGiB).UsageRatio, 1e-9);
    }

    private static void AssertParsed(string text, long expectedBytes, bool usedDecimalUnit)
    {
        var parsed = CodeIndexSizes.Parse(text);

        Assert.IsTrue(parsed.Success, $"'{text}' should parse");
        Assert.AreEqual(expectedBytes, parsed.Bytes, $"'{text}'");
        Assert.AreEqual(usedDecimalUnit, parsed.UsedDecimalUnit, $"'{text}' unit classification");
    }

    private static CodeIndexLibraryBudget Resolve(CodeIndexFixture fixture) =>
        new CodeIndexLibraryBudgetResolver(ProjectConfigPath(fixture), GlobalConfigPath(fixture)).Resolve();

    private static string ProjectConfigPath(CodeIndexFixture fixture) =>
        Path.Combine(fixture.Root, ".pudding", "code-index.json");

    private static string GlobalConfigPath(CodeIndexFixture fixture) =>
        Path.Combine(fixture.Root, "config", "code-index.json");

    private static void WriteProject(CodeIndexFixture fixture, string json) =>
        Write(ProjectConfigPath(fixture), json);

    private static void WriteGlobal(CodeIndexFixture fixture, string json) =>
        Write(GlobalConfigPath(fixture), json);

    private static void Write(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }
}
