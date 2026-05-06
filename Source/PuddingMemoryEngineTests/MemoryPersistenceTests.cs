using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingMemoryEngine;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingMemoryEngineTests;

[TestClass]
public sealed class MemoryPersistenceTests
{
    [TestMethod]
    public async Task SessionCrud_ShouldCreateReadUpdateDelete()
    {
        await using var scope = await CreateScopeAsync();
        await using var db = await scope.Factory.CreateDbContextAsync();

        var session = new SessionEntity
        {
            SessionId = Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws-session-crud",
            AgentId = "agent-alpha",
            Title = "session-title",
            Mode = "chat",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastActivityAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        var loaded = await db.Sessions.SingleOrDefaultAsync(x => x.SessionId == session.SessionId);
        Assert.IsNotNull(loaded);
        Assert.AreEqual("ws-session-crud", loaded.WorkspaceId);

        loaded.MessageCount = 3;
        await db.SaveChangesAsync();

        var updated = await db.Sessions.SingleAsync(x => x.SessionId == session.SessionId);
        Assert.AreEqual(3, updated.MessageCount);

        db.Sessions.Remove(updated);
        await db.SaveChangesAsync();

        var deleted = await db.Sessions.SingleOrDefaultAsync(x => x.SessionId == session.SessionId);
        Assert.IsNull(deleted);
    }

    [TestMethod]
    public async Task MessageTree_ShouldTraceFromLeafToRoot()
    {
        await using var scope = await CreateScopeAsync();
        await using var db = await scope.Factory.CreateDbContextAsync();

        var sessionId = Guid.NewGuid().ToString("N");
        db.Sessions.Add(new SessionEntity
        {
            SessionId = sessionId,
            WorkspaceId = "ws-message-tree",
            AgentId = "agent-tree",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastActivityAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        var root = new MessageEntity
        {
            MessageId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            Sequence = 1,
            Role = "user",
            Content = "root",
            BranchType = "MAIN",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var child = new MessageEntity
        {
            MessageId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            ParentId = root.MessageId,
            Sequence = 2,
            Role = "assistant",
            Content = "child",
            BranchType = "MAIN",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var leaf = new MessageEntity
        {
            MessageId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            ParentId = child.MessageId,
            Sequence = 3,
            Role = "assistant",
            Content = "leaf",
            BranchType = "MAIN",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        db.Messages.AddRange(root, child, leaf);
        await db.SaveChangesAsync();

        var trace = new List<string>();
        var current = await db.Messages.AsNoTracking().SingleAsync(x => x.MessageId == leaf.MessageId);
        while (current is not null)
        {
            trace.Add(current.MessageId);
            if (string.IsNullOrWhiteSpace(current.ParentId))
            {
                break;
            }

            current = await db.Messages.AsNoTracking().SingleAsync(x => x.MessageId == current.ParentId);
        }

        CollectionAssert.AreEqual(new[] { leaf.MessageId, child.MessageId, root.MessageId }, trace);
    }

    [TestMethod]
    public async Task MessageBranchType_ShouldFilterMainAndRetry()
    {
        await using var scope = await CreateScopeAsync();
        await using var db = await scope.Factory.CreateDbContextAsync();

        var sessionId = Guid.NewGuid().ToString("N");
        db.Sessions.Add(new SessionEntity
        {
            SessionId = sessionId,
            WorkspaceId = "ws-branch",
            AgentId = "agent-branch",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastActivityAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        var parentId = Guid.NewGuid().ToString("N");
        db.Messages.Add(new MessageEntity
        {
            MessageId = parentId,
            SessionId = sessionId,
            Sequence = 1,
            Role = "assistant",
            Content = "parent",
            BranchType = "MAIN",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        db.Messages.Add(new MessageEntity
        {
            MessageId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            ParentId = parentId,
            Sequence = 2,
            Role = "assistant",
            Content = "main branch",
            BranchType = "MAIN",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        db.Messages.Add(new MessageEntity
        {
            MessageId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            ParentId = parentId,
            Sequence = 3,
            Role = "assistant",
            Content = "retry branch",
            BranchType = "RETRY",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        await db.SaveChangesAsync();

        var retryRows = await db.Messages
            .AsNoTracking()
            .Where(x => x.SessionId == sessionId && x.BranchType == "RETRY")
            .OrderBy(x => x.Sequence)
            .ToListAsync();

        Assert.HasCount(1, retryRows);
        Assert.AreEqual("retry branch", retryRows[0].Content);
    }

    [TestMethod]
    public async Task MemoryCrud_ShouldSupportTagAndSupersededFiltering()
    {
        await using var scope = await CreateScopeAsync();
        await using var db = await scope.Factory.CreateDbContextAsync();

        var workspaceId = "ws-memory-crud";
        var activeId = Guid.NewGuid().ToString("N");
        var supersededId = Guid.NewGuid().ToString("N");

        db.Memories.Add(new MemoryEntity
        {
            MemoryId = activeId,
            Scope = "workspace",
            WorkspaceId = workspaceId,
            Tag = "preference",
            Content = "喜欢深色主题",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        db.Memories.Add(new MemoryEntity
        {
            MemoryId = supersededId,
            Scope = "workspace",
            WorkspaceId = workspaceId,
            Tag = "preference",
            Content = "喜欢浅色主题",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(),
        });

        await db.SaveChangesAsync();

        var byTag = await db.Memories
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.Tag == "preference")
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();

        Assert.HasCount(2, byTag);

        var superseded = await db.Memories.SingleAsync(x => x.MemoryId == supersededId);
        superseded.SupersededBy = activeId;
        await db.SaveChangesAsync();

        var activeRows = await db.Memories
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.SupersededBy == null)
            .ToListAsync();

        Assert.HasCount(1, activeRows);
        Assert.AreEqual(activeId, activeRows[0].MemoryId);
    }

    [TestMethod]
    public async Task BuildMemoryContext_ShouldRespectAgentIsolationAndParentSessionRecall()
    {
        await using var scope = await CreateScopeAsync();

        var sessionStore = new SessionMemoryStore(scope.Factory);
        var workspaceStore = new WorkspaceMemoryStore(scope.Factory);
        var engine = new MemoryEngine(sessionStore, workspaceStore, new MemoryBoundaryService());

        const string sessionId = "session-child";
        const string parentSessionId = "session-parent";
        const string workspaceId = "ws-isolation";
        const string agentA = "agent-A";
        const string agentB = "agent-B";

        sessionStore.Write(sessionId, new MemoryEntry
        {
            SessionId = sessionId,
            ParentSessionId = parentSessionId,
            WorkspaceId = workspaceId,
            AgentId = agentA,
            Tag = "preference/editor",
            Content = "当前分支偏好：开启自动保存",
            Source = "test",
            Scope = MemoryScope.Session,
        });

        sessionStore.Write(parentSessionId, new MemoryEntry
        {
            SessionId = parentSessionId,
            WorkspaceId = workspaceId,
            AgentId = agentA,
            Tag = "project/decision",
            Content = "主分支决策：优先使用 SQLite",
            Source = "test",
            Scope = MemoryScope.Session,
        });

        sessionStore.Write(sessionId, new MemoryEntry
        {
            SessionId = sessionId,
            WorkspaceId = workspaceId,
            AgentId = agentB,
            Tag = "secret",
            Content = "Agent B 的会话记忆",
            Source = "test",
            Scope = MemoryScope.Session,
        });

        workspaceStore.Write(workspaceId, new MemoryEntry
        {
            SessionId = sessionId,
            WorkspaceId = workspaceId,
            AgentId = agentA,
            Tag = "preference/editor/font",
            Content = "Consolas 14px",
            Source = "test",
            Scope = MemoryScope.Workspace,
        });

        workspaceStore.Write(workspaceId, new MemoryEntry
        {
            SessionId = sessionId,
            WorkspaceId = workspaceId,
            AgentId = agentB,
            Tag = "preference/editor/theme",
            Content = "Agent B 的主题偏好",
            Source = "test",
            Scope = MemoryScope.Workspace,
        });

        var memoryContext = engine.BuildMemoryContext(sessionId, workspaceId, agentA, parentSessionId);

        Assert.IsNotNull(memoryContext);
        StringAssert.Contains(memoryContext, "当前分支偏好：开启自动保存");
        StringAssert.Contains(memoryContext, "主分支决策：优先使用 SQLite");
        StringAssert.Contains(memoryContext, "Consolas 14px");
        Assert.IsFalse(memoryContext.Contains("Agent B 的会话记忆", StringComparison.Ordinal));
        Assert.IsFalse(memoryContext.Contains("Agent B 的主题偏好", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TagTreeIndexer_ShouldSearchPrefixAndReturnChildren()
    {
        await using var scope = await CreateScopeAsync();
        await using var db = await scope.Factory.CreateDbContextAsync();

        const string workspaceId = "ws-tag-tree";
        const string agentId = "agent-tree";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        db.Memories.AddRange(
            new MemoryEntity
            {
                MemoryId = Guid.NewGuid().ToString("N"),
                Scope = "workspace",
                WorkspaceId = workspaceId,
                AgentId = agentId,
                Tag = "preference/editor/font",
                Content = "Consolas 14px",
                Importance = 0.9,
                CreatedAt = now,
            },
            new MemoryEntity
            {
                MemoryId = Guid.NewGuid().ToString("N"),
                Scope = "workspace",
                WorkspaceId = workspaceId,
                AgentId = agentId,
                Tag = "preference/editor/theme",
                Content = "Dark+",
                Importance = 0.8,
                CreatedAt = now + 1,
            },
            new MemoryEntity
            {
                MemoryId = Guid.NewGuid().ToString("N"),
                Scope = "workspace",
                WorkspaceId = workspaceId,
                AgentId = agentId,
                Tag = "preference/shell",
                Content = "PowerShell",
                Importance = 0.7,
                CreatedAt = now + 2,
            },
            new MemoryEntity
            {
                MemoryId = Guid.NewGuid().ToString("N"),
                Scope = "workspace",
                WorkspaceId = workspaceId,
                AgentId = agentId,
                Tag = "task/priority",
                Content = "P0 first",
                Importance = 0.6,
                CreatedAt = now + 3,
            },
            new MemoryEntity
            {
                MemoryId = Guid.NewGuid().ToString("N"),
                Scope = "workspace",
                WorkspaceId = workspaceId,
                AgentId = "agent-other",
                Tag = "preference/editor/font",
                Content = "should be isolated",
                Importance = 1.0,
                CreatedAt = now + 4,
            });

        await db.SaveChangesAsync();

        var indexer = new PuddingMemoryEngine.Data.TagTreeIndexer(scope.Factory);

        var prefixHits = await indexer.SearchByTagPrefixAsync(workspaceId, agentId, "preference/editor", 20);
        Assert.HasCount(2, prefixHits);
        Assert.IsTrue(prefixHits.All(h => h.Tag.StartsWith("preference/editor", StringComparison.Ordinal)));

        var preferenceChildren = await indexer.GetTagChildrenAsync(workspaceId, agentId, "preference");
        Assert.IsTrue(preferenceChildren.Any(x => x.Tag == "preference/editor"));
        Assert.IsTrue(preferenceChildren.Any(x => x.Tag == "preference/shell"));

        var rootChildren = await indexer.GetTagChildrenAsync(workspaceId, agentId);
        Assert.IsTrue(rootChildren.Any(x => x.Tag == "preference"));
        Assert.IsTrue(rootChildren.Any(x => x.Tag == "task"));
    }

    [TestMethod]
    public async Task Fts5Search_ShouldRecallChineseKeyword()
    {
        await using var scope = await CreateScopeAsync();
        await using var db = await scope.Factory.CreateDbContextAsync();

        var sessionId = Guid.NewGuid().ToString("N");
        db.Sessions.Add(new SessionEntity
        {
            SessionId = sessionId,
            WorkspaceId = "ws-fts",
            AgentId = "agent-fts",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastActivityAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 1; i <= 100; i++)
        {
            var content = i % 5 == 0
                ? $"第{i}条：用户偏好是深色主题和紧凑布局"
                : $"第{i}条：普通消息内容";

            db.Messages.Add(new MessageEntity
            {
                MessageId = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                Sequence = i,
                Role = "assistant",
                Content = content,
                BranchType = "MAIN",
                CreatedAt = now + i,
            });
        }

        await db.SaveChangesAsync();

        var engine = new MemoryEngine(
            new SessionMemoryStore(scope.Factory),
            new WorkspaceMemoryStore(scope.Factory),
            new MemoryBoundaryService());

        var hits = await engine.SearchMessagesAsync(db, "深色主题", 10);

        Assert.IsNotEmpty(hits);
        Assert.IsTrue(hits.Any(x => (x.Content ?? string.Empty).Contains("深色主题", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RecallWithIntentAsync_ShouldUseTagTreeAndTimeWindow()
    {
        await using var scope = await CreateScopeAsync();
        await using var db = await scope.Factory.CreateDbContextAsync();

        const string workspaceId = "ws-intent";
        const string agentId = "agent-intent";
        var now = DateTimeOffset.UtcNow;

        db.Memories.AddRange(
            new MemoryEntity
            {
                MemoryId = Guid.NewGuid().ToString("N"),
                Scope = "workspace",
                WorkspaceId = workspaceId,
                AgentId = agentId,
                Tag = "project/gift/birthday",
                Content = "最近结论：生日礼物优先皮具方案并保持预算 800 元以内",
                Importance = 0.95,
                Confidence = 0.9,
                CreatedAt = now.AddDays(-2).ToUnixTimeMilliseconds(),
            },
            new MemoryEntity
            {
                MemoryId = Guid.NewGuid().ToString("N"),
                Scope = "workspace",
                WorkspaceId = workspaceId,
                AgentId = agentId,
                Tag = "project/gift/birthday",
                Content = "旧方案：三个月前考虑过香薰礼盒",
                Importance = 0.7,
                Confidence = 0.8,
                CreatedAt = now.AddDays(-120).ToUnixTimeMilliseconds(),
            });
        await db.SaveChangesAsync();

        var memoryLlm = new StubMemoryLlmClient(
            new MemoryQueryIntent
            {
                IntentType = "task_progress",
                Entities = ["生日礼物", "皮具"],
                TimeRange = "recent",
                SearchQuery = "生日礼物 皮具 进度",
                TagPrefix = "project/gift",
            });

        var engine = new MemoryEngine(
            new SessionMemoryStore(scope.Factory),
            new WorkspaceMemoryStore(scope.Factory),
            new MemoryBoundaryService(),
            scope.Factory,
            new TagTreeIndexer(scope.Factory),
            memoryLlm);

        var result = await engine.RecallWithIntentAsync(
            "生日礼物做到哪一步了？",
            workspaceId,
            agentId,
            sessionId: "session-intent",
            maxTokens: 2000);

        Assert.IsNotNull(result);
        StringAssert.Contains(result, "皮具方案");
        Assert.IsFalse(result.Contains("三个月前", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RecallWithIntentAsync_ShouldFallbackToFts5WhenIntentUnavailable()
    {
        await using var scope = await CreateScopeAsync();
        await using var db = await scope.Factory.CreateDbContextAsync();

        const string workspaceId = "ws-intent-fallback";
        const string agentId = "agent-intent-fallback";
        var sessionId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        db.Sessions.Add(new SessionEntity
        {
            SessionId = sessionId,
            WorkspaceId = workspaceId,
            AgentId = agentId,
            CreatedAt = now,
            LastActivityAt = now,
        });

        db.Messages.Add(new MessageEntity
        {
            MessageId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            Sequence = 1,
            Role = "assistant",
            Content = "用户偏好：深色主题 + 紧凑布局",
            BranchType = "MAIN",
            CreatedAt = now + 1,
        });

        await db.SaveChangesAsync();

        var memoryLlm = new StubMemoryLlmClient(null);
        var engine = new MemoryEngine(
            new SessionMemoryStore(scope.Factory),
            new WorkspaceMemoryStore(scope.Factory),
            new MemoryBoundaryService(),
            scope.Factory,
            new TagTreeIndexer(scope.Factory),
            memoryLlm);

        var result = await engine.RecallWithIntentAsync(
            "深色主题",
            workspaceId,
            agentId,
            sessionId,
            maxTokens: 2000);

        Assert.IsNotNull(result);
        StringAssert.Contains(result, "深色主题");
    }

    [TestMethod]
    public async Task PerformanceBenchmark_ShouldMeetPhase1Threshold()
    {
        await using var scope = await CreateScopeAsync();
        await using var db = await scope.Factory.CreateDbContextAsync();

        var sessionId = Guid.NewGuid().ToString("N");
        db.Sessions.Add(new SessionEntity
        {
            SessionId = sessionId,
            WorkspaceId = "ws-performance",
            AgentId = "agent-performance",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastActivityAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        await db.SaveChangesAsync();

        var writeSw = Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 1; i <= 100; i++)
        {
            db.Messages.Add(new MessageEntity
            {
                MessageId = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                Sequence = i,
                Role = "assistant",
                Content = $"性能基准消息 {i} 包含关键词 召回验证",
                BranchType = "MAIN",
                CreatedAt = now + i,
            });
        }

        await db.SaveChangesAsync();
        writeSw.Stop();

        var engine = new MemoryEngine(
            new SessionMemoryStore(scope.Factory),
            new WorkspaceMemoryStore(scope.Factory),
            new MemoryBoundaryService());

        var searchSw = Stopwatch.StartNew();
        var results = await engine.SearchMessagesAsync(db, "召回验证", 10);
        searchSw.Stop();

        Assert.IsLessThan(upperBound: 500, value: writeSw.ElapsedMilliseconds,
            message: $"100 条消息写入耗时 {writeSw.ElapsedMilliseconds}ms，超出 500ms 阈值。");
        Assert.IsLessThan(upperBound: 200, value: searchSw.ElapsedMilliseconds,
            message: $"FTS 查询耗时 {searchSw.ElapsedMilliseconds}ms，超出 200ms 阈值。");
        Assert.IsNotEmpty(results);
    }

    [TestMethod]
    public async Task JsonlWriteRead_ShouldPersistAndRestore()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var baseTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            await using var writer = new JsonlSessionWriter(baseDir: tempDir);
            var reader = new JsonlSessionReader(baseDir: tempDir);

            for (var i = 1; i <= 10; i++)
            {
                writer.Enqueue(sessionId, new JsonlEntry
                {
                    Type = i % 2 == 0 ? "assistant" : "user",
                    MessageId = $"msg-{i}",
                    SessionId = sessionId,
                    Role = i % 2 == 0 ? "assistant" : "user",
                    Content = $"content-{i}",
                    CreatedAt = baseTimestamp + i,
                });
            }

            await writer.FlushAsync();

            var rows = await reader.ReadSessionAsync(sessionId);
            Assert.HasCount(10, rows);
            Assert.AreEqual("content-1", rows[0].Content);
            Assert.AreEqual("content-10", rows[^1].Content);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    [TestMethod]
    public async Task JsonlBatchFlush_ShouldPersistCompleteFile()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var baseTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            await using var writer = new JsonlSessionWriter(baseDir: tempDir);
            var reader = new JsonlSessionReader(baseDir: tempDir);

            for (var i = 1; i <= 50; i++)
            {
                writer.Enqueue(sessionId, new JsonlEntry
                {
                    Type = "assistant",
                    MessageId = $"batch-{i}",
                    SessionId = sessionId,
                    Role = "assistant",
                    Content = $"batch-content-{i}",
                    CreatedAt = baseTimestamp + i,
                });
            }

            await writer.FlushAsync();

            var rows = await reader.ReadSessionAsync(sessionId);
            Assert.HasCount(50, rows);

            var filePath = Path.Combine(tempDir, $"{sessionId}.jsonl");
            var allLines = await File.ReadAllLinesAsync(filePath);
            Assert.HasCount(50, allLines);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    [TestMethod]
    public async Task JsonlConcurrentWrite_ShouldIsolateSessions()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var baseTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var sessionIds = new[]
            {
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"),
            };

            await using var writer = new JsonlSessionWriter(baseDir: tempDir);
            var reader = new JsonlSessionReader(baseDir: tempDir);

            await Parallel.ForEachAsync(sessionIds, async (sid, ct) =>
            {
                for (var i = 1; i <= 20; i++)
                {
                    writer.Enqueue(sid, new JsonlEntry
                    {
                        Type = "assistant",
                        MessageId = $"{sid}-m{i}",
                        SessionId = sid,
                        Role = "assistant",
                        Content = $"content-{i}",
                        CreatedAt = baseTimestamp + i,
                    });
                }

                await Task.CompletedTask;
            });

            await writer.FlushAsync();

            foreach (var sid in sessionIds)
            {
                var rows = await reader.ReadSessionAsync(sid);
                Assert.HasCount(20, rows);
                Assert.IsTrue(rows.All(x => x.SessionId == sid));
            }
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    [TestMethod]
    public async Task JsonlMalformedSkip_ShouldContinueReading()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var filePath = Path.Combine(tempDir, $"{sessionId}.jsonl");

            var oldEntry = new JsonlEntry
            {
                Type = "user",
                MessageId = "old",
                SessionId = sessionId,
                Role = "user",
                Content = "old-content",
                CreatedAt = 100,
            };

            var newEntry = new JsonlEntry
            {
                Type = "assistant",
                MessageId = "new",
                SessionId = sessionId,
                Role = "assistant",
                Content = "new-content",
                CreatedAt = 200,
            };

            var lines = new[]
            {
                JsonSerializer.Serialize(newEntry, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                "{ malformed-json-line",
                JsonSerializer.Serialize(oldEntry, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            };

            await File.WriteAllLinesAsync(filePath, lines, Encoding.UTF8);

            var reader = new JsonlSessionReader(baseDir: tempDir);
            var rows = await reader.ReadSessionAsync(sessionId);

            Assert.HasCount(2, rows);
            Assert.AreEqual("old", rows[0].MessageId);
            Assert.AreEqual("new", rows[1].MessageId);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    private static async Task<TestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .EnableSensitiveDataLogging()
            .Options;

        var factory = new TestDbContextFactory(options);
        await MemoryDbInitializer.InitializeAsync(factory);

        return new TestScope(connection, factory);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "pudding-jsonl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // ignore cleanup exceptions in tests
        }
    }

    private sealed class TestScope(SqliteConnection connection, IDbContextFactory<MemoryDbContext> factory) : IAsyncDisposable
    {
        public IDbContextFactory<MemoryDbContext> Factory { get; } = factory;

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<MemoryDbContext> options)
        : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryDbContext(options));
    }

    private sealed class StubMemoryLlmClient(MemoryQueryIntent? intent) : IMemoryLlmClient
    {
        public Task<MemoryClassification> ClassifyAsync(string messageText, CancellationToken ct = default)
            => Task.FromResult(new MemoryClassification(false, 0.1, 0.1, null, null));

        public Task<string?> SummarizeAsync(IReadOnlyList<string> memoryContents, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<MemoryQueryIntent?> ParseIntentAsync(string userMessage, CancellationToken ct = default)
            => Task.FromResult(intent);
    }
}
