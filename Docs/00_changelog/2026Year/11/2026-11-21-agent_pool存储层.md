---
title: agent_pool 存储层（持久子代理池 S2）
author: hyfree
date: 2026-11-21
last_reviewed: 2026-11-21
status: active
description: 为持久子代理池新增存储层：九个返回受影响行数的条件原语、为什么条件必须写进 WHERE 而不是先查后改、驱逐为何必须单语句，以及三条留给接入阶段的已知取舍。
categories: [changelog, subagent, persistence, storage]
tags: [subagent-pool, agent-pool, store, conditional-update, demand-1]
related_docs: [Docs/07_architecture/持久子代理与团队协作规划-2026-11-20.md, Docs/00_changelog/2026Year/11/2026-11-21-agent_pool表schema.md]
related_files: [Source/PuddingPlatform/Services/AgentPool/IAgentPoolEntryStore.cs, Source/PuddingPlatform/Services/AgentPool/AgentPoolEntryStore.cs, Source/PuddingPlatformTests/Services/AgentPoolEntryStoreTests.cs]
slug: agent-pool-storage-layer
draft: false
---

# agent_pool 存储层（持久子代理池 S2）

## 交付物

| 文件 | 内容 |
|---|---|
| `Services/AgentPool/IAgentPoolEntryStore.cs` | 接口 + 两个 record（`AgentPoolEntrySnapshot` / `AgentPoolInsertRequest`） |
| `Services/AgentPool/AgentPoolEntryStore.cs` | EF 实现，每次方法调用从 `IDbContextFactory` 新建并释放 context |
| `Services/AgentPoolEntryStoreTests.cs` | 9 个用例，每个独立临时库 |

DI 注册为 `Singleton`。理由写在注册处：store 无状态，且每次都从工厂新建 context，
所以「Singleton 捕获 Scoped」在这里不成立。**不是**为了省一次解析才选 Singleton。

本阶段仍无消费者——除 DI 注册外没有任何代码引用该接口，运行时行为不变。

## 核心设计：返回值是受影响行数，不是 bool

九个方法全部返回 `Task<int>`。0 行的含义是「前置条件不成立」，而不是「失败」：

| 方法 | 0 行的含义 |
|---|---|
| `TryInsertAsync` | 池满（容量检查与插入在同一事务内） |
| `TryAcquireAsync` | 归属不匹配 / 已 busy / 已软删 |
| `TryCompleteAsync` | 执行令牌不匹配或条目已不在 busy（**迟到收尾**） |
| `TrySetStatusAsync` | 当前状态不是期望状态 |
| `TryEvictOldestSleepingAsync` | 没有可驱逐的 sleeping 条目 |

用 bool 会丢掉「为什么没成」；用异常会让正常路径上的并发竞争变成日志噪音。
受影响行数同时是**并发正确性的证据**：两个线程抢同一条目，DB 里不可能出现两行非空
`active_execution_id`，因为只有一个 UPDATE 能命中。

## 为什么条件写进 WHERE，而不是先查后改

`TryAcquire` 的全部前置条件（归属会话、归属工作区、未软删、状态可迁移）**写在同一条
UPDATE 的 WHERE 里**：

```sql
UPDATE "agent_pool"
   SET "status"='busy', "active_execution_id"=$exec, "last_used_at"=$now, "version"="version"+1
 WHERE "pool_id"=$id
   AND "owner_session_id"=$ownerSession
   AND "owner_workspace_id"=$ownerWorkspace
   AND "deleted_at" IS NULL
   AND "status" IN ('idle','sleeping');
```

原因不只是「少一次往返」。先 SELECT 再 UPDATE 有两个真实后果：

1. 检查与写入之间存在窗口，两条请求可以都读到 `idle` 然后都通过；
2. 窗口内条目可能被 destroy 重建，随后按旧判断写入就会落在一个**已不是同一个**的条目上。

这也是 `TryComplete` 要求 `active_execution_id` 匹配的原因：迟到的收尾不该写进
一个已经换了主人的条目。

## 为什么驱逐必须单语句

