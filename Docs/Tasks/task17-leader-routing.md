# Task 17: Leader 动态路由设计方案 (Smart Routing)

> **状态：** ✏️ 设计中 · **优先级：** 🟠 P1 · **依赖：** Task 04, Task 09, Task 10, D08
>
> **目标：** 将 Leader 从"全量广播派发器"升级为"按需智能路由器"。Leader 应根据任务语义、Worker 能力标签和成本预算，动态选择最优子集进行编排，实现 Plan-then-Execute 范式。

---

## 目录

1. [背景与问题](#1-背景与问题)
2. [设计目标](#2-设计目标)
3. [核心架构：Plan-then-Execute](#3-核心架构plan-then-execute)
4. [任务评估与分解引擎](#4-任务评估与分解引擎)
5. [Worker 能力匹配与路由](#5-worker-能力匹配与路由)
6. [结果汇聚模式](#6-结果汇聚模式)
7. [通信协议改进](#7-通信协议改进)
8. [可观测性增强](#8-可观测性增强)
9. [Swarm 视觉表现](#9-swarm-视觉表现)
10. [Leader System Prompt 设计](#10-leader-system-prompt-设计)
11. [实现路线图](#11-实现路线图)

---

## 1. 背景与问题

### 1.1 现状：静态全量派发

当前 `SimulateLeaderTaskAsync` 采用**静态分片**策略——Leader 收到任务后，不做任何分析，直接将任务等分给所有可用 Worker。

**触发场景复现（日志节选）：**

```log
[INFO ] leader-0: Assigned subtask-1: 思考喝牛奶好还是吃鸡蛋好 (part 1/5) → worker-1
[INFO ] leader-0: Assigned subtask-2: 思考喝牛奶好还是吃鸡蛋好 (part 2/5) → worker-2
[INFO ] leader-0: Assigned subtask-3: 思考喝牛奶好还是吃鸡蛋好 (part 3/5) → worker-3
[INFO ] leader-0: Assigned subtask-4: 思考喝牛奶好还是吃鸡蛋好 (part 4/5) → worker-4
[INFO ] leader-0: Assigned subtask-5: 思考喝牛奶好还是吃鸡蛋好 (part 5/5) → worker-5
```

### 1.2 暴露的五个核心缺陷

| # | 缺陷 | 表现 | 影响 |
| --- | --- | --- | --- |
| **C1** | 缺乏分片差异化 | 5 个 subtask 的描述文本完全一致，只有编号不同 | Worker 重复劳动，Token 浪费 |
| **C2** | 扇出无汇聚 (Fan-out without Fan-in) | Leader "summarizing" 时收到的是 5 份重复答案 | 上下文溢出，信息冗余 |
| **C3** | 状态机缺少反馈环 | Worker 直接 `Completed`，没有 Leader Review/Re-think 环节 | 无 QA 质量保障 |
| **C4** | 消息语义混淆 | Worker `unread=3` 表示指令堆积，Worker 可能未读完就开始执行 | 任务理解不完整 |
| **C5** | 缺乏全局追踪 ID | 日志仅有 `leader-0` / `worker-1` 局部 ID，多任务并行时无法区分 | 调试和可视化困难 |

### 1.3 理想编排对比

以"喝牛奶还是吃鸡蛋"为例：

| 维度 | 当前（全量广播） | 理想（智能路由） |
| --- | --- | --- |
| Worker 数量 | 5 个全部激活 | 仅激活 2~3 个 |
| 子任务描述 | `part 1/5` ~ `part 5/5`（完全相同） | `调研牛奶营养` / `调研鸡蛋营养` / `汇总对比` |
| 未用 Worker | 全部消耗 Token | 保持 `Idle`/`Sleeping`，节省 ~40% Token |
| 结果汇聚 | 5 份重复文本 | 结构化 JSON → Leader 或 Reporter 汇总 |

---

## 2. 设计目标

| 目标 | 量化指标 |
| --- | --- |
| **按需派发** | 简单任务仅激活必要 Worker，闲置 Worker 保持 Idle/Sleeping |
| **差异化分片** | 每个 subtask 的 `Action` 和 `Scope` 必须不同 |
| **成本可控** | 激活超过阈值 Worker 数时需用户确认（熔断机制） |
| **结果闭环** | 每个任务流必须产出最终输出（Leader 汇总或 Reporter 生成） |
| **可追溯** | 每条日志携带 `TraceID`，支持按任务流过滤 |

---

## 3. 核心架构：Plan-then-Execute

Leader 的职责从"参与者"转变为"资源调度器"。整体流程分为三个阶段：

```
用户任务
    │
    ▼
┌─────────────────────────────┐
│  Phase 1: Plan（计划）       │
│  ┌─────────────────────────┐│
│  │ 1. 语义解析 → 任务类型   ││
│  │ 2. 复杂度评估 → Low/Med/Hi││
│  │ 3. 能力匹配 → 候选 Worker││
│  │ 4. 路径规划 → 执行拓扑   ││
│  │ 5. 成本预估 → 熔断检查   ││
│  └─────────────────────────┘│
└──────────────┬──────────────┘
               │ 通过 / 用户确认
               ▼
┌─────────────────────────────┐
│  Phase 2: Execute（执行）    │
│  Leader 按拓扑逐步激活 Worker │
│  Worker 间可有数据依赖       │
│  支持条件分支和重试          │
└──────────────┬──────────────┘
               │
               ▼
┌─────────────────────────────┐
│  Phase 3: Aggregate（汇聚）  │
│  模式 A: Leader 自行汇总     │
│  模式 B: Reporter Worker 汇总│
│  → 最终输出给用户            │
└─────────────────────────────┘
```

---

## 4. 任务评估与分解引擎

### 4.1 Leader 的思考链 (Planning Reasoning)

Leader 收到任务后，在 `Thinking` 阶段执行以下推理步骤：

| 步骤 | 名称 | Leader 内心独白示例 | 产出 |
| --- | --- | --- | --- |
| **S1** | 语义解析 | "这是一个'营养学对比'与'决策建议'类任务。" | `TaskType: Comparative` |
| **S2** | 复杂度评估 | "对比类问题，复杂度低，不需要深度推理。" | `Complexity: Low` |
| **S3** | 子任务分解 | "需要：搜集 A 数据、搜集 B 数据、汇总对比。" | `subtasks: [...]` |
| **S4** | 能力匹配 | "搜集任务需要 `Knowledge` 标签，对比需要 `Analyst` 标签。" | `candidates: [...]` |
| **S5** | 成本/并行度权衡 | "3 个 Worker 可在 2s 完成，无需动用全部 5 个。" | `plan: { workers: 3, idle: 2 }` |

### 4.2 子任务差异化要求

每个 subtask 必须包含两个必填字段，确保不出现"复读机"式分片：

```json
{
  "subtaskId": "st-001",
  "action": "research",
  "scope": "牛奶的宏量营养素",
  "dependsOn": [],
  "assignTo": "worker-1"
}
```

| 字段 | 说明 |
| --- | --- |
| `action` | 动作类型：`research` / `analyze` / `compare` / `report` |
| `scope` | 作用域：必须与其他 subtask 不同 |
| `dependsOn` | 前置 subtask ID 列表（支持 DAG 依赖） |
| `assignTo` | 路由到的 Worker ID |

### 4.3 条件分支编排

Leader 的计划可以包含条件逻辑，形成有向无环图（DAG）：

```
1. 指派 Worker-1: 搜集牛奶数据
2. 指派 Worker-2: 搜集鸡蛋数据
3. IF Worker-1 发现数据缺失 THEN 激活 Worker-3 查备用数据源
4. ELSE Worker-3 保持 Idle
5. 将 Worker-1 + Worker-2 的结果交给 Worker-4 汇总
```

这种 DAG 式编排在 Swarm 视图中形成**波浪式路径**，而非放射状全量爆发。

---

## 5. Worker 能力匹配与路由

### 5.1 Worker 能力标签

每个 Worker（或其模板）携带能力标签：

```csharp
public record WorkerCapability(string Tag, float Proficiency); // 0.0~1.0

// 示例
// worker-1: [("Knowledge_Nutrition", 0.9), ("Data_Collection", 0.8)]
// worker-2: [("Data_Analyst", 0.9), ("Report_Generation", 0.7)]
// worker-3: [("Security_Audit", 0.9)] → 不适合营养学任务
```

### 5.2 匹配评分算法

```
Score(worker, subtask) = Σ (tag ∈ subtask.requiredTags) worker.proficiency[tag]
```

Leader 按分数降序选择 Worker，低于阈值的 Worker 标记为 **[Skip]**。

### 5.3 熔断机制

| 条件 | 行为 |
| --- | --- |
| 计划激活 Worker ≤ 3 | 直接执行 |
| 计划激活 Worker > 3 | 弹出用户确认对话框，显示预估 Token 成本 |
| 预估 Token 超出预算 | 自动降级：减少 Worker 数，或切换为串行模式 |

---

## 6. 结果汇聚模式

### 6.1 模式 A：Leader 汇总（小型任务）

```
Worker-1 ──JSON──┐
                 ├──→ Leader ──→ 最终输出
Worker-2 ──JSON──┘
```

- Worker 将结果作为结构化 JSON 返回给 Leader
- Leader 进行润色和总结，产出最终 Markdown
- **适用场景：** 子任务 ≤ 3 个，结果数据量小

### 6.2 模式 B：Reporter Worker 汇总（大型任务）

```
Worker-1 ──JSON──┐
Worker-2 ──JSON──┼──→ Reporter Worker ──→ 最终输出
Worker-3 ──JSON──┘
```

- 引入专门的 `Reporter_Template` Worker
- Leader 将所有子任务结果打包交给 Reporter
- Reporter 负责格式化、生成报告
- **适用场景：** 子任务 ≥ 4 个，结果需要深度整合

### 6.3 选择策略

| 条件 | 汇聚模式 | 理由 |
| --- | --- | --- |
| `subtasks.Count ≤ 3` | 模式 A (Leader 汇总) | Leader 上下文足够处理 |
| `subtasks.Count > 3` | 模式 B (Reporter) | 避免 Leader 上下文溢出 |
| 用户指定输出格式 (HTML/PDF) | 模式 B (Reporter) | 需要专门的格式化能力 |

---

## 7. 通信协议改进

### 7.1 双向握手 (ACK Protocol)

当前 Worker 收到指令可能未读完就开始执行。改进为：

```
Leader → Worker: Command(subtask)
Worker → Leader: ACK(subtaskId)       ← Worker 确认收到
Leader:          开始计时
Worker → Leader: Result(subtaskId, data)
Leader → Worker: Review(pass/re-think) ← Leader 质量审查
```

### 7.2 Worker 完成后状态流

当前 Worker 直接进入 `Completed`，缺少审查环节：

```
当前:   Thinking → ToolExecuting → Completed (终态)
改进:   Thinking → ToolExecuting → PendingReview → (Leader 审查)
          → Completed (通过)
          → Thinking (打回重做)
```

### 7.3 消息去重

每个任务指派仅产生**一条 Command Bubble**，避免 `unread` 堆积。状态更新使用独立的 `StatusUpdate` 消息类型，不计入 unread。

---

## 8. 可观测性增强

### 8.1 全局 TraceID

每个任务流分配唯一的 `TraceID`，所有相关日志条目均携带此 ID：

```log
[INFO ] [SWARM] [Trace:A1B2C3] leader-0: 任务类型识别：[对比研究]
[DEBUG] [SWARM] [Trace:A1B2C3] leader-0: 匹配到 2 个合适 Worker
[INFO ] [SWARM] [Trace:A1B2C3] leader-0 → worker-1: 搜集牛奶营养数据
[INFO ] [SWARM] [Trace:A1B2C3] worker-1: ACK received
[INFO ] [SWARM] [Trace:A1B2C3] worker-1 → leader-0: Result(JSON, 389 tokens)
```

### 8.2 改进后的完整日志示例

```log
[TRACE] [Trace:X7Y8Z9] leader-0: 语义解析 → TaskType=Comparative, Complexity=Low
[DEBUG] [Trace:X7Y8Z9] leader-0: 检索 Worker 能力库... 匹配: worker-1(0.9), worker-2(0.85)
[DEBUG] [Trace:X7Y8Z9] leader-0: 跳过 worker-3(Security, score=0.1), worker-4(DevOps, score=0.2)
[INFO ] [Trace:X7Y8Z9] leader-0 → worker-1: action=research, scope=牛奶宏量营养素
[INFO ] [Trace:X7Y8Z9] leader-0 → worker-2: action=research, scope=鸡蛋蛋白质吸收率
[DEBUG] [Trace:X7Y8Z9] leader-0: worker-3, worker-4, worker-5 保持 Sleep (节省约 40% Token)
[INFO ] [Trace:X7Y8Z9] worker-1: ACK → 开始执行
[INFO ] [Trace:X7Y8Z9] worker-2: ACK → 开始执行
[INFO ] [Trace:X7Y8Z9] worker-1 → leader-0: Result(JSON, 389 tokens) ✅
[INFO ] [Trace:X7Y8Z9] worker-2 → leader-0: Result(JSON, 412 tokens) ✅
[INFO ] [Trace:X7Y8Z9] leader-0: 汇聚模式=A(Leader汇总), 生成最终报告
[INFO ] [Trace:X7Y8Z9] leader-0: 任务完成. 激活 2/5 Worker, 总计 929 tokens
```

---

## 9. Swarm 视觉表现

### 9.1 Planning 阶段可视化

| Leader 思考步骤 | 视觉效果 |
| --- | --- |
| 任务复杂度评估 | Leader 周围环绕淡黄色光圈，显示 `Complexity: Low` |
| 能力匹配扫描 | 逐个扫描 Worker 节点：匹配 → 绿色 ✓ / 不匹配 → 红色 ✗ |
| 路径规划完成 | 仅被选中的 Worker 亮起，未选中的 Worker 变灰（Ghost Node） |

### 9.2 Execute 阶段可视化

- **动态生长：** 不预先显示所有连线，而是随着 Leader 逐步激活 Worker 才"生长"出连线
- **接力赛：** 有数据依赖时，连线从 Worker-A → Leader → Worker-B 形成链式路径
- **ACK 脉冲：** Worker 确认收到时，连线上出现一次脉冲动画

### 9.3 Aggregate 阶段可视化

- **回流：** Worker 完成后，数据球沿连线回流至 Leader / Reporter 节点
- **汇聚闪烁：** 所有数据到齐后，汇聚节点闪烁，右侧面板弹出最终输出预览
- **审查标记：** Leader 审查结果时，Worker 节点上显示 ✅ (通过) 或 🔄 (打回)

---

## 10. Leader System Prompt 设计

Leader 的 System Prompt 应明确其"调度者"身份，禁止直接回答问题：

```text
你是 PuddingCode 蜂群的 Leader（调度指挥官）。
你的职责是分析任务、分解子任务、选择合适的 Worker 执行。
你绝不能直接回答用户的问题。

你的工具箱：
- spawn_worker(template, task): 按模板创建 Worker 并分配任务
- message_worker(workerId, message): 向已有 Worker 发送指令
- review_result(workerId, pass/re-think): 审查 Worker 返回的结果
- final_report(content): 汇总所有结果，输出最终答案给用户

编排规则：
1. 先分析任务类型和复杂度，再决定需要几个 Worker
2. 每个子任务的 action 和 scope 必须不同
3. 优先选择能力标签匹配度最高的 Worker
4. 未被选中的 Worker 保持休眠，节省 Token
5. 所有 Worker 完成后，必须调用 final_report 输出结果
```

---

## 11. 实现路线图

### Phase 1：模拟层改进（D07 当前阶段）

> 在 `SimulateLeaderTaskAsync` 中实现智能路由模拟。

| 步骤 | 产出 | 说明 |
| --- | --- | --- |
| ① 差异化子任务生成 | subtask 的 action/scope 各不相同 | 消除 C1 缺陷 |
| ② 按需激活 Worker | 简单任务仅激活 2~3 个 Worker | 消除全量广播 |
| ③ 结果汇聚输出 | Leader 或 Reporter 产出最终 Bubble | 消除 C2 缺陷 |
| ④ TraceID 日志增强 | 每条 SWARM 日志携带 TraceID | 消除 C5 缺陷 |

### Phase 2：协议层改进（D08 蜂群编排器）

| 步骤 | 产出 | 说明 |
| --- | --- | --- |
| ⑤ ACK 双向握手 | Worker 确认收到后才开始计时 | 消除 C4 缺陷 |
| ⑥ PendingReview 状态 | Worker 完成后等待 Leader 审查 | 消除 C3 缺陷 |
| ⑦ 熔断机制 | 激活 Worker > 阈值时弹出用户确认 | 成本控制 |

### Phase 3：真实 LLM 编排（D08 + D03）

| 步骤 | 产出 | 说明 |
| --- | --- | --- |
| ⑧ Leader Orchestration Prompt | System Prompt + Tool 定义 | Leader 通过 LLM 自主决策路由 |
| ⑨ Worker 能力标签系统 | `WorkerCapability` 模型 + 匹配算法 | 与 Task 10 能力体系集成 |
| ⑩ 动态 DAG 编排 | 条件分支 + 数据依赖拓扑 | 完整的 Plan-then-Execute 闭环 |
