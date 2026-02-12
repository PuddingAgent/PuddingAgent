# Hooks 配置（v1）

最后更新：2026-02-20

`config.json` 中可配置：

```json
{
  "hooks": {
    "enabled": ["metrics", "audit_file", "external"],
    "auditLogPath": ".pudding/hooks.log",
    "external": [
      {
        "name": "webhook-relay",
        "command": "node",
        "arguments": "scripts/hook-relay.js",
        "enabled": true,
        "timeoutMs": 8000
      }
    ]
  }
}
```

说明：

- `metrics`: 内存计数 Hook（用于状态面板）。
- `audit_file`: 写入审计日志到 `auditLogPath`。
- `external`: 启动外部进程，向其 `stdin` 发送 JSON 事件。

JSON 事件示例（发送给 external hook）：

```json
{
  "name": "webhook-relay",
  "event": "pre_tool_call",
  "timestamp": "2026-02-20T10:00:00.0000000+00:00",
  "payload": {
    "id": "call_1",
    "name": "file_write",
    "argumentsJson": "{...}"
  }
}
```

CLI 命令：

- `/hook status`
- `/hook enable <metrics|audit_file|external>`
- `/hook disable <metrics|audit_file|external>`

## Hook System v2 direction

Hook v2 separates internal framework hooks from external user-configured hooks.

- Internal hooks are mandatory framework lifecycle events and use `IHookPublisher` plus the existing internal event pipeline.
- External hooks remain read-only, async, timeout-bound, and isolated from mandatory framework hooks.
- The first v2 internal hook is `session.compressed`, used by Memory System v2 R4.
- Current implementation publishes `session.compressed` from `ContextCompactionService` and bridges it to durable `SubconsciousJobs`. `SubconsciousWorkerService` leases durable jobs first. The legacy `ConsolidationJob` channel remains only as an explicit compatibility path.
- Legacy subconscious compatibility is disabled by default to avoid duplicate learning beside durable jobs. Use `Subconscious:EnableLegacyConsolidationHook=true` to register the old agent-loop hook, or `Subconscious:EnableLegacyAgentExecutionFallback=true` only when the old `AgentExecutionService` fallback enqueue path is intentionally required.

The existing `metrics`, `audit_file`, and `external` settings remain v1-compatible. A later migration can map those external targets onto Hook v2 as read-only subscribers.
