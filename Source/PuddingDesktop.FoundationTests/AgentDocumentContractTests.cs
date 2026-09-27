using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-04 document slice: slot catalogue, fingerprint stability and override classification.</summary>
public sealed class AgentDocumentContractTests
{
    [Fact]
    public void SlotCataloguesAreStableAndWithoutDuplicates()
    {
        Assert.Equal(7, AgentDocuments.TemplateSlots.Count);
        Assert.Equal(6, AgentDocuments.InstanceSlots.Count);
        Assert.Equal(AgentDocuments.TemplateSlots.Count,
            AgentDocuments.TemplateSlots.Select(slot => slot.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(AgentDocuments.InstanceSlots.Count,
            AgentDocuments.InstanceSlots.Select(slot => slot.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["soul", "agents", "tools", "bootstrap", "memory", "heartbeat"],
            AgentDocuments.InstanceSlots.Select(slot => slot.Key));
        Assert.Equal("SOUL.md", AgentDocuments.Find(AgentDocuments.InstanceSlots, "soul")!.FileName);
        Assert.Equal("heartbeatPrompt.md", AgentDocuments.Find(AgentDocuments.InstanceSlots, "HEARTBEAT")!.FileName);
        Assert.Null(AgentDocuments.Find(AgentDocuments.InstanceSlots, "systemPrompt"));
    }

    [Fact]
    public void EveryInstanceSlotExceptHeartbeatMapsToATemplateSlot()
    {
        Assert.Equal("personaPrompt", AgentDocuments.TemplateSlotFor("soul"));
        Assert.Equal("agentsPrompt", AgentDocuments.TemplateSlotFor("agents"));
        Assert.Equal("toolsDescription", AgentDocuments.TemplateSlotFor("tools"));
        Assert.Equal("bootstrapTemplate", AgentDocuments.TemplateSlotFor("bootstrap"));
        Assert.Equal("memoryPrompt", AgentDocuments.TemplateSlotFor("memory"));
        Assert.Equal("", AgentDocuments.TemplateSlotFor("heartbeat"));
        Assert.All(AgentDocuments.InstanceSlots.Select(slot => slot.Key), key =>
        {
            var mapped = AgentDocuments.TemplateSlotFor(key);
            if (mapped.Length == 0) return;
            Assert.NotNull(AgentDocuments.Find(AgentDocuments.TemplateSlots, mapped));
        });
    }

    [Fact]
    public void FingerprintIsStableOrderIndependentAndWhitespaceSensitive()
    {
        var baseline = new Dictionary<string, string?> { ["systemPrompt"] = "a", ["personaPrompt"] = "b" };
        var reordered = new Dictionary<string, string?> { ["personaPrompt"] = "b", ["systemPrompt"] = "a" };
        Assert.Equal(AgentDocuments.Fingerprint(baseline), AgentDocuments.Fingerprint(reordered));
        Assert.Equal(64, AgentDocuments.Fingerprint(baseline).Length);
        Assert.NotEqual(AgentDocuments.Fingerprint(baseline),
            AgentDocuments.Fingerprint(new Dictionary<string, string?> { ["systemPrompt"] = "a ", ["personaPrompt"] = "b" }));
        Assert.NotEqual(AgentDocuments.Fingerprint(baseline),
            AgentDocuments.Fingerprint(new Dictionary<string, string?> { ["systemPrompt"] = "a" }));
        // An explicitly null document is the same as an absent one.
        Assert.Equal(AgentDocuments.Fingerprint(new Dictionary<string, string?> { ["systemPrompt"] = null }),
            AgentDocuments.Fingerprint(new Dictionary<string, string?>()));
        // Case matters: document text is code, not prose.
        Assert.NotEqual(AgentDocuments.Fingerprint(new Dictionary<string, string?> { ["systemPrompt"] = "A" }),
            AgentDocuments.Fingerprint(new Dictionary<string, string?> { ["systemPrompt"] = "a" }));
    }

    [Fact]
    public void OnlyRealDifferencesCountAsInstanceOverrides()
    {
        Assert.False(AgentDocuments.IsOverride("same", "same"));
        Assert.False(AgentDocuments.IsOverride("same\n", "same"));
        Assert.False(AgentDocuments.IsOverride("same", "same\r\n"));
        Assert.False(AgentDocuments.IsOverride("", ""));
        Assert.False(AgentDocuments.IsOverride(null, null));
        Assert.True(AgentDocuments.IsOverride("edited", "same"));
        Assert.True(AgentDocuments.IsOverride("same", ""));
        Assert.True(AgentDocuments.IsOverride(" same", "same"));
        Assert.True(AgentDocuments.IsOverride("same ", "same"), "尾随空格是真实编辑，不能被当成未修改");
    }
}
