using PuddingPlatform.Services.StorageManagement;

namespace PuddingHost.Tests.Storage;

/// <summary>
/// 按表占用归入数据类的三条不可混淆的出口：数据类目录（可归类/可清理）、保护清单（已知且刻意
/// 不清理）、未归类（目录没覆盖）。
///
/// <para>
/// 为什么单独钉这条：运行中的存储页曾把 6 张**保护清单里已声明、但数据类目录里没有**的表
/// （ChatMessages / TokenUsageEvents / llm_gateway_usage_events / room_messages …）
/// 全部报成「未归类」，用户读到的意思变成"目录没覆盖这些表"，而事实是"这些表被保护"。
/// 实测那一桶约 0.52 GiB，其中 97.5% 属于前者。修法不是把它们塞进数据类目录
/// （那会让清理路径可达受保护表，已有测试 Catalog_Never_Maps_Protected_Tables 拦截），
/// 而是让保护清单单独出一个桶。
/// </para>
/// </summary>
public sealed class StorageDataClassSpaceMapperTests
{
    [Fact]
    public void TableInProtectionList_GoesToProtectedBucket_NotUnclassified()
    {
        var tables = new[]
        {
            new DatabaseTableSpace("ChatMessages", Bytes: 1_000, Pages: 1),
            new DatabaseTableSpace("SomeTableNobodyDeclared", Bytes: 5_000, Pages: 5),
        };

        var result = StorageDataClassSpaceMapper.Map(tables);

        var protectedBucket = result.Single(x => x.TargetId == StorageDataClassSpaceMapper.ProtectedTargetId);
        Assert.Contains("ChatMessages", protectedBucket.Tables, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1_000, protectedBucket.Bytes);
        Assert.False(protectedBucket.ManualCleanupAllowed);
        Assert.Equal(StorageDataClassSpaceMapper.ProtectedDisplayName, protectedBucket.DisplayName);

        var unclassified = result.Single(x => x.TargetId == StorageDataClassSpaceMapper.UnclassifiedTargetId);
        Assert.Contains("SomeTableNobodyDeclared", unclassified.Tables, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ChatMessages", unclassified.Tables, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedBucket_SortsBeforeUnclassified_ButAfterRealClasses()
    {
        var tables = new[]
        {
            // 未归类特意给最大体积：它仍必须排最后，否则「目录没覆盖」会伪装成最大的构成。
            new DatabaseTableSpace("SomeTableNobodyDeclared", Bytes: 9_000, Pages: 9),
            new DatabaseTableSpace("ChatMessages", Bytes: 1_000, Pages: 1),
            new DatabaseTableSpace("conversation_events", Bytes: 500, Pages: 1),
        };

        var order = StorageDataClassSpaceMapper.Map(tables).Select(x => x.TargetId).ToArray();

        Assert.Equal(StorageDataClassSpaceMapper.UnclassifiedTargetId, order[^1]);
        Assert.Equal(StorageDataClassSpaceMapper.ProtectedTargetId, order[^2]);
        Assert.DoesNotContain(StorageDataClassSpaceMapper.UnclassifiedTargetId, order[..^1]);
    }

    [Fact]
    public void DataClassDefinition_WinsOverProtectionList()
    {
        // 数据类目录一旦映射了某张表，它就有「可归类」语义；保护清单不得把已归类的表抢走。
        // 用真实的目录声明（conversation_events 已在 evidence.conversation-events 里），
        // 不手工构造定义对象，避开内部字段演进带来的测试腐化。
        var protectedObjects = new[]
        {
            new StorageDataClassCatalog.StorageProtectedObject
            {
                Label = "测试保护项",
                Tables = ["conversation_events"],
                Reason = "测试用：仅用于验证优先级",
            },
        };
        var tables = new[] { new DatabaseTableSpace("conversation_events", Bytes: 777, Pages: 1) };

        var result = StorageDataClassSpaceMapper.Map(
            tables,
            definitions: null,
            protectedObjects: protectedObjects);

        var bucket = Assert.Single(result);
        Assert.NotEqual(StorageDataClassSpaceMapper.ProtectedTargetId, bucket.TargetId);
        Assert.NotEqual(StorageDataClassSpaceMapper.UnclassifiedTargetId, bucket.TargetId);
        Assert.Equal(777, bucket.Bytes);
    }

    [Fact]
    public void IndexBytes_AreAttributedToTheirOwnerTable()
    {
        // 索引行（OwnerTable 指向所属表）必须并入该表所在的桶，否则"索引特别多的表"会被明显低估。
        var tables = new[]
        {
            new DatabaseTableSpace("conversation_events", Bytes: 100, Pages: 1),
            new DatabaseTableSpace("ix_ce_seq", Bytes: 900, Pages: 9, OwnerTable: "conversation_events"),
        };

        var bucket = StorageDataClassSpaceMapper.Map(tables)
            .Single(x => x.Tables.Contains("conversation_events", StringComparer.OrdinalIgnoreCase));

        Assert.Equal(1_000, bucket.Bytes);
        Assert.Equal(10, bucket.Pages);
    }
}
