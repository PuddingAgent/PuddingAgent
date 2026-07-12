# 滑动窗口熔断机制 — 详细设计 V1

> 状态: draft
> 创建: 2026-06-20
> 作者: 默认助手 (default.global_general-assistant.823)
> 优先级: P0

---

## 1. 问题定义

当前上下文窗口使用率接近 80% 时没有自动保护机制，可能导致：
- Token 溢出、上下文混乱
- 长对话场景下 Agent 行为退化
- 缺乏可预测的恢复路径

## 2. 设计目标

| 目标 | 指标 |
|------|------|
| 自动检测 | 60 秒滑动窗口内实时计量上下文使用率 |
| 分级预警 | 60% 黄警 / 80% 红警触发熔断 |
| 安全熔断 | 熔断时触发自动压缩，保留决策关键信息 |
| 可恢复 | 熔断后有明确的恢复路径，不丢失任务状态 |
| 零侵入 | 不修改 Agent 核心推理循环，在心跳层挂载 |

## 3. 架构概览

```
Heartbeat Loop (每 60s)
  │
  ├─→ ContextWindowMonitor.SampleAsync()
  │     └─→ 采样 token 使用率，推入环形缓冲
  │
  ├─→ FuseController.Evaluate()
  │     ├─ GREEN  (<60%): 正常
  │     ├─ YELLOW (60-80%): 预警 + 广播
  │     └─ RED    (>80%):  触发熔断
  │
  └─→ [RED] AutoCompressionService.CompactAsync()
        └─→ RecoveryOrchestrator.RecoverAsync()
```

## 4. 核心组件

### 4.1 ContextWindowMonitor

```csharp
public class ContextWindowMonitor
{
    public int SamplingIntervalSeconds { get; set; } = 60;
    public double YellowThreshold { get; set; } = 0.60;
    public double RedThreshold { get; set; } = 0.80;

    private readonly CircularBuffer<double> _samples = new(5);

    public async Task<MonitorReport> SampleAsync();
}
```

- 每次心跳调用 SampleAsync()
- 通过 LLM API 获取当前 token 使用量
- 5 点环形缓冲计算趋势（上升/稳定/下降）
- 降级方案：按 ~200 条消息 ≈ 80% 估算

### 4.2 FuseController

```csharp
public enum FuseState { Green, Yellow, Red }

public FuseState Evaluate(double usage, TrendDirection trend)
{
    if (usage >= RedThreshold)  return FuseState.Red;
    if (usage >= YellowThreshold)
        return trend == TrendDirection.Rising ? FuseState.Red : FuseState.Yellow;
    return FuseState.Green;
}
```

熔断触发条件：
- **直接触发**: 使用率 ≥ 80%
- **趋势触发**: 使用率 ≥ 60% 且连续 3 次上升

### 4.3 AutoCompressionService

熔断时压缩流程：
1. 标记压缩范围（上次压缩标记 → 当前）
2. 调用 Flash 模型生成结构化摘要
3. 强制保留: goal.md 状态、task_list、decision_log、用户偏好
4. 写入 compact_summary 块
5. 清理旧上下文

### 4.4 RecoveryOrchestrator

恢复流程：
1. 注入恢复提示词（含压缩摘要）
2. 从 goal.md 恢复任务状态
3. 可选广播熔断状态给其他 agent
4. 重置 FuseState = Green

## 5. 配置

```json
{
  "SlidingWindowFuse": {
    "SamplingIntervalSeconds": 60,
    "YellowThreshold": 0.60,
    "RedThreshold": 0.80,
    "TrendSampleCount": 3,
    "CompactModel": "deepseek/deepseek-v4-flash",
    "MaxCompactionPerSession": 5
  }
}
```

## 6. 与其他系统交互

| 系统 | 交互 |
|------|------|
| HeartbeatOrchestrator | 每次心跳调用 Monitor.SampleAsync() |
| AutoCompression | 熔断时触发 CompactAsync() |
| MemoryLibrary | 读取 goal.md / 重要记忆 |
| MessageFabric | Yellow 预警 / Red 通知到聊天室 |

## 7. 实现步骤

| # | 内容 | 负责人 |
|---|------|--------|
| 1 | ContextWindowMonitor + 环形缓冲 | dev |
| 2 | FuseController + 状态机 | dev |
| 3 | 集成到 HeartbeatOrchestrator | dev |
| 4 | RecoveryOrchestrator | dev |
| 5 | 单元测试 (Green/Yellow/Red) | dev |
| 6 | 集成测试 (模拟 80%) | dev |
| 7 | goal.md 更新熔断配置 | 默认助手 |

## 8. 风险缓解

| 风险 | 缓解 |
|------|------|
| 熔断过于频繁 | MaxCompactionPerSession ≤ 5 |
| Flash 摘要质量差 | 结构化模板约束 |
| 丢失关键上下文 | 强制保留 task_list + decision_log |
| LLM 不返回 token 量 | 降级: 消息条数估算 |

## 9. 验收标准

- [ ] ≥80% 自动触发熔断压缩
- [ ] ≥60% + 上升趋势触发预警
- [ ] 熔断后 Agent 正常恢复继续任务
- [ ] 压缩摘要保留 task_list/decision_log/偏好
- [ ] 每会话熔断 ≤ 5 次
