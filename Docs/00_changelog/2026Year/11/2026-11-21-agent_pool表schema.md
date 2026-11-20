---
title: agent_pool 表 schema（持久子代理池 S1）
author: hyfree
date: 2026-11-21
last_reviewed: 2026-11-21
status: active
description: 为持久子代理池新增 agent_pool 表的完整 schema 定义（18 列 + 3 索引），并说明本项目为何要「实体声明 + 幂等 DDL」两条路同时覆盖、以及为何不需要历史回填。
categories: [changelog, subagent, persistence, schema]
tags: [subagent-pool, agent-pool, schema, sqlite, ensure-created, demand-1]
related_docs: [Docs/07_architecture/持久子代理与团队协作规划-2026-11-20.md, Docs/00_changelog/2026Year/11/2026-11-20-子代理并发上限与模型选择开关.md]
related_files: [Source/PuddingPlatform/Data/Entities/AgentPoolEntity.cs, Source/PuddingPlatform/Services/AgentPoolSchemaBootstrapper.cs, Source/PuddingPlatform/Data/PlatformDbContext.cs, Source/PuddingHost/Hosting/PuddingApplicationInitializer.cs, Source/PuddingPlatformTests/Services/AgentPoolSchemaBootstrapperTests.cs]
slug: agent-pool-table-schema
draft: false
---

# agent_pool 表 schema（持久子代理池 S1）

## 目标与边界

持久子代理要求池条目在进程重启后仍然存在，而 `SubAgentPool` 今天是一个纯内存
`ConcurrentDictionary`：进程一退，条目全失。本阶段只做**表**这一步——建 schema，
不写任何读写方。

因此本提交的验收判据里有一条是负向的：`SubAgentPool` / `ISubAgentPool` /
`session_sub_agents` / `sub_agent_runs` **一行未改**，运行时行为不变。
唯一可观测的变化，是启动时多执行 4 条 `IF NOT EXISTS` DDL。

## 为什么必须「实体声明 + 幂等 DDL」两条路同时覆盖

本项目的 schema 不走 EF migration，走两条路：

| 场景 | 谁负责建表 |
|---|---|
| 全新数据库 | EF `EnsureCreated` 依实体声明建表 |
| 已存在的数据库 | **没人**——`EnsureCreated` 对已存在的库直接跳过，不补表 |

第二行是这里的关键，`SubAgentRunSchemaBootstrapper` 的注释已经写明过这一点。
所以只写实体声明，在当前这台机器的存量库上启动后表根本不存在；后续任何读写都会炸。
反过来只写 raw DDL，全新库的列就会与实体漂移。

结论：两边都写，并且用测试锁死「两条路径产出的列集合、列定义、索引必须完全一致」。

## 表结构（18 列）

| 列 | 类型 | 说明 |
|---|---|---|
| `Id` | INTEGER PK AUTOINCREMENT | 代理主键 |
| `pool_id` | TEXT NOT NULL | 稳定 id，改名不改主键 |
| `owner_workspace_id` | TEXT NOT NULL | 创建者工作区 |
| `name` | TEXT NOT NULL | 池内名（部分唯一索引） |
| `sub_session_id` | TEXT NOT NULL | 复用键，可与 `session_sub_agents` 关联 |
| `template_id` | TEXT NOT NULL | 模板 |
| `role` | TEXT NULL | 角色（可空） |
| `owner_session_id` | TEXT NULL | 创建者父会话，防跨会话误驱动 |
| `owner_agent_id` | TEXT NULL | 创建者 agent |
| `active_execution_id` | TEXT NULL | 当前执行令牌，防迟到收尾写错条目 |
| `status` | TEXT NOT NULL | `idle` / `busy` / `sleeping` / `dead` |
| `status_reason` | TEXT NULL | 状态原因（如 `recovered_busy`） |
| `task_count` | INTEGER NOT NULL DEFAULT 0 | 执行次数 |
| `last_success` | INTEGER NULL | 三态：NULL / 0 / 1（对应 `bool?`） |
| `created_at` | TEXT NOT NULL | ISO-8601 |
| `last_used_at` | TEXT NOT NULL | LRU 排序键 |
| `version` | INTEGER NOT NULL DEFAULT 0 | 乐观并发 |
| `deleted_at` | TEXT NULL | 墓碑（软删） |

