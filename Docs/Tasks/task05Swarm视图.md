# Task 05 — Swarm 视图设计方案

> **状态：** ✏️ 设计中
> **依赖：** Task 04 (蜂群模式设计)、D07 (桌面端 MVP)
> **目标：** 在桌面端窗口态（Command Center）实现 Swarm 多 Agent 可视化与指挥交互
>
> **⚠️ 视觉对齐：** Swarm 视图是窗口态的核心板块之一（精灵的"大脑解剖图"）。视觉风格需遵循 [task18定位.md](../Tasks/task18定位.md) 的布丁美学：奶油黄/抹茶绿/焦糖棕配色、圆润布丁节点、弹性动画。Agent 角色化设计（Leader 焦糖布丁、Scholar 蓝莓布丁等）应体现在节点视觉中。

---

## 一、设计目标

打造**"数字化指挥中心"**体验：

- 不是展示日志，而是展示一个**有生命力的虚拟专家团队**
- 用户扮演**指挥官**，实时观察、干预、指挥多 Agent 协作
- Agent 之间的通信、质疑、博弈过程要**可见、可控、可回溯**

---

## 二、核心视觉系统

### 2.1 节点拓扑图（Swarm Topology）

核心视觉载体，用**节点图**取代简单列表：

| 元素 | 视觉设计 |
|---|---|
| **Agent 节点** | 发光的"布丁节点"，大小反映层级（Leader 最大） |
| **通信连线** | 带箭头的流光效果，粒子从发送方流向接收方 |
| **层级关系** | Leader 在上，Worker 环绕/在下，子 Agent 嵌套在父 Agent 容器内 |

**节点状态视觉：**

| 状态 | 颜色 | 特效 |
|---|---|---|
| Idle | 灰色半透明 | 静止，磨砂质感 |
| Thinking | 琥珀黄 `#F5A623` | 呼吸发光 + 旋转粒子环 |
| ToolExecuting | 天蓝 `#4A90D9` | 稳定脉冲 |
| Completed | 抹茶绿 `#7ED321` | 弹跳 + 绿色粒子烟花 |
| Error | 警告红 `#D0021B` | 震动颤感 + 裂纹 |
| Offline | 深灰 `#444` | 淡出消失 |

### 2.2 连线动画（Energy Flow）

- **Leader → Worker（指令）：** 光点从上往下流动
- **Worker → Leader（汇报）：** 光点从下往上流动
- **Worker ↔ Worker（讨论）：** 弧形横向连线，淡紫色
- **阻塞中：** 断续红色脉冲
- **触发执行：** 绿色能量流冲击点亮目标节点

### 2.3 弹幕思维层（Thought Overlay）

解决多 Agent 并行时的信息过载问题——中间思考以弹幕形式轻量呈现：

**三级弹幕：**

| 级别 | 类型 | 表现 | 示例 |
|---|---|---|---|
| 一级 | 背景思绪 | 半透明，缓慢滑过 | "正在读取 `User.cs`..." |
| 二级 | 对话建议 | Agent 气泡，弧形轨迹 | `[Coder→Reviewer]`: "建议用泛型" |
| 三级 | 冲突高亮 | 中央对撞，粒子火花 | 两个 Agent 严重分歧 |

**弹幕浓度控制（用户可调）：**

- **Quiet** — 只显示关键里程碑
- **Balanced** — 主要讨论和质疑
- **Brainstorm** — 全量弹幕，完整思维流

---

## 三、桌面端布局（Avalonia）

### 3.1 Swarm 视图总体布局

```
┌──────────────────────────────────────────────────────────────────────┐
│ [Editor | Swarm]     PuddingCode          Provider: [deepseek ▾]    │
├────────────┬─────────────────────────────────────┬───────────────────┤
│ Agent List │        Topology Graph                │   Agent Detail    │
│            │                                      │                   │
│ 👑 Leader  │       ┌──────────────┐               │ Name: Leader      │
│   ● Active │       │  👑 Leader   │               │ Role: Leader      │
│            │       │  ● Active    │               │ Model: deepseek-R │
│ 🐝 w-auth │       │  deepseek-R  │               │ Status: ● Active  │
│   ● Think  │       └──────┬───────┘               │ Task: 编排中       │
│            │         ┌────┼────┐                  │ Tokens: 1,234     │
│ 🐝 w-test │      ┌──┴──┐ ┌┴────┐ ┌─┴───┐        │ Context: ███░ 62% │
│   ○ Idle   │      │ w-1 │ │w-2 │ │ w-3 │        │                   │
│            │      │●Thi │ │○Idl│ │●Exe │        │ [Pause] [Resume]  │
│ [+Worker]  │      └─────┘ └────┘ └─────┘        │ [Kill]            │
│            │                                      │                   │
│            │    ~~~~ 弹幕层（半透明飘过）~~~~       │ Assign Task:      │
│            │                                      │ [__________] [Go] │
├────────────┴─────────────────────────────────────┴───────────────────┤
│ Swarm Event Log                                                      │
│ [13:25:01] Leader: 分配 task-001 → worker-auth                       │
│ [13:25:03] worker-auth: 开始分析 AuthService.cs                       │
│ [13:25:05] worker-test: task-002 已完成                               │
└──────────────────────────────────────────────────────────────────────┘
```

