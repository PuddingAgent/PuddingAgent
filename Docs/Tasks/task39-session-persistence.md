# task39 — 会话持久化设计

> **创建日期：** 2026-05-03
> **优先级：** P0（L1 记忆基础）
> **状态：** ✏️ 设计中
> **依赖：** task26 (Runtime 基础宿主)、task08 (记忆系统)
> **参考：** [Claude Code EP09 Session Persistence](../../Docs/claude-reviews-claude/architecture/09-session-persistence.md) — JSONL 追加写入、parent-UUID 链、双写路径

---

## 任务目标

设计并实现 Pudding Agent 的会话持久化层，支持会话的全量保存、快速恢复（resume）、分支（fork）和跨会话搜索。

## 参考设计：JSONL + parent-UUID 链

### 存储格式

借鉴 Claude Code 的 JSONL 追加写入模式——每行是一个自包含的 JSON 对象：

```
~/.pudding/sessions/{scene-id}/{session-id}.jsonl
```

每行包含 `type` 字段区分条目类型：

| Type | 说明 |
|------|------|
| `user` / `assistant` / `system` | 对话消息 |
| `tool_use` / `tool_result` | 工具调用与结果 |
| `summary` | 压缩摘要 |
| `title` | 会话标题 |
| `mode` | 会话模式（normal/coordinator） |
| `tag` | 用户标签 |
| `fork` | 分支来源 session-id |

### Parent-UUID 链

消息通过 UUID 形成链表，支持分支和子代理转录：

```
msg-A (parentUuid: null)
  └── msg-B (parentUuid: A)
        └── msg-C (parentUuid: B)
              ├── msg-D (parentUuid: C)  ← 主分支
              └── msg-D' (parentUuid: C) ← fork 分支
```

### 延迟物化

会话文件不在启动时创建，而是在**首次用户或助手消息**时才物化：
1. 初始化消息（系统提示词、记忆加载结果）缓冲在 `pendingEntries[]`
2. `materializeSessionFile()` 创建文件、写入缓冲、清空队列
3. 这防止了"打开即退出"产生空的元数据文件

### 双写路径

| 路径 | 时机 | 方式 |
|------|------|------|
| **异步队列** | 正常运行时 | `enqueueWrite()` → `scheduleDrain()`（100ms 合并） → `drainWriteQueue()` |
| **同步直接写** | 退出/崩溃恢复 | `appendEntryToFile()` → 同步 `appendFileSync()` |

异步队列是主路径——100ms 合并批量写入减少 I/O。同步路径用于退出清理和元数据重写，此时异步调度不安全。

### UUID 去重

写入前检查 UUID 是否已存在——防止恢复/重放时重复写入。

## Pudding 具体实现方案

### 为什么用 SQLite + JSONL 双模？

| 需求 | 方案 | 理由 |
|------|------|------|
| 结构化查询 | SQLite 表 | 按 Agent/场景/时间查询会话列表 |
| 完整转录 | JSONL 文件 | 追加写入性能好，文件可独立备份/迁移 |
| 消息链遍历 | SQLite `parent_uuid` 列 | SQL 递归 CTE 高效追溯 |

### 数据表设计（SQLite）

```sql
-- 会话元数据
CREATE TABLE sessions (
    id TEXT PRIMARY KEY,          -- UUID
    scene_id TEXT NOT NULL,       -- 所属场景
    agent_id TEXT NOT NULL,       -- 所属 Agent
    title TEXT,                   -- 会话标题
    mode TEXT DEFAULT 'normal',   -- normal | coordinator
    status TEXT DEFAULT 'active', -- active | archived | compacted
    jsonl_path TEXT,              -- 对应 JSONL 文件路径
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

-- 消息记录（与 JSONL 保持同步）
CREATE TABLE messages (
    id TEXT PRIMARY KEY,          -- UUID
    session_id TEXT NOT NULL,     -- 所属会话
    parent_uuid TEXT,             -- 父消息 UUID（NULL = 根）
    type TEXT NOT NULL,           -- user | assistant | tool_use | tool_result | system
    role TEXT,                    -- user | assistant | system | tool
    content TEXT,                 -- 消息内容（JSON）
    token_count INTEGER,          -- Token 估算
    created_at TEXT NOT NULL,
    FOREIGN KEY (session_id) REFERENCES sessions(id)
);

-- 会话分支
CREATE TABLE session_forks (
    id TEXT PRIMARY KEY,
    original_session_id TEXT NOT NULL,
    forked_session_id TEXT NOT NULL,
    fork_point_uuid TEXT,         -- 从哪条消息 fork
    created_at TEXT NOT NULL
);
```

### 实现步骤

1. **SessionStore** — SQLite 会话元数据 CRUD
2. **JsonlWriter** — JSONL 追加写入（异步队列 + 100ms 合并）
3. **JsonlReader** — JSONL 流式读取与恢复
4. **SessionResume** — 从 JSONL + SQLite 恢复完整会话状态
5. **SessionFork** — 基于 parent-UUID 创建会话分支
6. **DualWriteSync** — SQLite 与 JSONL 双写一致性保障

## 验收标准

1. 会话完整保存（消息 + 工具调用 + 工具结果）
2. `--resume` 恢复上次会话，上下文完整
3. 支持 fork 会话分支，不破坏原始会话
4. 退出时同步刷盘，不丢数据
5. 100 条消息的会话恢复时间 < 500ms
6. JSONL 文件可独立于 SQLite 备份和迁移

## 不做

- 跨设备同步（V2）
- 会话加密存储（V2）
- 会话导出为其他格式（Markdown/PDF，V2）
