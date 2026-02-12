# Agent 个性与记忆系统 — 设计方案

> **参考**: OpenClaw 智能体工作区引导文件模型（AGENTS/SOUL/TOOLS/IDENTITY/USER/MEMORY）
> **适配**: Pudding 多 Agent 多场景架构

## 1. 目标

为 Pudding Agent 赋予**个性（Persona）**和**记忆（Memory）**能力，使每个 Agent 人格具备：
- 名字、风格、语气（IDENTITY + SOUL）
- 工具使用约定（TOOLS）
- 用户画像感知（USER）
- 首次引导仪式（BOOTSTRAP）
- 长期记忆 + 每日笔记（MEMORY）
- 会话压缩时自动记忆提取

## 2. OpenClaw 模型 vs Pudding 适配

| OpenClaw 文件 | 职责 | Pudding 落地位置 |
|---------------|------|-------------------|
| `AGENTS.md` | 操作说明 + 规则 | `AgentTemplate.SystemPrompt`（已有） |
| `SOUL.md` | 人设、边界、语气 | **新增** `AgentTemplate.PersonaPrompt` |
| `TOOLS.md` | 工具约定描述 | **新增** `AgentTemplate.ToolsDescription` |
| `IDENTITY.md` | 名称/风格/emoji | **新增** `AgentTemplate.DisplayName` + `AvatarEmoji` |
| `USER.md` | 用户画像 + 偏好 | **新增** `Workspace.UserProfile`（场景级） |
| `BOOTSTRAP.md` | 首次运行引导 | **新增** `AgentTemplate.BootstrapTemplate` |
| `MEMORY.md` | 长期记忆 | **新增** `AgentMemoryEntity`（type=long_term） |
| `memory/YYYY-MM-DD.md` | 每日笔记 | **新增** `AgentMemoryEntity`（type=daily） |

**关键差异**：OpenClaw 是单用户单 Agent 的全局文件模式，Pudding 需要按 **Agent 实例化**，因为一个用户可能有多个 Agent 人格、多个场景。

## 3. 数据模型变更

### 3.1 扩展 AgentTemplate（全局 + 工作区级）

在 `GlobalAgentTemplateEntity` 和 `WorkspaceAgentTemplateEntity` 中新增字段：

```csharp
// ── 个性层（Persona）─────────────────────────────────────
/// <summary>人设与语气提示词（SOUL）。定义 Agent 的性格、边界、回复风格。</summary>
public string? PersonaPrompt { get; set; }

/// <summary>工具使用约定描述（TOOLS）。解释用户自定义工具的用途和用法。</summary>
public string? ToolsDescription { get; set; }

/// <summary>首次对话引导模板（BOOTSTRAP）。新会话首轮使用的问答模板。</summary>
public string? BootstrapTemplate { get; set; }

/// <summary>Agent 展示用 Emoji（如 🤖🧠🔧）。</summary>
[MaxLength(8)]
public string? AvatarEmoji { get; set; }
```

### 3.2 扩展 Workspace（场景级用户画像）

在 `WorkspaceEntity` 中新增：

```csharp
/// <summary>用户画像（USER）。描述该场景下的用户偏好、背景、习惯。</summary>
public string? UserProfile { get; set; }
```

### 3.3 新增 AgentMemoryEntity（记忆存储）

```csharp
public class AgentMemoryEntity
{
    public long Id { get; set; }
    public string MemoryId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>所属 Agent 实例 ID。</summary>
    public string AgentInstanceId { get; set; }

    /// <summary>记忆类型：long_term / daily / session</summary>
    public string MemoryType { get; set; } = "long_term";

    /// <summary>记忆内容（Markdown 或纯文本）。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>日期（daily 类型使用，YYYY-MM-DD）。</summary>
    public string? DateKey { get; set; }

    /// <summary>重要性评分（0-100），用于提升决策。</summary>
    public int ImportanceScore { get; set; } = 50;

    /// <summary>最后访问/更新时间。</summary>
    public DateTimeOffset AccessedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

## 4. 系统提示词组装增强

### 4.1 当前流程

```
BuildSystemPrompt(template, sessionId, workspaceId, capability, instanceId)
  → 仅返回 template.SystemPrompt
```

### 4.2 增强后流程（参考 OpenClaw 分层模型）

```
BuildAgentSystemPrompt(template, workspace, memoryContext) →
  ┌─────────────────────────────────────────┐
  │ 1. IDENTITY 层   → 名称 + Emoji         │  ← template.DisplayName + AvatarEmoji
  │ 2. SOUL 层       → 人设/语气/边界       │  ← template.PersonaPrompt
  │ 3. AGENTS 层     → 操作规则/指令        │  ← template.SystemPrompt（已有）
  │ 4. TOOLS 层      → 工具约定             │  ← template.ToolsDescription
  │ 5. USER 层       → 用户画像             │  ← workspace.UserProfile
  │ 6. MEMORY 层     → 长期记忆 + 今日笔记  │  ← AgentMemory（受 token 预算裁剪）
  │ 7. RUNTIME 层    → 日期/时间/OS/模型    │  ← 运行时元数据
  └─────────────────────────────────────────┘