```sql
UPDATE "agent_pool"
   SET "status"='dead', "deleted_at"=$now, "active_execution_id"=NULL, "version"="version"+1
 WHERE "pool_id" = (
    SELECT "pool_id" FROM "agent_pool"
     WHERE "status"='sleeping' AND "deleted_at" IS NULL
     ORDER BY "last_used_at" ASC LIMIT 1
 );
```

「先选最旧的一条，再按条件更新」在并发下会退化成「两次调用选到同一条」或
「都选到但只该删一条」。子查询形式让选择与写入成为同一条语句，天然不会双选。

## 一条方法论教训：变异要选能真正区分的形态

任务书原本要求用「把驱逐改成先 SELECT 再按 status 更新」作变异，预期并发用例失败。
**实测取不到红**，原因有两条：

1. 本机 SQLite 的 EF 异步实现在进程内同步完成，两个被 `await` 的调用实际串行，
   造不出交错窗口；
2. 第二次 UPDATE 自带 `WHERE status='sleeping'` 守卫，对同一行天然幂等（0 行）。

也就是说「先查后改」的**双删**风险在这种守卫下不会出现，该变异形态不可区分。
真正能红的是同族的另一种缺陷：**漏掉「只取一条」**——一条 UPDATE 用
`WHERE status='sleeping' AND deleted_at IS NULL`，一次就把整池清空。
把它注入后，新增的「三条 sleeping 只删最旧一条」用例报
`应为 <1>，实际为 <3>`。

结论：变异的价值取决于它是否落在**能被断言区分的**行为差异上。并发用例只能锁
结果不变量（≤1 且墓碑数等于受影响行数），区分不了实现形态——这一点已在测试注释里写明。

## 验证

| 步骤 | 结果 |
|---|---|
| `PuddingPlatform` / `PuddingHost` 构建 | 0 error，新文件未引入新 warning |
| `AgentPoolEntryStoreTests` + `AgentPoolSchemaBootstrapperTests` | **11/11** 通过（父代理独立复跑） |
| 变异（父代理亲做）：`AND "owner_session_id" = {ownerSessionId}` → `AND 1 = 1` | **失败 1 / 通过 8**，失败用例 `TryAcquire_WhenOwnerSessionDiffers_ReturnsZeroAndKeepsIdle`，报「归属不匹配的会话不得抢到条目」 |
| 复原 | SHA-256 比特一致 `043331D4…DAA4A46F`，`MUTATION` 残留 0，`-t:Rebuild` 后重跑 11/11 |
| 提交门禁 | 5 工程，`no new test failures` |

## 三条留给 S3 接入阶段的取舍

1. **`TryInsertAsync` 用 deferred 事务**：事务内先 `COUNT` 再 `INSERT`，两个并发插入
   在升级写锁时可能立即返回 `SQLITE_BUSY`（`busy_timeout` 只处理锁等待，不处理升级死锁）。
   当前无调用方，影响为零；接入时应改 `IMMEDIATE` 事务，或改成「插入后按 count 反查补偿」。
2. **`last_used_at` 按 ISO-8601 文本字典序排序**：若调用方混用 `+08:00` 与 `Z` 两种偏移量，
   字典序与真实时序不一致。接入时统一以 UTC 传入，或改用 `julianday()` 排序。
3. **`owner_session_id` 为 NULL 的条目永远抢不到**：SQLite 三值逻辑下 `NULL = $x` 不成立。
   这是 D-P0-1「孤儿条目只可 list/destroy」的预期行为，不是缺陷；若将来要做工作区级共享条目，
   谓词需改写成 `($s IS NULL OR owner_session_id = $s)`。

另：`TrySetStatusAsync` 刻意不写 `last_used_at`（状态迁移不是「使用」，写了会扰乱 LRU
顺序），而 `ResetStaleBusyAsync` 写 `last_used_at=now`（刚救回的条目不应立刻成为淘汰首选）。
两者口径不同是有意的。

## 已知限制

- DI 注册只经编译验证，没有容器解析冒烟用例；S5 接入时应补一条「能解析该接口」的断言。
- `IAgentPoolEntryStore` 是 `PuddingPlatform` 内部接口，没有下沉到 `PuddingCore`——
  消费方（池）与实现方同在 Platform，不像 `ISubAgentRunStore` 那样被 Runtime 直接使用。
