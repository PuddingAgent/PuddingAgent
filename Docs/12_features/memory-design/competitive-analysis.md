# 竞品调研：Claude Code vs Hermes Agent — 记忆与学习机制对比

> 调研时间：2026-07-11
> 目的：为 Pudding 的长效学习机制设计提供参考

---

## 一、总体定位差异

| 维度 | Claude Code | Hermes Agent | Pudding（当前） |
|------|-------------|-------------|----------------|
| 核心理念 | "Memory IS the harness" | "The agent that grows with you" | "完善智能体系 + 缓存命中率优化" |
| 记忆驱动 | 框架强制执行 | Agent 自主学习 | Agent 显意识 + 心跳协议 |
| SKILL 来源 | 手动编写 CLAUDE.md | Agent 自动从经验创建 | 手动创建 SKILL.md |
| 后台处理 | extractMemories + Auto-Dream | Autonomous Curator + GEPA | SubconsciousWorkerService（规划中） |

---

## 二、Claude Code 记忆架构（最值得借鉴）

### 四大记忆系统

```
CLAUDE.md ──────── 人工编写，四层优先级加载（managed>user>project>local）
Auto-Memory ────── Agent 编写，后台 extractMemories 子代理提取
Session Memory ─── 10段结构化会话笔记，替代 LLM 压缩摘要
Auto-Dream ─────── 定期合并冗余记忆、解决矛盾、重建索引
```

### 🔑 关键模式

#### 1. Frozen Snapshot（冻结快照）
> 会话开始时加载记忆 → 冻结为系统提示词的一部分 → 整个会话期间不变。
> 会话中写入的记忆立即落盘，但**不重建提示词**，等下次会话才可见。

**为什么**：保持 prompt cache 命中。提示词变化 → 缓存失效 → 每次 API 调用多付费。

**Pudding 对应**：我们已经有 PINNED 层缓存机制，但缺少"冻结快照"的明确约束。当前问题是心跳写记忆 → PINNED 变 → 缓存失效。

#### 2. Pre-Compaction Flush（压缩前冲洗）⭐
> 压缩前，注入一条合成系统消息："此会话即将压缩，请保存任何值得记住的内容。
> 优先用户偏好和重复模式，而非任务特定细节。"

**这是整个架构中最有价值的模式**。它把压缩从纯丢失事件变成了知识巩固事件。

**Pudding 可直接借鉴**：在 `TryAutoCompactAsync` 中，压缩前先触发一次快速保存。

#### 3. Background Extractor（后台提取器）
> `extractMemories` 子代理：每个查询循环后 fork 出来，分析最近消息，
> 提取原子事实，分类（user/feedback/project/reference），去重，写入 memory/ 目录。

**关键约束**：
- 主 Agent 在当轮写了记忆 → extractMemories 跳过当轮（`hasMemoryWritesSince` 检查）
- 使用 LLM 做相关性选择，**不用向量搜索**
- `MEMORY.md` 硬封顶 200 行 / 25KB

#### 4. Auto-Dream（自动梦境）⭐
> 24h + 5 个会话后触发。Fork 子代理：
> Phase 1: Orient — 读现有记忆
> Phase 2: Gather — grep 扫描新会话记录
> Phase 3: Consolidate — 合并冗余、解决矛盾、转换相对日期
> Phase 4: Prune — 删除过时条目、重建 MEMORY.md

**四门触发**（由便宜到贵）：
1. 时间门：距上次 ≥ 24h
2. 扫描节流：距上次扫描 ≥ 10min
3. 会话门：≥ 5 个新会话
4. 锁获取：PID 文件锁 + 过期检测

#### 5. 四种记忆类型 + "不要保存"清单
| 类型 | 内容 | 衰减 |
|------|------|------|
| User | 角色、技能、偏好、沟通风格 | 慢 |
| Feedback | 纠正、验证过的方法、要避免的 | 慢 |
| Project | 截止日期、决策、进行中的工作 | 快 |
| Reference | 外部系统指针（Linear, Slack） | — |

**不要保存**：能用 `git log`/`git blame` 找到的、进行中的任务状态、调试配方、对话限定细节。

#### 6. Session Memory（会话记忆）
> 10 段结构化 Markdown：
> title, current state, task spec, files/functions, workflow, errors,
> codebase docs, learnings, key results, worklog
>
> 触发：上下文窗口达 10K tokens 后首次提取，之后每 +5K tokens 或 3 次工具调用更新。

**关键**：Session Memory 替代了传统的 LLM 压缩摘要，消除了昂贵的压缩 API 调用。

---

## 三、Hermes Agent 学习架构

### 五阶段闭环

