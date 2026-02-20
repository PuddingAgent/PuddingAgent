# Task 10 — Agent 能力体系设计方案

> **状态：** ✏️ 设计中
> **依赖：** Task 04 (蜂群模式)、Task 09 (Agent 生命周期)
> **目标：** 定义 Agent 技能的三层架构、内置能力清单、权限模型及自适应 Token 优化策略

---

## 目录

1. [三层架构](#一三层架构)
2. [内置技能清单](#二内置技能清单)
3. [权限模型](#三权限模型)
4. [自适应 Prompt 引擎](#四自适应-prompt-引擎)
5. [SkillRegistry 实现](#五skillregistry-实现)
6. [视觉反馈](#六视觉反馈)
7. [技术栈映射](#七技术栈映射)
8. [实现路线](#八实现路线)

---

## 一、三层架构

Agent 的能力需要 **纵向贯通** 三层——LLM 决定"做什么"，软件层翻译并校验，物理层真正执行。

```
┌─────────────────────────────────────────────┐
│  第一层：LLM（认知层）                        │
│  Function Definitions (JSON Schema)          │
│  System Prompt 中声明技能清单及副作用           │
├─────────────────────────────────────────────┤
│  第二层：软件（桥梁层 / SkillRegistry）        │
│  参数校验 → 上下文注入 → 异步调度 → 权限拦截    │
├─────────────────────────────────────────────┤
│  第三层：实现（物理层）                        │
│  MessagingService · OrchestrationService     │
│  CliService · MemoryService                  │
└─────────────────────────────────────────────┘
```

### 1.1 认知层（LLM）

| 职责 | 说明 |
|---|---|
| 决策 | 大脑，决定"何时"以及"如何"使用技能 |
| 表现形式 | OpenAI 兼容 `Function Definitions` (JSON Schema) |
| 关键点 | 在 System Prompt 中声明技能清单及副作用 |

> *示例：* "你拥有 `MessageSkill`。使用 `send_message(target, content)` 与同伴沟通。遇到无法解决的逻辑时，必须通过此工具询问 Leader。"

### 1.2 桥梁层（软件）

| 职责 | 说明 |
|---|---|
| 参数校验 | 确保 Agent A 没有试图发消息给不存在的 Agent |
| 上下文注入 | 自动补充发送者名称，Agent 不需自行填写 |
| 异步调度 | 耗时技能（如运行测试）执行期间挂起 Agent 等待结果 |
| 权限拦截 | 阻止 Worker 调用 `spawn_worker` 等 Leader 专属技能 |

### 1.3 物理层（实现）

| 服务 | 职责 |
|---|---|
| `MessagingService` | 消息路由（私聊 / 广播 / Leader 咨询） |
| `OrchestrationService` | 创建、挂起、销毁 Worker |
| `CliService` | CliWrap 命令行调用，实时捕获 stdout/stderr |
| `MemoryService` | SQLite-VSS 混合检索 + Markdown 文件读写 |

---

## 二、内置技能清单

技能按 **四个 SkillSet** 分组，每个 SkillSet 携带独立的权限标记。

### 2.1 OrchestrationSet（蜂群管理 · Leader 专属）

| 函数名 | 参数 | 描述 |
|---|---|---|
| `spawn_worker` | `template`, `task`, `name?` | 从模板库实例化新 Worker |
| `delegate_task` | `target_agent_id`, `instruction` | 指派/调整 Worker 的任务 |
| `terminate_worker` | `agent_id`, `reason` | 销毁 Agent，回收资源 |
| `finalize_swarm` | — | 汇总所有 Worker 的 `MEMORY.md`，生成最终报告 |

### 2.2 SocialSet（横向通信 · 通用）

| 函数名 | 参数 | 描述 |
|---|---|---|
| `send_message` | `to`, `message` | 私聊消息（触发 UI 气泡 + 目标 Agent 上下文注入） |
| `broadcast` | `message` | 全蜂群频道广播 |
| `consult_leader` | `question` | Worker 向 Leader 请求决策或汇报阻碍 |

### 2.3 EnvironmentSet（环境执行 · 通用）

| 函数名 | 参数 | 描述 |
|---|---|---|
| `execute_command` | `command`, `args`, `workingDir?` | 通过 CliWrap 执行 Shell 命令 |
| `read_file` | `path`, `startLine?`, `count?` | 精准读取文件（支持分页，节省 Token） |
| `write_file` | `path`, `content`, `overwrite?` | 写入文件或生成 Diff 补丁 |

### 2.4 MemorySet（记忆管理 · 通用）

| 函数名 | 参数 | 描述 |
|---|---|---|
| `search_memory` | `query` | BM25 + Vector 混合检索 |
| `commit_memory` | `text`, `layer` | 写入 `memory/YYYY-MM-DD.md` 或 `MEMORY.md`，触发索引更新 |
| `get_project_context` | — | 返回项目树结构、技术栈、当前 Worker 列表 |

### 2.5 IntrospectionSet（自省 · 通用）

| 函数名 | 参数 | 描述 |
|---|---|---|
| `check_status` | — | Token 用量、剩余上下文长度、已加载技能列表 |
| `fetch_skill_docs` | `skillName?` | 按需获取技能详细用法（减少初始 Token） |
| `search_skills` | `keyword` | 搜索可用技能库（懒加载入口） |

---

## 三、权限模型

技能按角色授权，防止 Worker "越权"：

```
┌─────────────┬───────────────────┬───────────────────┐
│  SkillSet    │  Leader           │  Worker            │
├─────────────┼───────────────────┼───────────────────┤
│ Orchestration│  ✅ 完全访问       │  ❌ 禁止            │
│ Social       │  ✅ 完全访问       │  ✅ 完全访问         │
│ Environment  │  ✅ 完全访问       │  ⚠️ 作用域内         │
│ Memory       │  ✅ 完全访问       │  ✅ 完全访问         │
│ Introspection│  ✅ 完全访问       │  ✅ 完全访问         │
└─────────────┴───────────────────┴───────────────────┘
```

**关键约束：**

- Worker 的 `Environment` 技能受 **作用域隔离**（见 Task 04），只能操作分配给自己的文件范围
- `SkillRegistry` 在解析 Function Call 时自动检查 `AgentRole`，违规调用返回 `PermissionDenied`

---

## 四、自适应 Prompt 引擎

解决 **Token 焦虑** 问题——不一次性喂全部技能文档，按需精简注入。

### 4.1 五层优化策略

| # | 策略 | 机制 | 触发时机 | 效果 |
|---|---|---|---|---|
| 1 | **懒加载** (Lazy Loading) | 初始只给 `search_skills`，LLM 按需查询后才注入完整 Schema | 首轮对话 | 🧩 首轮极精简 |
| 2 | **状态切换** (State Switching) | 按生命周期阶段分组注入（Planning → 编排技能；Coding → 执行技能） | 状态机变更 | 🎯 减少无关干扰 |
| 3 | **上下文缓存** (Context Caching) | 完整技能文档放在 Static Prefix，利用云端缓存（DeepSeek/Gemini/GPT-4o） | 每次请求 | ⚡ 大幅降费 |
| 4 | **技能槽** (Skill Slots) | LLM 主动请求 `Load Skill "X"`，软件层下一轮注入；长期未用则自动驱逐 | LLM 显式请求 | 📉 按需加卸载 |
| 5 | **工具指纹** (Tool Fingerprint) | 首次发送完整 Schema，后续只保留函数名+参数类型的压缩引用 | 第二轮起 | 📉 压缩重复 |

### 4.2 动态 Capability 注入示例

Agent 实例化时，根据模板和阶段动态生成 System Message：

```markdown
## Your Capabilities
- [SKILL] Messaging: Use 'send_message(to, text)' to talk to peers.
- [SKILL] FileSystem: You can read/write files in your workspace scope.
- [SKILL] Search: Use 'search_skills(keyword)' to discover more abilities.
- [STATUS] Current Swarm: [Leader, 抹茶布丁, 焦糖蛋挞]
- [STATUS] Phase: Coding
- [STATUS] Context: 62% used (48,000 / 77,000 tokens)
```

### 4.3 技能依赖感知

如果技能 A 的结果是技能 B 的前置条件，需在 Schema 中声明：

```
run_test → get_last_error    (测试失败后可查看崩溃堆栈)
read_file → apply_diff       (必须先读取再修改)
search_memory → commit_memory (检索后可追加新记忆)
```

---

## 五、SkillRegistry 实现

### 5.1 核心职责

1. **自动发现** — 反射扫描程序集中标记 `[PuddingSkill]` 的方法
2. **Schema 生成** — 自动生成 OpenAI 兼容的 JSON Function Definition
3. **权限校验** — 执行前检查 `AgentRole` 是否有权调用
4. **调度执行** — 反序列化参数、调用实现、返回结果

### 5.2 属性定义

```csharp
/// <summary>标记一个方法为 PuddingCode 技能。</summary>
[AttributeUsage(AttributeTargets.Method)]
public class PuddingSkillAttribute(string description) : Attribute
{
    public string Description { get; } = description;

    /// <summary>允许调用此技能的角色（默认全部）。</summary>
    public AgentRole[] AllowedRoles { get; set; } = [];
}
```

### 5.3 技能实现示例

```csharp
public class TerminalSkill
{
    [PuddingSkill("在指定目录下异步执行命令行工具。",
        AllowedRoles = [AgentRole.Leader, AgentRole.Worker])]
    public async Task<string> ExecuteCommand(
        [Description("要执行的命令，如 dotnet")] string cmd,
        [Description("参数列表")] string args,
        CancellationToken ct)
    {
        var result = await Cli.Wrap(cmd)
            .WithArguments(args)
            .ExecuteBufferedAsync(ct);

        return result.StandardOutput + result.StandardError;
    }
}

public class OrchestrationSkill
{
    [PuddingSkill("从模板库创建新的 Worker Agent。",
        AllowedRoles = [AgentRole.Leader])]
    public Task<string> SpawnWorker(
        [Description("模板 ID")] string template,
        [Description("任务描述")] string task,
        [Description("可选：Agent 昵称")] string? name,
        CancellationToken ct) { ... }
}
```

### 5.4 SkillRegistry 核心流程

```
                ┌──────────────────────┐
                │   程序集扫描          │
                │   [PuddingSkill] ──→ │
                └──────────┬───────────┘
                           ▼
                ┌──────────────────────┐
                │  SkillRegistry       │
                │  ┌────────────────┐  │
                │  │ SkillEntry[]   │  │
                │  │  Name          │  │
                │  │  JsonSchema    │  │
                │  │  AllowedRoles  │  │
                │  │  MethodInfo    │  │
                │  └────────────────┘  │
                └──────────┬───────────┘
                           │
          ┌────────────────┼────────────────┐
          ▼                ▼                ▼
   GetSchemas(role)   Execute(name,args)  Search(keyword)
   按角色过滤并返回    权限校验 → 反射调用   返回匹配的技能列表
   JSON Function Def   → 返回结果           用于懒加载
```

---

## 六、视觉反馈

技能触发时，Swarm 拓扑节点需要视觉响应：

| 技能类型 | 视觉效果 | 图标 |
|---|---|---|
| **编排技能** (spawn/delegate) | 节点中心闪烁指挥棒图标，向目标伸出金色连线 | 🎯 |
| **通信技能** (send_message) | 节点两侧伸出天线电波动画，消息粒子飞向目标 | 📡 |
| **命令行技能** (execute_command) | 节点下方出现终端符号，滚动输出字符 | 💻 |
| **文件技能** (read/write) | 节点旁浮现文件名标签，写入时闪绿光 | 📄 |
| **记忆技能** (search/commit) | 节点底部出现脑图波纹，搜索时扩散，写入时收敛 | 🧠 |

---

## 七、技术栈映射

三层架构中各层对应的开源库：

| 层级 | 库 | 职责 |
|---|---|---|
| **桥梁层** | [Semantic Kernel](https://github.com/microsoft/semantic-kernel) | Function Calling 映射、插件系统、按需注入 |
| **物理层 · 命令行** | [CliWrap](https://github.com/Tyrrrz/CliWrap) | 异步进程启动、stdout/stderr 捕获、CancellationToken |
| **物理层 · 记忆** | [SQLite-VSS](https://github.com/asg017/sqlite-vec) + SQLitePCLRaw | BM25 全文 + Vector 向量混合检索 |
| **UI 层 · 桌面** | [Avalonia UI](https://avaloniaui.net/) | 跨平台窗口、Swarm 拓扑视图 |
| **UI 层 · 绘图** | [SkiaSharp](https://github.com/mono/SkiaSharp) | 高性能 Canvas 连线/粒子/动画 |
| **UI 层 · 响应** | CommunityToolkit.Mvvm | MVVM 数据绑定、状态同步 |

---

## 八、实现路线

### ✅ 已完成

- 基础 `ITool` / `IToolRegistry` 抽象（Task 03）
- `FileTool`、`ShellTool` 本地工具实现
- Agent → LLM Tool Calling 闭环

### 🚧 下一步

| 优先级 | 任务 | 说明 |
|---|---|---|
| **P0** | `PuddingSkillAttribute` + `SkillRegistry` | 反射扫描、Schema 生成、权限校验 |
| **P0** | `SocialSet` 实现 | `send_message` → UI 气泡 + 目标 Agent 上下文注入 |
| **P1** | `ContextOrchestrator` | 根据 Agent 角色/状态动态拼接最小化 System Message |
| **P1** | `EnvironmentSet` 改造 | 在现有 Tool 基础上加作用域隔离 |
| **P2** | `MemorySet` 实现 | SQLite-VSS 混合检索集成 |
| **P2** | `OrchestrationSet` 实现 | Leader 专属编排技能 |
| **P3** | 上下文缓存对接 | 对接 DeepSeek/Gemini Context Caching API |
| **P3** | 技能视觉反馈 | SkiaSharp 绘制技能触发动画 |