### 3.2 特殊交互模式

**辩论台（Duel View）：** 两个 Agent 产生质疑时，自动分屏对垒展示双方思路

**共识进度条（Consensus Meter）：** 屏幕顶端，从红黄（分歧）逐渐变绿（一致）

**语义缩放（Semantic Zoom）：**
- 默认：二层子 Agent 缩小为小圆点
- 悬停/点击父节点：展开内部拓扑
- 层级 ≥ 3 时：红色警戒，限制继续派生

---

## 四、CLI 端布局（Spectre.Console）

使用 `Layout` + `Live Display` 在终端实现极客风格：

```text
┌── [ ARCHITECT ] ────────────────┐  ┌── [ CODER ] ─────────────────────┐
│ 💭 Planning database schema...   │  │ ⌨️ Generating UserEntity.cs...   │
│ [▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒] 80%         │  │ [▒▒▒▒▒▒░░░░░░░░░░] 30%         │
└──────────────────────────────────┘  └──────────────────────────────────┘
```

**质疑以缩进树表示：**

```text
[✓] Task: 实现文件上传
└── [Coder]: 编写 UploadHandle.cs
    └── [!] [Critic]: 检测到内存溢出风险!
        └── [Coder]: 修正逻辑，改用流式上传。
```

- 不同 Agent 使用不同边框颜色（Coder 青色、QA 紫色、Leader 金色）
- 侧边栏 20 字符宽的"思维垂直通道"，模拟弹幕效果

---

## 五、Agent 间通信协议

### 5.1 辩论协议（Debate Protocol）

引入**对抗式推理（Adversarial Reasoning）**：

1. **Proposal（提议）** — Agent 提出方案
2. **Challenge（质疑）** — 其他 Agent 发现漏洞并反驳
3. **Concede（认同）** — Agent 接受批评并修正
4. **Consensus（共识）** — 全员 ACK，执行方案

**头脑风暴模式（Round-Robin）：**
Architect A 提方案 → Architect B 找缺点 → Architect C 综合总结

### 5.2 弹幕数据结构

```csharp
public record SwarmBullet(
    string SenderId,
    string ReceiverId,
    string Content,
    BulletType Type,
    int Intensity       // 情感强度，决定弹幕速度和特效
);

public enum BulletType { Thought, Proposal, Challenge, Consensus }
```

---

## 六、指挥官模式（Commander Mode）

用户以"上帝视角"控制整个蜂群：

| 操作 | 描述 | 视觉反馈 |
|---|---|---|
| **全局暂停** | 一键冻结所有 Agent | 界面变黑白色调 |
| **分工指派** | 拖拽任务到 Agent 节点 | 节点产生"张嘴吸引"动画 |
| **断开连线** | 强制中止 Agent 间辩论 | 连线断裂特效 |
| **手动连线** | 将任务从 W-1 拨给 W-2 | 磁吸重连动画 |
| **注入消息** | 双击背景发送上帝消息 | 金色弹幕，所有节点向其聚拢 |
| **手动接管** | 直接控制某个 Worker | 节点变亮紫色 + 星形标记 |

---

## 七、编排可视化

### 7.1 动态编排树（Dynamic Planning Tree）

- Leader 拆解任务时，节点下方实时"生长"子任务节点
- 执行顺序用蓝色"逻辑流"连线表示

### 7.2 预演路径（Ghost Path）

- 计划中的未来步骤：灰色虚线
- 执行中：虚线逐渐填充为实线
- 计划失败：虚线红闪消失，Leader 长出新路径

### 7.3 甘特图泳道（Gantt Lane View）

