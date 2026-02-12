using PuddingCode.Models;

namespace PuddingCoreTests.Memory;

[TestClass]
public sealed class MemoryWriteCommandValidatorTests
{
    [TestMethod]
    public void Validate_ShouldRejectMissingSource()
    {
        var command = ValidAppendCommand() with { Source = null! };
        var result = new MemoryWriteCommandValidator().Validate(command);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(e => e.Code == MemoryWriteValidationErrors.MissingSource));
    }

    [TestMethod]
    public void Validate_ShouldRejectCrossWorkspaceCandidate()
    {
        var command = ValidAppendCommand() with
        {
            Candidates =
            [
                new MemoryWriteCandidate
                {
                    WorkspaceId = "other-workspace",
                    ChapterId = "chapter-1",
                },
            ],
        };

        var result = new MemoryWriteCommandValidator().Validate(command);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(e => e.Code == MemoryWriteValidationErrors.CrossWorkspaceReference));
    }

    [TestMethod]
    public void Validate_ShouldRejectAppendWithoutContent()
    {
        var command = ValidAppendCommand() with
        {
            Payload = new MemoryWritePayload { Title = "Preference" },
        };
        var result = new MemoryWriteCommandValidator().Validate(command);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(e => e.Code == MemoryWriteValidationErrors.MissingRequiredField));
    }

    [TestMethod]
    public void Validate_ShouldRequireSubconsciousPlanIdentity()
    {
        var command = ValidAppendCommand() with
        {
            Source = new MemoryWriteSource
            {
                SourceKind = MemoryWriteSourceKinds.SubconsciousPlan,
                SessionId = "session-1",
            },
        };

        var result = new MemoryWriteCommandValidator().Validate(command);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(e => e.Code == MemoryWriteValidationErrors.MissingSourceIdentity));
    }

    [TestMethod]
    public void Validate_ShouldAcceptValidAppendDryRunCommand()
    {
        var result = new MemoryWriteCommandValidator().Validate(ValidAppendCommand());

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(0, result.Errors.Count);
    }

    private static MemoryWriteCommand ValidAppendCommand() =>
        new()
        {
            CommandId = "cmd-1",
            WorkspaceId = "workspace-1",
            Intent = MemoryWriteIntents.AppendNew,
            Mode = MemoryWriteExecutionModes.DryRun,
            Source = new MemoryWriteSource
            {
                SourceKind = MemoryWriteSourceKinds.RuntimeTool,
                SessionId = "session-1",
                ToolCallId = "tool-1",
            },
            Payload = new MemoryWritePayload
            {
                Title = "Preference",
                Content = "User prefers concise engineering summaries.",
                Confidence = 0.82,
            },
        };
}
