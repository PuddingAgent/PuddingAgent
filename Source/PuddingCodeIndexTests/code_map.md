# PuddingCodeIndexTests CodeMAP

> **索引组件的独立测试工程**（组件化交付规程 S2/S3 的第一次真实兑现，2026-09-23）
> 目的：让 `PuddingCodeIndex` 的测试运行在**只引用它自己**的工程里
> —— 从而"**不重启宿主即可测试与开发**"（规程 R1），且测试进程**不加载** Roslyn / MSBuild / 上层程序集。
> 命名空间：`PuddingCodeIndexTests` / `PuddingCodeIndexTests.Services` / `PuddingCodeIndexTests.Services.CodeIndex` / `PuddingCodeIndexTests.Storage`

## 边界（编译期 + 运行期双重强制）

- `PuddingCodeIndexTests.csproj` 的 `ProjectReference` **恰好 1 条** → `../PuddingCodeIndex/PuddingCodeIndex.csproj`。
- **严禁**引用 `PuddingCodeIntelligence` / `PuddingRuntime` / `PuddingHost` / `PuddingAgent`，也不得引用其他组件或宿主。
- 测试框架/包版本与既有测试工程一致（`MSTest.Sdk/4.0.1`，`net10.0`，`UseVSTest`）；不新增 NuGet 依赖。
- 运行期断言见 `ComponentBoundaryTests`：本进程**已加载**、**元数据引用**、**探测路径可达**三个维度都不允许出现禁用程序集。

## 测试清单（58 用例 = 55 个迁入用例 + 3 条边界断言）

| 文件 | 用途 |
|------|------|
| `ComponentBoundaryTests.cs` | **S3/S4 边界断言**：① 探测器自检（必须能报出违规）；② 测试进程未加载（并强制加载元数据引用）Roslyn/MSBuild/上层程序集；③ 声明的依赖闭包（`*.deps.json`）不含禁用程序集 |
| `Services/CodeIndexFixture.cs` | 组件本地夹具：临时根目录 + 真实 `SqliteCodeIndexStore`（**自足**，不用上层夹具） |
| `Services/CodeIndex/CodeIndexChangeCoalescerTests.cs` | 防抖折叠（静默 500ms / 最长 2s / 2 万路径 → reconcile） |
| `Services/CodeIndex/CodeIndexChangeQueueTests.cs` | 有界队列容量与 `TryPublish` 不阻塞 |
| `Services/CodeIndex/CodeIndexChangeTestHelpers.cs` | 共享测试替身：`TestDirectory` / `MutableTimeProvider` / `CodeIndexChangeTestFactory` |
| `Services/CodeIndex/CodeIndexMaintenanceServiceTests.cs` | **U3-B1** 变更驱动维护服务：单驱动泵、置脏补跑、可见化、有界停止 |
| `Services/CodeIndex/CodeIndexMaintenanceTestDoubles.cs` | 维护服务测试替身：`RecordingCodeIndexer` / `FakeCodeIndexChangeWatcher` / `MaintenanceTestData` |
| `Services/CodeIndex/CodeIndexSchedulerTests.cs` | **U3-B1** 调度器不变量：in-flight 期间到达的请求不得丢弃、取消时重新入队 |
| `Services/CodeIndex/CodeIndexScopeStateTests.cs` | 范围状态（dirty/version/reconcile，无 IO） |
| `Services/CodeIndex/CodeIndexWatcherTests.cs` | 文件系统监视器过滤与发布 |
| `Storage/SqliteCodeIndexStoreTests.cs` | `SqliteCodeIndexStore` 项目/文件/符号/关系/引用往返，且不触碰源文件 |

## 运行

```
dotnet test Source\PuddingCodeIndexTests\PuddingCodeIndexTests.csproj
```

**不需要**启动 Core / Desktop / 任何后台服务；可在宿主运行中并发执行（不争宿主文件锁）。

> ⚠️ `ComponentBoundaryTests` 会读输出目录的 `*.dll`（与 `*.deps.json` 取交集）。
> 增删引用后请让 `bin/obj` 重新生成，避免陈旧产物造成假红。

## 来源与守恒

本工程 55 个用例由 `Source/PuddingCodeIntelligenceTests/` 迁入（`Services/CodeIndex/` 8 文件 +
`Storage/SqliteCodeIndexStoreTests.cs`），**零用例丢弃**：
`搬迁前 144 = 搬迁后 89（IntelligenceTests）+ 55（本工程）`。