三条索引：

```sql
CREATE UNIQUE INDEX IF NOT EXISTS "IX_agent_pool_name"
    ON "agent_pool" ("name") WHERE "deleted_at" IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "IX_agent_pool_sub_session"
    ON "agent_pool" ("sub_session_id");
CREATE INDEX IF NOT EXISTS "IX_agent_pool_status_last_used"
    ON "agent_pool" ("status", "last_used_at");
```

`name` 用**部分唯一索引**而不是普通唯一索引：软删之后同名条目还能被新建，
否则「删了就再也建不了同名池」。

## 为什么不写 ALTER 与历史回填

规划稿里原本有一条「把 `session_sub_agents.parent_session_id` 回填进
`owner_session_id`」。实施时取消了，理由有两条，都能在现场核：

1. 源码注释明确「产品未发布，不存在需升级的旧库」——没有需要迁移的存量数据；
2. 更要紧的是：池今天**从未落库**，所以 `agent_pool` 建出来必然是空表，
   回填语句的源集合永远是空集。

留着一条永远匹配 0 行的 DDL，只会让人误以为系统里有过历史数据。

## 一个刻意的形式偏离

DDL 用 `TEXT` / `INTEGER`，不写 `TEXT(64)` / `TEXT(32)`。

SQLite 不执行长度约束，而 EF 为 `MaxLength` 生成的列类型也是 `TEXT`。若 DDL 写
`TEXT(64)`，两条建表路径的 `PRAGMA table_info.type` 就会不同（`text(64)` vs `TEXT`），
比对测试会因此产生永久噪声，却换不到任何约束收益。长度上限仍由 EF 模型的
`MaxLength` 表达。

## 验证

| 步骤 | 结果 |
|---|---|
| `PuddingPlatform` / `PuddingHost` 构建 | 均 0 error |
| 相关 3 个测试类 | 7/7 通过（父代理独立复跑） |
| 两条建表路径列定义对比 | 18 列的类型 / NOT NULL / DEFAULT / PK **完全相同** |
| `name` 索引形态 | `PRAGMA index_list` 读出 `Unique=True, Partial=True` |
| 变异：`status_reason` → `status_reason_mut` | 失败 2 / 通过 2，两条断言均报出 EF 与 DDL 的差异 |
| 复原 | SHA-256 比特一致 `CEDBDF26…6779641D`，`MUTATION` 残留 0，`-t:Rebuild` 后重跑 7/7 |

## 门禁登记

提交门禁报出 2 条失败，均与本提交无关，已按 `staged-test-baseline.json` 的规则带日期登记：

- `CompactSession_DoesNotStackCompactionPrefixInNewSessionTitle`：把本次改动
  `git stash` 掉、工作树回到 `c00aa5d` 后同一用例**同样失败**（失败 1 / 通过 0）；
  `PuddingWebApiTests` 整工程跑 184 测试仅此 1 条失败。属预先存在。
- `GoalQueries_Return_Snapshot_And_Empty_Iterations`：单独跑与整工程跑均通过，
  仅在同一门禁并发跑多工程时失败，判为门禁隔离性问题。

## 已知限制

- 本阶段**没有**任何读写方；表建好后是空的，不会改变任何行为。
- `status` 取值目前只是常量，尚未有写入路径校验它。
- 后续阶段（store 层 / 池改造）需要处理跨会话归属校验、启动就绪门、迟到收尾丢弃
  三件事，设计与验收清单见 `Docs/07_architecture/持久子代理与团队协作规划-2026-11-20.md`
  与方案稿 `temp/dsh-survey-subagentpool-v2.md`。
