using PuddingCode.Models;

namespace PuddingCoreTests.MessageFabric;

[TestClass]
public sealed class MessageDeliveryPolicyTests
{
    [DataTestMethod]
    [DataRow(MessageIntents.Inform)]
    [DataRow(MessageIntents.ReportResult)]
    [DataRow(MessageIntents.AgentReply)]
    public void ResolveHandlingMode_PassiveIntent_DoesNotExecute(string intent)
    {
        var metadata = new Dictionary<string, string>
        {
            [MessageDeliveryPolicy.IntentMetadataKey] = intent,
            [MessageDeliveryPolicy.RequiresResponseMetadataKey] = "false",
        };

        Assert.AreEqual(
            MessageDeliveryHandlingModes.Notify,
            MessageDeliveryPolicy.ResolveHandlingMode(metadata));
        Assert.IsFalse(MessageDeliveryPolicy.RequiresResponse(metadata));
    }

    [DataTestMethod]
    [DataRow(MessageIntents.Ask)]
    [DataRow(MessageIntents.RequestReview)]
    [DataRow(MessageIntents.Delegate)]
    public void ResolveHandlingMode_WorkIntent_ExecutesAndDefaultsToOneReply(string intent)
    {
        var metadata = new Dictionary<string, string>
        {
            [MessageDeliveryPolicy.IntentMetadataKey] = intent,
        };

        Assert.AreEqual(
            MessageDeliveryHandlingModes.Execute,
            MessageDeliveryPolicy.ResolveHandlingMode(metadata));
        Assert.IsTrue(MessageDeliveryPolicy.RequiresResponse(metadata));
    }

    [TestMethod]
    public void RequiresResponse_UnknownLegacyIntent_FailsClosed()
    {
        var metadata = new Dictionary<string, string>
        {
            [MessageDeliveryPolicy.IntentMetadataKey] = "legacy_work",
        };

        Assert.AreEqual(
            MessageDeliveryHandlingModes.Execute,
            MessageDeliveryPolicy.ResolveHandlingMode(metadata));
        Assert.IsFalse(MessageDeliveryPolicy.RequiresResponse(metadata));
    }

    [TestMethod]
    public void NormalizeHandlingMode_PreV12EventWithoutMode_DerivesPassiveIntent()
    {
        var metadata = new Dictionary<string, string>
        {
            [MessageDeliveryPolicy.IntentMetadataKey] = MessageIntents.Inform,
            [MessageDeliveryPolicy.RequiresResponseMetadataKey] = "false",
        };

        Assert.AreEqual(
            MessageDeliveryHandlingModes.Notify,
            MessageDeliveryPolicy.NormalizeHandlingMode(null, metadata));
    }
}