底部或侧边轻量级实时甘特图，支持拖拽调整任务顺序：

| Agent | 时间轴 |
|---|---|
| **Leader** | `[ 🧠 规划 ][ ⚖️ 决策 ]` |
| **Worker-1** | `··········[ 📂 读文件 ][ ⚙️ 处理 ]` |
| **Worker-2** | `························[ 🧪 测试 ]` |

---

## 八、高阶特性（远期）

| 特性 | 描述 | 优先级 |
|---|---|---|
| **时间轴回溯** | 拖动滑块回放过去 5 分钟的拓扑变化 | P2 |
| **上下文压力监控** | 节点内水位计显示 Context Window 占用率 | P1 |
| **协作热力路径** | 通信频繁的连线越亮 | P2 |
| **知识蒸馏快照** | 一键生成当前 Swarm 争论的三句话摘要 | P2 |
| **信任度仪表盘** | Agent 多次被驳回则信任分下降，节点变暗 | P3 |
| **代码实验室分屏** | 选中 Worker 后滑出实时代码预览 | P2 |
| **成本追踪器** | 赛车计费表风格的实时费用跳动 | P1 |
| **思维词云** | 拓扑图背景浮现高频技术关键词 | P3 |
| **音效反馈** | 质疑 → 咔哒声；共识 → 布丁弹跳声 | P3 |

---

## 九、技术架构

### 9.1 事件总线（Event Bus）

```
Core 层                    UI 层
┌───────────────┐         ┌──────────────────┐
│ SwarmContext   │─events→ │ Avalonia/SkiaSharp│
│               │         │ (Canvas 绘制)     │
│ Agent A ←→ B  │─events→ │ Spectre.Console   │
│               │         │ (Live Display)    │
└───────────────┘         └──────────────────┘
```

1. **Core 层：** Agent 每步向 `ISwarmObserver` 发布事件（`AgentStarted`, `MessageSent`, `TaskFinished`）
2. **桌面端：** 通过 `SkiaSharp` / `DrawingContext` 绘制高性能节点动画
3. **CLI 端：** 监听事件触发 `ctx.Refresh()` 重渲染

### 9.2 核心数据模型

```csharp
// 蜂群上下文
public class SwarmContext
{
    public int MaxActiveAgents { get; set; } = 10;

    public async Task SendMessage(string fromId, string toId, string content)
    {
        UIEvents.Publish(new SwarmBullet(fromId, toId, content, ...));
        var target = FindAgent(toId);
        await target.ReceiveMessage(fromId, content);
    }

    public void OnAgentIdle(string agentId)
    {
        var nextTask = GlobalTaskQueue.Dequeue();
        if (nextTask != null) Leader.Assign(agentId, nextTask);
    }
}

// 编排任务图（DAG）
public class SwarmPlanGraph
{
    public List<SwarmTaskNode> Nodes { get; set; }
    public List<DependencyLink> Edges { get; set; }
    public void ReCalculateTopology() { ... }
}

// 任务节点
public class SwarmTaskNode
{
    public string Id { get; set; }
    public NodeStatus Status { get; set; }  // Pending, Active, Completed, Blocked
    public List<string> Dependencies { get; set; }
    public int Depth { get; set; }
    public List<SwarmTaskNode> Children { get; set; }

    public bool CanActivate(IEnumerable<string> completedIds)
        => Dependencies.All(id => completedIds.Contains(id));
}

// 状态镜像（驱动 UI 帧刷新）
public class SwarmStateMirror
{
    public Dictionary<string, AgentViewModel> ActiveAgents { get; set; }
    public List<CommunicationLink> Links { get; set; }
    public void UpdateFrame() { ... }
}
```

---

## 十、当前实现状态

### ✅ 已完成

- 双视图切换（Editor / Swarm）
- `SwarmAgent` 模型（Id, Role, Status, Model, CurrentTask, ParentId, TokensUsed）
- Swarm 视图基础布局：Agent 列表 + 拓扑图 + Detail 面板 + Event Log
- 基础控制命令：Spawn Worker, Pause, Resume, Kill, Assign Task
- Leader/Worker 拓扑分层展示

### 🚧 下一步

1. 实现 `SwarmOrchestrator`（Core 层蜂群编排器）
2. 连接真实 LLM 实例到 Worker 节点
3. 实现节点间通信（`SwarmBullet` 事件流）
4. 用 SkiaSharp 绘制连线动画和能量流
5. 实现弹幕思维层
