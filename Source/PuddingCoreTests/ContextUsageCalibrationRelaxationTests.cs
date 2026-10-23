using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingCoreTests;

/// <summary>
/// The prompt calibration ratio must be able to <b>come back down</b>.
/// </summary>
/// <remarks>
/// With a keep-the-maximum rule, one provider "input exceeds limit" failure can lift the ratio to
/// e.g. 1.6, and it then stays there forever: every later turn reports <c>observedRatio = 1.0</c>
/// (because the estimate was pessimistic) yet the stored ratio never shrinks, so used tokens stay
/// inflated by 1.6x while the effective window does not. That is what made compaction fire early.
/// These tests pin the relaxation rule: raise immediately, relax by a factor, never below 1.0.
/// </remarks>
[TestClass]
public sealed class ContextUsageCalibrationRelaxationTests
{
    private const double Tolerance = 0.01;

    [TestMethod]
    public void Higher_Measured_Ratio_Raises_The_Calibration_Immediately()
    {
        var store = new ContextUsageSnapshotStore();

        var ratio = ReportAndRead(store, "s-raise", "model-A", "inv-raise", promptFactor: 1.6);

        Assert.AreEqual(1.6, ratio, Tolerance, "a measurably larger request must raise the ratio at once");
    }

    [TestMethod]
    public void Lower_Measured_Ratio_Relaxes_Instead_Of_Sticking_At_The_Maximum()
    {
        var store = new ContextUsageSnapshotStore();
        ReportAndRead(store, "s-relax", "model-A", "inv-high", promptFactor: 1.6);

        var relaxed = ReportAndRead(store, "s-relax", "model-A", "inv-low", promptFactor: 1.0);

        // 1.6 * 0.7 = 1.12, not 1.6 (stuck) and not 1.0 (collapsed in one step).
        Assert.AreEqual(1.12, relaxed, Tolerance, "a lower measurement must pull the ratio back down");
    }

    [TestMethod]
    public void Repeated_Low_Measurements_Converge_To_One_And_Never_Below_It()
    {
        var store = new ContextUsageSnapshotStore();
        ReportAndRead(store, "s-converge", "model-A", "inv-0", promptFactor: 1.6);

        var observed = new List<double>();
        for (var i = 1; i <= 5; i++)
            observed.Add(ReportAndRead(store, "s-converge", "model-A", $"inv-{i}", promptFactor: 1.0));

        Assert.AreEqual(1.12, observed[0], Tolerance, "first relaxation step");
        foreach (var ratio in observed.Skip(1))
        {
            Assert.AreEqual(1.0, ratio, Tolerance, "ratio must bottom out at 1.0");
        }

        // Below 1.0 the estimate would under-count and requests could exceed the provider input limit.
        Assert.IsTrue(observed.All(r => r >= 1.0), "calibration must never drop below 1.0");
    }

    [TestMethod]
    public void Relaxation_Is_Scoped_To_The_Model_That_Reported_It()
    {
        var store = new ContextUsageSnapshotStore();
        ReportAndRead(store, "s-scope", "model-A", "inv-a1", promptFactor: 1.6);
        var untouchedBefore = store.GetPromptCalibrationRatio("s-scope", "model-B");

        ReportAndRead(store, "s-scope", "model-A", "inv-a2", promptFactor: 1.0);

        Assert.AreEqual(
            untouchedBefore,
            store.GetPromptCalibrationRatio("s-scope", "model-B"),
            Tolerance,
            "relaxing model-A must not touch model-B");
        Assert.AreEqual(1.12, store.GetPromptCalibrationRatio("s-scope", "model-A"), Tolerance);
    }

    [TestMethod]
    public void A_Higher_Measurement_After_Relaxation_Raises_Again()
    {
        var store = new ContextUsageSnapshotStore();
        ReportAndRead(store, "s-rebound", "model-A", "inv-1", promptFactor: 1.6);
        ReportAndRead(store, "s-rebound", "model-A", "inv-2", promptFactor: 1.0);

        var rebound = ReportAndRead(store, "s-rebound", "model-A", "inv-3", promptFactor: 1.9);

        Assert.AreEqual(1.9, rebound, Tolerance, "relaxation must not become a permanent ceiling break");
    }

    /// <summary>
    /// Captures a request, reports provider usage for it, and returns the resulting ratio.
    /// Each call uses a fresh invocation id, because repeated usage for the same attempt is
    /// deliberately idempotent and would not move the ratio.
    /// </summary>
    private static double ReportAndRead(
        ContextUsageSnapshotStore store,
        string sessionId,
        string modelId,
        string invocationId,
        double promptFactor)
    {
        var prepared = store.CaptureLlmRequest(
            sessionId,
            Messages(4, 200),
            tools: null,
            modelId,
            LlmCallPurposes.Agent,
            invocationId: invocationId,
            attemptId: $"{invocationId}:a0");

        store.RecordProviderUsage(
            sessionId,
            new TokenUsageDto
            {
                PromptTokens = (int)Math.Round(prepared.RawEstimatedTokens * promptFactor),
            },
            LlmCallPurposes.Agent,
            invocationId: invocationId,
            attemptId: $"{invocationId}:a0");

        return store.GetPromptCalibrationRatio(sessionId, modelId);
    }

    private static IReadOnlyList<ChatMessage> Messages(int count, int chars)
    {
        var messages = new List<ChatMessage>();
        for (var i = 0; i < count; i++)
            messages.Add(new ChatMessage(ChatRole.User, $"m{i} " + new string('x', chars)));

        return messages;
    }
}
