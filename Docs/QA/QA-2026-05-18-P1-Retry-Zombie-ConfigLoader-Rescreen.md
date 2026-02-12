# QA 复审报告：P1-1/P1-2/P1-3 修复验证

**审阅日期**: 2026-05-18  
**审阅范围**: PriorityEventQueue 僵尸回收、retry/dead-letter 语义统一、LoadJsonAsync 异常收敛  
**审阅者**: GPT-5.3-Codex (QA)  
**结论**: **PASS_WITH_NOTES**

---

## 1. 复审对象

- `Source/PuddingRuntime/Services/Events/PriorityEventQueue.cs`
- `Source/PuddingRuntime/Services/Events/EventDispatcher.cs`
- `Source/PuddingCore/Abstractions/IPriorityEventQueue.cs`
- `Source/PuddingCore/Configuration/PuddingFileConfigLoader.cs`

---

## 2. 验证结果

### P1-1：PriorityEventQueue 僵尸事件回收

**结论：通过 ✅**

1. `DequeueAsync` 查询条件已覆盖过期 `processing` 事件：
   - `Status` 包含 `processing`
   - 且要求 `(LeaseUntil == null || LeaseUntil <= now)`
2. 回收时状态重置为 `retrying`：
   - `if (entity.Status == "processing") { entity.Status = "retrying"; ... }`
3. 保留原 `RetryCount`：
   - 回收分支未重置/递增 `RetryCount`，继续沿用既有计数
4. 已记录 Warning 日志：
   - `LogWarning("[PriorityEventQueue] Reclaimed zombie event ...")`

---

### P1-2：retry/dead-letter 语义统一

**结论：通过 ✅**

1. EventDispatcher 已移除本地 `RetryCount < 3` 裁决：
   - 失败后统一调用 `_queue.UpdateStatusAsync(..., "retrying", ...)`
   - 不再在 Dispatcher 内进行重试上限判断
2. `UpdateStatusAsync` 返回类型已统一为“最终生效状态”：
   - `IPriorityEventQueue` 接口：`Task<string> UpdateStatusAsync(...)`
   - `PriorityEventQueue` 实现返回 `entity.Status`
3. Dispatcher 已按返回状态记录 activity：
   - `finalStatus == "dead_letter"` → `RuntimeActivityStatuses.Failed`
   - 否则 → `RuntimeActivityStatuses.Retried`
4. 队列层在 `retry_count >= 3` 时转 `dead_letter`：
   - `entity.RetryCount++` 后判断 `if (entity.RetryCount >= MaxRetries)`

---

### P1-3：LoadJsonAsync 异常捕获

**结论：通过 ✅**

1. `JsonException` 已捕获并转 `ConfigLoadResult.Fail`
2. `IOException` 已捕获并转 `ConfigLoadResult.Fail`
3. `UnauthorizedAccessException` 已捕获并转 `ConfigLoadResult.Fail`
4. 错误信息包含文件名和异常消息：
   - 统一格式 `"{Path.GetFileName(path)}: ... — {ex.Message}"`
5. 调用方已检查 `loadResult.Success`：
   - `LoadLlmProvidersAsync/LoadSystemAsync/LoadSecurityAsync/LoadConnectorsAsync` 均有 `if (!loadResult.Success) return loadResult;`

---

## 3. 证据与执行

- 静态诊断（上述 4 文件）：无编译/语义错误
- 单测执行：
  - `PuddingCoreTests` 中 `PuddingFileConfigLoaderTests` 共 6 项，**全部通过**

---

## 4. 新发现问题（非阻断）

| 编号 | 严重级别 | 位置 | 问题描述 | 建议 |
|---|---|---|---|---|
| N-1 | P2 | 事件系统测试覆盖 | 未发现针对 `PriorityEventQueue/EventDispatcher` 的专项回归测试（尤其是 lease 过期回收 + dead-letter 路径） | 增加最小集成测试：1) 过期 processing 回收；2) 第3次失败转 dead-letter；3) Dispatcher activity 状态断言 |

---

## 5. 复审结论

**PASS_WITH_NOTES**  
3 个历史 P1 阻断项均已修复到位，语义一致性与错误收敛行为符合预期。当前仅有“测试覆盖不足”的 P2 改进项，不阻断本轮通过。
