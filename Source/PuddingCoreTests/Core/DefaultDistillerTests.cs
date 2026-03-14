namespace PuddingCodeTests.Core;

[TestClass]
public sealed class DefaultDistillerTests
{
    private DefaultDistiller _distiller = null!;

    [TestInitialize]
    public void Setup()
    {
        _distiller = new DefaultDistiller();
    }

    // ──── Empty/Null Input Tests ────

    [TestMethod]
    public void Distill_NullOutput_ReturnsNoOutput()
    {
        // Act
        var result = _distiller.Distill(null!, new DistillContext("test", 0));

        // Assert
        Assert.AreEqual("(no output)", result.Summary);
        Assert.AreEqual(0, result.OriginalLines);
        Assert.IsFalse(result.IsTruncated);
    }

    [TestMethod]
    public void Distill_EmptyOutput_ReturnsNoOutput()
    {
        // Act
        var result = _distiller.Distill("", new DistillContext("test", 0));

        // Assert
        Assert.AreEqual("(no output)", result.Summary);
        Assert.AreEqual(0, result.OriginalLines);
        Assert.IsFalse(result.IsTruncated);
    }

    // ──── Passthrough Tests ────

    [TestMethod]
    public void Distill_SmallOutput_PassesThrough()
    {
        // Arrange
        var output = "Line 1\nLine 2\nLine 3";

        // Act
        var result = _distiller.Distill(output, new DistillContext("test", 0));

        // Assert
        Assert.AreEqual(output, result.Summary);
        Assert.AreEqual(3, result.OriginalLines);
        Assert.AreEqual(3, result.RetainedLines);
        Assert.IsFalse(result.IsTruncated);
    }

    [TestMethod]
    public void Distill_OutputAtPassthroughLimit_PassesThrough()
    {
        // Arrange
        var lines = Enumerable.Range(1, 20).Select(i => $"Line {i}");
        var output = string.Join("\n", lines);

        // Act
        var result = _distiller.Distill(output, new DistillContext("test", 0));

        // Assert
        Assert.AreEqual(output, result.Summary);
        Assert.AreEqual(20, result.OriginalLines);
        Assert.IsFalse(result.IsTruncated);
    }

    // ──── Truncation Tests ────

    [TestMethod]
    public void Distill_LargeOutput_TruncatesHeadAndTail()
    {
        // Arrange
        var lines = Enumerable.Range(1, 50).Select(i => $"Line {i}");
        var output = string.Join("\n", lines);

        // Act
        var result = _distiller.Distill(output, new DistillContext("test", 0));

        // Assert
        Assert.IsTrue(result.IsTruncated);
        Assert.AreEqual(50, result.OriginalLines);
        Assert.IsTrue(result.RetainedLines < 50);
        StringAssert.Contains(result.Summary, "Line 1");
        StringAssert.Contains(result.Summary, "Line 50");
        StringAssert.Contains(result.Summary, "truncated");
    }

    [TestMethod]
    public void Distill_Truncation_ContainsOmissionMarker()
    {
        // Arrange
        var lines = Enumerable.Range(1, 100).Select(i => $"Line {i}");
        var output = string.Join("\n", lines);

        // Act
        var result = _distiller.Distill(output, new DistillContext("test", 0));

        // Assert
        StringAssert.Contains(result.Summary, "lines truncated");
    }

    // ──── Error Enhancement Tests ────

    [TestMethod]
    public void Distill_FailedCommand_ExtractsErrorLines()
    {
        // Arrange - >20 lines to trigger error enhancement mode
        var output = """
            Building project...
            Compiling file1.cs
            Compiling file2.cs
            Compiling file3.cs
            Compiling file4.cs
            Compiling file5.cs
            Compiling file6.cs
            Compiling file7.cs
            Compiling file8.cs
            Compiling file9.cs
            Compiling file10.cs
            Compiling file11.cs
            Compiling file12.cs
            Compiling file13.cs
            Compiling file14.cs
            Compiling file15.cs
            Compiling file16.cs
            Compiling file17.cs
            Compiling file18.cs
            Compiling file19.cs
            error CS0001: Type 'Foo' not found
            at Program.Main()
            Build failed.
            """;

        // Act
        var result = _distiller.Distill(output, new DistillContext("build", 1));

        // Assert
        Assert.IsTrue(result.IsTruncated);
        StringAssert.Contains(result.Summary, "FAILED");
        StringAssert.Contains(result.Summary, "Exit code: 1");
        StringAssert.Contains(result.Summary, "error");
    }