```
Planning Memory → Skill Creation → Skill Self-Improvement → FTS5 Retrieval → User Modeling
```

### 🔑 关键模式

#### 1. 三级惰性加载（Skills）
```
Level 1: 名称 + 描述        (~20 tokens)
Level 2: + 参数规范         (~200 tokens)
Level 3: + 完整执行步骤     (~1000+ tokens)
```
只在需要时展开，控制 token 消耗。

#### 2. Autonomous Curator（自主策展人）
> 后台进程，可配置调度：
> - 合并重叠技能
> - 归档过时条目
> - 写每轮质量报告
> - v0.13.0 起支持 archive/prune/list-archived

#### 3. Skill Self-Improvement
> 每次使用技能时评估步骤是否匹配当前环境。
> 过时/不完整/错误 → 原位补丁 + 记录变更。

#### 4. Pluggable Memory Backends
> v0.7.0 起，内置 SQLite 可替换为企业级存储。
> 解决合规场景。

#### 5. GEPA（研究层）
> Gradient-Enhanced Prompt Adaptation：用 DSPy 优化技能、提示词、甚至 Agent 自身代码。
> ICLR 2026 Oral。

---

## 四、对比总结表

| 模式 | Claude Code | Hermes Agent | Pudding 可借鉴？ |
|------|-------------|-------------|:---:|
| 冻结快照 | ✅ 核心设计 | ❌ | ✅ P0 |
| 压缩前冲洗 | ✅ `pre-compaction flush` | ❌ | ✅ P0 |
| 后台提取器 | ✅ `extractMemories` | ❌ (Curator 做不同的事) | ✅ P0 |
| 自动梦境(定期合并) | ✅ `Auto-Dream` | ✅ `Autonomous Curator` | ✅ P1 |
| 记忆类型分类 | ✅ 4 类型 | ❌（Honcho 用户建模） | ✅ P1 |
| 技能自创建 | ❌（手动 CLAUDE.md） | ✅ 核心特性 | ✅ P2 |
| 技能自改进 | ❌ | ✅ 原位补丁 | ✅ P2 |
| 惰性技能加载 | ❌ | ✅ 三级 | ✅ P1 |
| LLM 选记忆（非向量） | ✅ `findRelevantMemories` | ✅ FTS5 | ✅ 已有 |
| 原子记忆（一事实一文件） | ✅ | ❌ | 🟡 权衡 |
| MEMORY.md 硬封顶 | ✅ 200行/25KB | ❌ | ✅ P1 |
| Pluggable Backend | ❌（文件系统） | ✅ v0.7.0 | 🟢 远期 |
| 新鲜度警告 | ✅ >1天标注 | ❌ | ✅ P2 |

---

## 五、Pudding 应优先借鉴的 Top 5

### P0（立即纳入设计）

1. **Pre-Compaction Flush**
   - 在 `TryAutoCompactAsync` 前注入一条系统消息
   - 让 Agent 在压缩前保存关键信息
   - → 对应我们已有的 HOOK: `compaction.started`

2. **Frozen Snapshot 约束**
   - 我们已经做到了（缓存命中 97.78%）
   - 但需要文档化：会话中写记忆不触发 PINNED 重建
   - 下次会话启动时才加载新记忆

3. **Background Extractor**
   - `session.closed` HOOK → fork Flash 子代理
   - 读会话记录 → 提取原子事实 → 分类 → 去重 → 写入
   - 对应我们设计中管道1的核心功能

### P1（第二批）

4. **Auto-Dream 式的定期合并**
   - `cron.daily` HOOK
   - 四阶段：Orient → Gather → Consolidate → Prune

5. **惰性 SKILL 加载**
   - 我们已有 `agent_skill(read_file)` 的按需加载
   - 可以加索引层：`agent_skill(list)` 只返回名称+描述
   - 对应 Hermes 的 Level 1 (~20 tokens)

---

## 六、关键洞察

1. **Claude 的记忆不是"功能"，是框架本身**。去掉记忆，Agent 无法运行。Pudding 正在朝这个方向走。

2. **压缩不应该是纯损失**。Pre-compaction flush 是 Claude 最聪明的设计——在丢弃信息之前让 Agent 主动保存。

3. **后台处理不等于复杂**。Claude 的后台提取器就是一个 fork 出来的子代理 + 普通文件读写。没有向量数据库、没有复杂的 pipeline。

4. **LLM 选记忆 > 向量搜索**。两边都选择了让 LLM 读取文件清单 → 选择最相关的 → 注入上下文，而不是做 embedding 匹配。

5. **硬封顶是必要的**。MEMORY.md 200 行限制是刻意设计——防止记忆无限膨胀。Pudding 已经有 archived 机制，但缺少硬上限。
