# Agent 状态健康检查 (agent-status-health-check)

## 来源与背景

v1.1.0（2026-08-11）新增：**上下文恢复检查项**。
背景：Agent 在心跳唤醒或会话启动时不知道之前发生了什么，导致记忆连续性差（缓存命中率 66% 下降明显）。

## 检查清单

### A. 上下文恢复检查（v1.1 新增）

```
A1. 启动/唤醒时是否已执行历史检索？
    - query_session_logs 全文检索最近会话 ✓/✗
    - 检索结果是否注入当前上下文（CONTEXT-RECOVERY REPORT）✓/✗
A2. 记忆连续性是否正常？
    - 是否知道"上一个任务/最近交付/未完成项" ✓/✗
    - 用户偏好是否已加载（L3-USER-PREFERENCES）✓/✗
A3. 缓存命中率指标
    - 若可观测：PromptCacheHitTokens 占比是否 ≥ 80%（当前实测 66%，需关注）
    - 若持续 < 80% → 提示执行 session-context-recovery
```

### B. 状态检查（原有）

```
B1. 当前状态：active / waiting_user / sleeping / faulted
B2. 是否有进行中的任务与剩余预算
B3. goal.md 是否与事实一致
B4. 关键服务：记忆库、会话日志、LLM 路由是否可用
```

### C. 健康信号汇总

```
HEALTH REPORT
- 状态: ...
- 上下文恢复: A1=✓/✗ A2=✓/✗ A3=命中率...
- 进行中任务: ...
- 风险: ...
- 建议动作: (如"执行 session-context-recovery" / "无需动作")
```

## 质量门禁

- [ ] 每次心跳/启动自检包含 A 组（上下文恢复检查）
- [ ] 发现上下文断裂时给出明确补救动作（执行 session-context-recovery）
- [ ] 报告可被后续会话读取（写入 goal.md 或记忆库）
