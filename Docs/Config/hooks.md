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
