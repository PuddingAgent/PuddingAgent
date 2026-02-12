using Microsoft.EntityFrameworkCore;
using PuddingCode.Models;
using PuddingPlatform.Data;
using PuddingPlatform.Services.TaskPlanning;

namespace PuddingPlatformTests.Services.TaskPlanning;

[TestClass]
public sealed class TaskPlanStoreTests
{
    [TestMethod]
    public async Task CreatePlanAsync_CreatesRootNode_WithDepthZeroAndDefaultMaxDepth()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();

            var store = new TaskPlanStore(db);
            var plan = await store.CreatePlanAsync(new TaskPlanCreateRequest
            {
                WorkspaceId = "default",
                RootSessionId = "session-root",
                LeaderAgentId = "leader",
                Objective = "Collect data",
            });

            Assert.AreEqual(2, plan.MaxDelegationDepth);
            Assert.AreEqual(TaskPlanStatuses.Draft, plan.Status);

            var nodes = await store.QueryNodesAsync(new TaskNodeQuery { PlanId = plan.PlanId }, CancellationToken.None);

            Assert.AreEqual(1, nodes.Count);
            Assert.IsTrue(nodes[0].TaskNodeId.StartsWith("task_"));
            Assert.IsNull(nodes[0].ParentTaskNodeId);
            Assert.AreEqual(0, nodes[0].Depth);
            Assert.AreEqual(TaskNodeStatuses.Draft, nodes[0].Status);
        }
    }

    [TestMethod]
    public async Task CreateNodeAsync_RejectsDepthGreaterThanPlanMaxDepth()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();

            var store = new TaskPlanStore(db);
            var plan = await store.CreatePlanAsync(new TaskPlanCreateRequest
            {
                WorkspaceId = "default",
                RootSessionId = "session-root",
                LeaderAgentId = "leader",
                MaxDelegationDepth = 1,
            });
            var rootNodes = await store.QueryNodesAsync(new TaskNodeQuery { PlanId = plan.PlanId });
            var root = rootNodes.Single();

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            {
                await store.CreateNodeAsync(new TaskNodeCreateRequest
                {
                    PlanId = plan.PlanId,
                    ParentTaskNodeId = root.TaskNodeId,
                    Depth = 2,
                    Objective = "too deep",
                });
            });

            Assert.IsNotNull(ex);
        }
    }

    [TestMethod]
    public async Task CreateNodeAsync_RejectsNegativeDepth()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();

            var store = new TaskPlanStore(db);
            var plan = await store.CreatePlanAsync(new TaskPlanCreateRequest
            {
                WorkspaceId = "default",
                RootSessionId = "session-root",
                LeaderAgentId = "leader",
            });
            var root = (await store.QueryNodesAsync(new TaskNodeQuery { PlanId = plan.PlanId })).Single();

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            {
                await store.CreateNodeAsync(new TaskNodeCreateRequest
                {
                    PlanId = plan.PlanId,
                    ParentTaskNodeId = root.TaskNodeId,
                    Depth = -1,
                    Objective = "invalid depth",
                });
            });

            Assert.IsNotNull(ex);
        }
    }

    [TestMethod]
    public async Task CreateNodeAsync_RejectsMissingParent()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();

            var store = new TaskPlanStore(db);
            var plan = await store.CreatePlanAsync(new TaskPlanCreateRequest
            {
                WorkspaceId = "default",
                RootSessionId = "session-root",
                LeaderAgentId = "leader",
            });

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            {
                await store.CreateNodeAsync(new TaskNodeCreateRequest
                {
                    PlanId = plan.PlanId,
                    Depth = 1,
                    Objective = "missing parent",
                });
            });

            Assert.AreEqual("Task node parent is required for CreateNodeAsync.", ex.Message);
            Assert.IsNotNull(ex);
        }
    }

    [TestMethod]
    public async Task CreateNodeAsync_RejectsDepthMismatchWithParent()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();

            var store = new TaskPlanStore(db);
            var plan = await store.CreatePlanAsync(new TaskPlanCreateRequest
            {
                WorkspaceId = "default",
                RootSessionId = "session-root",
                LeaderAgentId = "leader",
            });
            var root = (await store.QueryNodesAsync(new TaskNodeQuery { PlanId = plan.PlanId })).Single();

            await store.CreateNodeAsync(new TaskNodeCreateRequest
            {
                PlanId = plan.PlanId,
                ParentTaskNodeId = root.TaskNodeId,
                Depth = 1,
                Objective = "valid child",
            });

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            {
                await store.CreateNodeAsync(new TaskNodeCreateRequest
                {
                    PlanId = plan.PlanId,
                    ParentTaskNodeId = root.TaskNodeId,
                    Depth = 3,
                    Objective = "depth mismatch",
                });
            });

            Assert.IsNotNull(ex);
        }
    }

    [TestMethod]
    public async Task UpdateNodeStatusAsync_RecordsTerminalFieldsAndResult()
    {
        var cases = new (TaskNodeStatuses Status, string ResultSummary, string? ResultArtifactRef, string? ErrorMessage)[]
        {
            (TaskNodeStatuses.Completed, "Done", "artifact://result/1", null),
            (TaskNodeStatuses.Failed, "Failed", "artifact://result/2", "Failure: root could not finish"),
            (TaskNodeStatuses.Cancelled, "Cancelled by user", null, "Operation cancelled before execution"),
        };

        foreach (var item in cases)
        {
            using var temp = TemporaryDirectory.Create();
            var options = CreateOptions(temp.Path);

            await using (var db = new PlatformDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();

                var store = new TaskPlanStore(db);
                var plan = await store.CreatePlanAsync(new TaskPlanCreateRequest
                {
                    WorkspaceId = "default",
                    RootSessionId = "session-root",
                    LeaderAgentId = "leader",
                });
                var root = (await store.QueryNodesAsync(new TaskNodeQuery { PlanId = plan.PlanId })).Single();

                var updated = await store.UpdateNodeStatusAsync(new TaskNodeStatusUpdateRequest
                {
                    TaskNodeId = root.TaskNodeId,
                    Status = item.Status,
                    ResultSummary = item.ResultSummary,
                    ResultArtifactRef = item.ResultArtifactRef,
                    ErrorMessage = item.ErrorMessage,
                });

                Assert.AreEqual(item.Status, updated.Status);
                Assert.AreEqual(item.ResultSummary, updated.ResultSummary);
                Assert.AreEqual(item.ResultArtifactRef, updated.ResultArtifactRef);
                Assert.AreEqual(item.ErrorMessage, updated.ErrorMessage);
                Assert.IsNotNull(updated.CompletedAt);
            }
        }
    }

    [TestMethod]
    public async Task UpdateNodeStatusAsync_LeavesCompletedAtStableForTerminals_AndClearsForNonTerminal()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();

            var store = new TaskPlanStore(db);
            var plan = await store.CreatePlanAsync(new TaskPlanCreateRequest
            {
                WorkspaceId = "default",
                RootSessionId = "session-root",
                LeaderAgentId = "leader",
            });
            var root = (await store.QueryNodesAsync(new TaskNodeQuery { PlanId = plan.PlanId })).Single();

            var completed = await store.UpdateNodeStatusAsync(new TaskNodeStatusUpdateRequest
            {
                TaskNodeId = root.TaskNodeId,
                Status = TaskNodeStatuses.Completed,
                ResultSummary = "done",
            });

            Assert.IsNotNull(completed.CompletedAt);

            await Task.Delay(5);

            var failed = await store.UpdateNodeStatusAsync(new TaskNodeStatusUpdateRequest
            {
                TaskNodeId = root.TaskNodeId,
                Status = TaskNodeStatuses.Failed,
                ErrorMessage = "Failure should keep completed_at",
            });

            Assert.AreEqual(completed.CompletedAt, failed.CompletedAt);

            var running = await store.UpdateNodeStatusAsync(new TaskNodeStatusUpdateRequest
            {
                TaskNodeId = root.TaskNodeId,
                Status = TaskNodeStatuses.Running,
            });

            Assert.IsNull(running.CompletedAt);
        }
    }

    [TestMethod]
    public async Task QueryAndGetMethods_HandleCorruptedEnumsWithFallbackValues()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();

            var store = new TaskPlanStore(db);
            var plan = await store.CreatePlanAsync(new TaskPlanCreateRequest
            {
                WorkspaceId = "default",
                RootSessionId = "session-root",
                LeaderAgentId = "leader",
            });
            var root = (await store.QueryNodesAsync(new TaskNodeQuery { PlanId = plan.PlanId })).Single();

            var planEntity = await db.TaskPlanRuns.FirstAsync(item => item.PlanId == plan.PlanId);
            planEntity.Status = "not-a-real-plan-status";
            var nodeEntity = await db.TaskNodes.FirstAsync(item => item.TaskNodeId == root.TaskNodeId);
            nodeEntity.Status = "not-a-real-node-status";
            nodeEntity.AssignedToKind = "not-a-real-assignment";
            await db.SaveChangesAsync();

            var loadedPlan = await store.GetPlanAsync(plan.PlanId);
            var nodes = await store.QueryNodesAsync(new TaskNodeQuery { PlanId = plan.PlanId });

            Assert.AreEqual(TaskPlanStatuses.Draft, loadedPlan!.Status);
            Assert.AreEqual(TaskNodeStatuses.Draft, nodes.Single().Status);
            Assert.AreEqual(TaskAssignmentKinds.Unassigned, nodes.Single().AssignedToKind);
        }
    }

    [TestMethod]
    public async Task QueryNodesAsync_ReturnsPlanTreeInCreatedOrder()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();

            var store = new TaskPlanStore(db);
            var plan = await store.CreatePlanAsync(new TaskPlanCreateRequest
            {
                WorkspaceId = "default",
                RootSessionId = "session-root",
                LeaderAgentId = "leader",
            });
            var root = (await store.QueryNodesAsync(new TaskNodeQuery { PlanId = plan.PlanId })).Single();

            var nodeOne = await store.CreateNodeAsync(new TaskNodeCreateRequest
            {
                PlanId = plan.PlanId,
                ParentTaskNodeId = root.TaskNodeId,
                Depth = 1,
                Objective = "Node one",
            });
            await Task.Delay(10);

            var nodeTwo = await store.CreateNodeAsync(new TaskNodeCreateRequest
            {
                PlanId = plan.PlanId,
                ParentTaskNodeId = root.TaskNodeId,
                Depth = 1,
                Objective = "Node two",
            });
            await Task.Delay(10);

            var nodeThree = await store.CreateNodeAsync(new TaskNodeCreateRequest
            {
                PlanId = plan.PlanId,
                ParentTaskNodeId = root.TaskNodeId,
                Depth = 1,
                Objective = "Node three",
            });

            var nodes = await store.QueryNodesAsync(new TaskNodeQuery
            {
                PlanId = plan.PlanId,
                Limit = 10,
            }, ct: CancellationToken.None);

            Assert.AreEqual(4, nodes.Count);
            Assert.AreEqual(root.TaskNodeId, nodes[0].TaskNodeId);
            Assert.AreEqual(nodeOne.TaskNodeId, nodes[1].TaskNodeId);
            Assert.AreEqual(nodeTwo.TaskNodeId, nodes[2].TaskNodeId);
            Assert.AreEqual(nodeThree.TaskNodeId, nodes[3].TaskNodeId);
        }
    }

    private static DbContextOptions<PlatformDbContext> CreateOptions(string root)
    {
        var dbPath = Path.Combine(root, "platform.db");
        return new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-platform-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }
}