    [TestMethod]
    public void Distill_FailedCommand_ExtractsFailLines()
    {
        // Arrange - >20 lines to trigger error enhancement mode
        var output = """
            Running tests...
            Test1 passed
            Test2 passed
            Test3 passed
            Test4 passed
            Test5 passed
            Test6 passed
            Test7 passed
            Test8 passed
            Test9 passed
            Test10 passed
            Test11 passed
            Test12 passed
            Test13 passed
            Test14 passed
            Test15 passed
            Test16 passed
            Test17 passed
            Test18 passed
            Test19 failed: Expected true but got false
            Test20 passed
            Build failed with 1 error.
            """;

        // Act
        var result = _distiller.Distill(output, new DistillContext("test", 1));

        // Assert
        Assert.IsTrue(result.IsTruncated);
        StringAssert.Contains(result.Summary, "FAILED");
        StringAssert.Contains(result.Summary, "fail");
    }

    [TestMethod]
    public void Distill_FailedCommand_ExtractsExceptionLines()
    {
        // Arrange - >20 lines to trigger error enhancement mode
        var output = """
            Starting application...
            Loading module 1
            Loading module 2
            Loading module 3
            Loading module 4
            Loading module 5
            Loading module 6
            Loading module 7
            Loading module 8
            Loading module 9
            Loading module 10
            Loading module 11
            Loading module 12
            Loading module 13
            Loading module 14
            Loading module 15
            Loading module 16
            Loading module 17
            Loading module 18
            Unhandled Exception: System.NullReferenceException
            at Program.Main() in Program.cs:line 42
            Application terminated.
            """;

        // Act
        var result = _distiller.Distill(output, new DistillContext("run", 1));

        // Assert
        Assert.IsTrue(result.IsTruncated);
        StringAssert.Contains(result.Summary, "FAILED");
        StringAssert.Contains(result.Summary, "Exception");
    }

    [TestMethod]
    public void Distill_FailedCommand_ExtractsFatalLines()
    {
        // Arrange - >20 lines to trigger error enhancement mode
        var output = """
            Initializing...
            Loading config
            Connecting to database
            Verifying credentials
            Checking permissions
            Loading user data
            Syncing state
            Processing queue
            Validating input
            Parsing response
            Formatting output
            Rendering view
            Updating cache
            Notifying subscribers
            Logging activity
            Monitoring health
            Checking metrics
            Analyzing trends
            Processing results
            FATAL: Database connection failed
            Shutting down.
            """;

        // Act
        var result = _distiller.Distill(output, new DistillContext("init", 1));

        // Assert
        Assert.IsTrue(result.IsTruncated);
        StringAssert.Contains(result.Summary, "FAILED");
        StringAssert.Contains(result.Summary, "FATAL");
    }

    // ──── Error Context Tests ────

    [TestMethod]
    public void Distill_ErrorEnhancement_IncludesContextLines()
    {
        // Arrange
        var lines = Enumerable.Range(1, 50).Select(i => $"Line {i}");
        var outputList = lines.ToList();
        outputList[25] = "error: Something went wrong"; // Error at line 26
        var output = string.Join("\n", outputList);

        // Act
        var result = _distiller.Distill(output, new DistillContext("test", 1));

        // Assert
        Assert.IsTrue(result.IsTruncated);
        // Should include context around line 26 (lines 23-29)
        StringAssert.Contains(result.Summary, "Line 23");
        StringAssert.Contains(result.Summary, "Line 29");
        // Should mark the error line
        StringAssert.Contains(result.Summary, "> error:");
    }

