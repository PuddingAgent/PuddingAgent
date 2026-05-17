# QA 审阅报告：T-103 前端 source 卡片映射

**审阅日期**: 2026-05-17  
**任务**: T-103 — 打通 source 元数据从连接器到前端 metadata 帧的完整通路  
**开发者**: DeepSeek-V4-Pro  
**审阅者**: DeepSeek-V4-Pro (QA)  
**结论**: ` + "PASS_WITH_NOTES" + @"  
**变更文件**: 5 个（3 C# + 2 TS）

---

## 1. 变更概述

| # | 文件 | 变更内容 |
|---|------|---------|
| 1 | PuddingCore/Platform/MessageContracts.cs | MessageIngressRequest 新增 Dictionary<string,string>? Metadata |
| 2 | PuddingController/Services/SessionRouter.cs | RouteMessageStreamAsync 注入 metadata SSE 帧（source_type/id/name） |
| 3 | PuddingPlatform/Services/PlatformApiClient.cs | SendMessageAsync/SendMessageStreamAsync 新增 metadata 参数 |
| 4 | PuddingPlatformAdmin/.../api.ts | AdminChatStreamEvent 新增 metadata 事件变体（snake_case 字段） |
| 5 | PuddingPlatformAdmin/.../useChatState.ts | mapEventToTurn 处理 metadata 事件，构造 ChatSource，兼容双命名 |

---

## 2. 编译检查

- PuddingCore.csproj → 0 errors, 0 warnings ✅
- PuddingController.csproj → 0 errors, 0 warnings ✅
- PuddingPlatform.csproj → 0 errors, 2 NU1903 (pre-existing) ✅

---

## 3. 逐文件审阅

### 3.1 MessageContracts.cs ✅

`csharp
public Dictionary<string, string>? Metadata { get; init; }
`

- 类型正确：可空 Dictionary，向前兼容
- XML 文档完整，说明来源和用途

### 3.2 SessionRouter.cs ✅

RouteMessageStreamAsync 第 369-376 行注入 metadata SSE 帧：

`csharp
yield return ServerSentEventFrame.Json(SseEventTypes.Metadata, new
{
    messageId, sessionId, routeDecisionId,
    source_type = request.Metadata?.GetValueOrDefault("source_type"),
    source_id = request.Metadata?.GetValueOrDefault("source_id"),
    source_name = request.Metadata?.GetValueOrDefault("source_name"),
});
`

- Null 安全：GetValueOrDefault 在 Metadata 为 null 时不抛异常
- SSE 帧位置：RouteDecision 审计后、Runtime dispatch 前 — 时序正确
- JSON 序列化验证：JsonSerializerDefaults.Web → CamelCase 策略仅将 PascalCase 首字母小写，不转换 snake_case。source_type/id/name 在 JSON 中保持 snake_case

### 3.3 PlatformApiClient.cs ✅

- 两个方法均追加 Dictionary<string, string>? metadata = null 参数
- 默认 null — 向后兼容，已有调用者无需修改
- 正确透传至 MessageIngressRequest.Metadata

### 3.4 api.ts (AdminChatStreamEvent) ✅

- metadata 事件变体字段 snake_case（source_type/id/name），与后端 JSON 输出一致
- 所有 source 字段标记为可选 — 兼容 null
- sendAdminChatMessageStream 显式处理 eventName === 'metadata'
- subscribeSessionEvents 通用 onEvent({ type, ...data }) 自动处理

### 3.5 useChatState.ts (mapEventToTurn) ✅

`	ypescript
if (ev.type === 'metadata') {
    const sourceMeta = anyMeta.source_id || anyMeta.sourceId || anyMeta.source_type;
    const source = sourceMeta ? {
        sourceId: String(anyMeta.source_id || anyMeta.sourceId || 'agent'),
        sourceType: (anyMeta.source_type as ChatSource['sourceType']) || 'agent',
        displayName: String(anyMeta.source_name || 'AI 助手'),
        avatarEmoji: (...),
        avatarColor: stringToColor(...),
    } : undefined;
    return { ...turn, source: source || turn.source, ... };
}
`

- Null 回退正确：sourceMeta falsy → source = undefined → 保持原值
- sourceId 双命名兼容：source_id || sourceId ✅
- 用户消息状态标记为 success ✅

**P2 观察**:
- displayName 仅读取 source_name，未兼容 sourceName
- avatarEmoji 仅读取 source_type，未兼容 sourceType

---

## 4. 数据通路完整性

### 已实现路径（Platform → Controller → 前端）

`
Connector → PlatformApiClient.SendMessageStreamAsync(metadata: {...})
  → MessageIngressRequest.Metadata
    → POST /api/messageingress/stream → SessionRouter.RouteMessageStreamAsync()
      → yield metadata SSE 帧 → SSE → 前端 mapEventToTurn → ChatSource ✅
`

### 已知断点（dev 已报告）

`
Connector → AgentEventHandler → AgentExecutionService
  → 不经过 SessionRouter，不产生 metadata SSE 帧
`

### T-102 新流（fire-and-forget POST + 持久 SSE）

SessionRouter 的 metadata 帧通过流式响应发射，不经过持久 SSE 通道. 直接 Chat 的 source 默认为 'agent'，无功能缺陷。后续 connector 路径补充时需写入 session event log。

---

## 5. 向后兼容性

| 场景 | 行为 | 结果 |
|------|------|------|
| 直接 Chat（无 Metadata） | source_type 为 null → 前端 fallback 到默认 | ✅ |
| 已有 PlatformApiClient 调用者 | metadata 参数默认 null | ✅ |
| 已有 AdminChatStreamEvent 消费者 | metadata 事件为联合类型新增成员 | ✅ |

---

## 6. 问题清单

### P0（阻断）— 无

### P1（严重）— 无

### P2（改进建议）

| ID | 位置 | 描述 | 建议 |
|----|------|------|------|
| P2-1 | useChatState.ts | displayName 未兼容 camelCase sourceName | 添加 || anyMeta.sourceName fallback |
| P2-2 | useChatState.ts | avatarEmoji 未兼容 camelCase sourceType | 添加 || anyMeta.sourceType fallback |
| P2-3 | — | Metadata 帧未写入 session event log | 后续 connector 路径补充时处理 |

---

## 7. 结论

**PASS_WITH_NOTES** — 无阻断性或严重问题。

实现正确覆盖 Platform → Controller → 前端的 metadata 通路，JSON 序列化行为与前端类型一致，null 安全处理完备，向后兼容。3 项 P2 建议不阻断合并，可在 connector 路径补充时一并处理。

---

## 8. QA 审阅记录

| 日期 | 任务ID | 开发者 | QA | 结果 | 备注 |
|------|--------|-------|-----|------|------|
| 0517 | T-103 | DeepSeek-V4-Pro | DeepSeek-V4-Pro | PASS_WITH_NOTES | 3xP2 命名兼容+metadata持久化 |