```

各层之间用分隔标记，方便后续启用提示词缓存（静态层可全局缓存）。

### 4.3 Token 预算控制

- 长期记忆注入上限：`agents.defaults.memory.maxLongTermTokens`（默认 2000）
- 每日笔记注入上限：`agents.defaults.memory.maxDailyTokens`（默认 1000）
- 超出部分截断 + 标记 `[...truncated]`

## 5. 记忆系统设计

### 5.1 三层记忆模型

| 层次 | 存储位置 | 生命周期 | 注入时机 |
|------|---------|---------|---------|
| **会话记忆** | 对话历史（内存/JSONL） | 会话结束即压缩 | 每轮对话自动可见 |
| **每日笔记** | `AgentMemoryEntity(type=daily)` | 按日期归档 | 当天 + 昨天的笔记注入 |
| **长期记忆** | `AgentMemoryEntity(type=long_term)` | 持久化 | 每次会话启动注入（受预算裁剪） |

### 5.2 记忆写入路径

1. **用户显式指令**："记住我偏好 TypeScript" → LLM 调用 `memory_save` 工具 → 写入长期记忆
2. **压缩时自动提取**：会话压缩前 → 静默轮次提取关键事实 → 写入每日笔记
3. **定期提升**（Dreaming 简化版）：定时任务扫描每日笔记 → 评分 → 高分条目提升为长期记忆

### 5.3 记忆工具

新增两个内置 Skill/Tool：
- `memory_search(query)` — 语义搜索相关记忆（需要嵌入模型）
- `memory_get(type, date?)` — 读取特定记忆文件

V1 阶段用简单关键词匹配替代语义搜索，V2 再引入向量索引。

## 6. 首次引导仪式（BOOTSTRAP）

### 6.1 触发条件

- Agent 实例创建后首次对话
- `BootstrapTemplate` 非空

### 6.2 引导流程

```
用户: "你好"
Agent: [检测到 BOOTSTRAP 标记]
       "嗨！在开始之前，我想更了解你。
        1. 你希望我怎么称呼你？
        2. 你偏好什么风格的回复？（正式/轻松/幽默/专业）
        3. 有什么我该知道的重要事情？"
用户: [回答]
Agent: [写入 USER profile + IDENTITY → 删除 BOOTSTRAP 标记]
       "明白了！以后请叫我 [名字]，我会以 [风格] 风格和你交流。"
```

## 7. Admin UI 变更

### 7.1 Agent 模板表单新增

在编辑抽屉中增加"个性设置"分组：

| 字段 | 组件 | 说明 |
|------|------|------|
| `avatarEmoji` | Emoji 选择器（或文本输入） | Agent 头像符号 |
| `personaPrompt` | TextArea (4行) | 人设/语气/边界 |
| `toolsDescription` | TextArea (4行) | 工具使用约定 |
| `bootstrapTemplate` | TextArea (6行) | 首次引导模板 |

### 7.2 场景设置新增

在 Workspace 详情页增加：

| 字段 | 组件 | 说明 |
|------|------|------|
| `userProfile` | TextArea (6行) | 用户画像与偏好 |

### 7.3 对话界面变更

- Agent 选择器旁显示 AvatarEmoji
- 首次对话时展示引导仪式流程

## 8. 实施路径

### Phase 1：数据模型 + 最小提示词注入（P0）

- 扩展 AgentTemplate 实体（PersonaPrompt / ToolsDescription / BootstrapTemplate / AvatarEmoji）
- 扩展 Workspace 实体（UserProfile）
- 创建 AgentMemoryEntity + 数据库迁移
- 增强 BuildSystemPrompt → 组装 IDENTITY + SOUL + AGENTS + TOOLS + USER 层
- Admin UI 表单新增字段
- **验收**：创建 Agent 时可填写个性字段，对话时系统提示词包含个性信息

### Phase 2：记忆引擎（P1）

- 实现 memory_save / memory_get 工具
- 实现会话压缩时的自动记忆提取
- 实现长期记忆 + 每日笔记注入系统提示词
- Admin UI 增加记忆查看页面
- **验收**：Agent 能记住跨会话的信息，对话中可搜索记忆

### Phase 3：完整体验（P2）

- Bootstrap 引导仪式
- 每日笔记自动整理
- 长期记忆自动提升（Dreaming 简化版）
- **验收**：首次创建 Agent 有引导仪式，记忆随使用自动积累

## 9. 技术约束

- 记忆存储在 SQLite（不引入外部向量数据库）
- V1 语义搜索使用简单关键词匹配（不依赖嵌入模型 API key）
- 系统提示词 Token 预算须可控，防止上下文浪费
- 所有新增字段向后兼容（nullable，未设置时不影响现有行为）