    [TestMethod]
    public void Distill_ErrorEnhancement_MarksErrorLine()
    {
        // Arrange - >20 lines to trigger error enhancement mode
        var output = """
            Line 1
            Line 2
            Line 3
            Line 4
            Line 5
            Line 6
            Line 7
            Line 8
            Line 9
            Line 10
            Line 11
            Line 12
            Line 13
            Line 14
            Line 15
            Line 16
            Line 17
            Line 18
            Line 19
            error: Target line
            Line 21
            """;

        // Act
        var result = _distiller.Distill(output, new DistillContext("test", 1));

        // Assert
        StringAssert.Contains(result.Summary, "> error:");
    }

    // ──── Multiple Errors Tests ────

    [TestMethod]
    public void Distill_MultipleErrors_ExtractsAllSnippets()
    {
        // Arrange - >20 lines to trigger error enhancement mode
        var output = """
            Building project...
            Compiling file1.cs
            Compiling file2.cs
            Compiling file3.cs
            Compiling file4.cs
            Compiling file5.cs
            Compiling file6.cs
            Compiling file7.cs
            Compiling file8.cs
            Compiling file9.cs
            error CS0001: Type 'A' not found
            Compiling more...
            Compiling file10.cs
            Compiling file11.cs
            Compiling file12.cs
            Compiling file13.cs
            Compiling file14.cs
            error CS0002: Type 'B' not found
            Compiling...
            Compiling file15.cs
            fail: Another issue
            Build failed.
            """;

        // Act
        var result = _distiller.Distill(output, new DistillContext("build", 1));

        // Assert
        Assert.IsTrue(result.IsTruncated);
        StringAssert.Contains(result.Summary, "error(s)");
    }

    // ──── Max Error Snippets Tests ────

    [TestMethod]
    public void Distill_ManyErrors_RespectsMaxSnippets()
    {
        // Arrange
        var errors = Enumerable.Range(1, 20).Select(i => $"error E{i}: Issue {i}");
        var output = "Building...\n" + string.Join("\n", errors) + "\nBuild failed.";

        // Act
        var result = _distiller.Distill(output, new DistillContext("build", 1));

        // Assert
        Assert.IsTrue(result.IsTruncated);
        // Should limit to MaxErrorSnippets (default: 5)
    }

    // ──── Failed With No Error Keywords Tests ────

    [TestMethod]
    public void Distill_FailedNoErrorKeywords_FallbackToHeadTail()
    {
        // Arrange
        var lines = Enumerable.Range(1, 100).Select(i => $"Line {i}");
        var output = string.Join("\n", lines);

        // Act
        var result = _distiller.Distill(output, new DistillContext("test", 1));

        // Assert
        Assert.IsTrue(result.IsTruncated);
        StringAssert.Contains(result.Summary, "FAILED");
        StringAssert.Contains(result.Summary, "Exit code: 1");
        StringAssert.Contains(result.Summary, "Line 1");
        StringAssert.Contains(result.Summary, "Line 100");
    }

    // ──── Character Limit Tests ────

    [TestMethod]
    public void Distill_LargeOutput_TruncatesToMaxLlmChars()
    {
        // Arrange - Multi-line large output (>20 lines and >MaxLlmChars)
        var lines = Enumerable.Range(1, 100).Select(i => new string('x', 100) + $" Line{i}");
        var largeOutput = string.Join("\n", lines);

        // Act
        var result = _distiller.Distill(largeOutput, new DistillContext("test", 1));

        // Assert
        Assert.IsTrue(result.IsTruncated);
        Assert.IsTrue(result.Summary.Length <= 4500); // MaxLlmChars + truncation marker
    }

    // ──── Custom Config Tests ────

    [TestMethod]
    public void Distill_WithCustomConfig_UsesCustomSettings()
    {
        // Arrange
        var config = new DistillerConfig
        {
            PassthroughLimit = 5,
            HeaderSize = 2,
            FooterSize = 2,
            MaxLlmChars = 1000,
            MaxErrorSnippets = 3
        };
        var customDistiller = new DefaultDistiller(config);
        var lines = Enumerable.Range(1, 20).Select(i => $"Line {i}");
        var output = string.Join("\n", lines);

        // Act
        var result = customDistiller.Distill(output, new DistillContext("test", 0));

        // Assert
        Assert.IsTrue(result.IsTruncated);
        Assert.AreEqual(4, result.RetainedLines); // HeaderSize + FooterSize
    }
}
