# ADR-064：Codex 独立执行服务与 Pudding 自修复重启

> 状态：Accepted  
> 日期：2026-07-26

## Context

Pudding 作为 MCP Client 直接启动 `codex mcp-server` 时，Codex 是 Pudding 的 stdio 子进程。
正常关闭 Pudding 会释放 MCP Client Session，并由官方 SDK 终止 Codex 子进程。这对普通 MCP
Server 是正确的生命周期，但无法支持“Pudding 请求 Codex 修补自身，随后由 Codex 编译并重启
Pudding”：宿主退出会提前终止正在修补宿主的执行者。

简单地让 stdio 子进程脱离父进程也不成立。Pudding 与 Codex 之间的请求和结果仍绑定原连接，
Pudding 重启后无法可靠找回正在执行的调用；Windows 也不保证孤儿子进程具备可治理生命周期。

## Decision

新增独立进程 `PuddingCodexService`，由 Pudding 外部监督器与 Backend 并列托管：

```text
dev-up / OS service manager
├── PuddingAgent
└── PuddingCodexService
    └── codex mcp-server
```

1. Service 对 Pudding 暴露 loopback Streamable HTTP MCP。
2. Service 独占并管理内部 `codex mcp-server` stdio 子进程。
3. Codex 调用采用持久异步任务：提交立即返回 `taskId`，状态与结果落盘；Pudding 通过新连接查询。
4. Pudding 与 Service 使用 `taskId`，Service 与 Codex 使用 `threadId`，两种身份不得混用。
5. 当前开发机 Service 固定以 `danger-full-access` + `never`（Yolo）执行 Codex Task，且不接受
   调用方逐任务覆盖；Service 仍只允许仓库根目录内的 Codex `cwd`。生产部署必须重新收紧权限。
6. 自重启使用 `pudding_self_heal_start` 与外部监督器；Codex 只修补/验证，禁止直接停止或启动
   Pudding。Codex 成功后 Service 自动提交 staging build，成功后才停止并替换 Backend。
7. Backend-only restart 不重启 Codex Service、Frontend 或 Proxy；Service 完整重启才允许中断 Codex。
8. 普通 MCP stdio Server 仍由 Pudding 直接托管，不因 Codex 特例改变通用生命周期。
9. 自修复的 `taskId → restartRequestId` 由 Service 持久化和幂等恢复，不依赖 Agent 在单次 Turn 内
   轮询、记忆或拼接进程控制提示词。

## Rationale

- 把“被修补的宿主”和“执行修补的 Agent”放在不同故障域。
- HTTP MCP 允许 Pudding 释放连接而不拥有 Service 进程；持久 `taskId` 允许重连恢复结果。
- 保留 Pudding 的通用 MCP Client、Workspace 隔离、审批和工具审计，无需 Codex 特化调用路径。
- staging build 在旧 Backend 在线时完成，构建失败不会把当前可用实例停掉。
- 把“完成 Codex 工作后安排重启”收口为 Service 状态机，避免 Agent 查询一次 Running 后结束，
  或重启后继续复述过期状态。
- Service 仍使用稳定的 `codex mcp-server`，V1 不依赖实验性的 app-server WebSocket 协议。
- 开发机 Yolo 模式绕过 Windows sandbox helper，允许 Codex 执行进程控制与本地构建；固定权限由
  Service 配置所有，不信任渠道消息或 Agent 工具参数中的权限声明。

## Alternatives Rejected

### Pudding 退出时不 Dispose MCP Client

进程强制退出、stdin EOF 和 Windows 子进程树行为都无法形成可靠合同；重启后的 Pudding 也没有原
请求的 durable handle。

### Codex 直接执行 `python dev-up.py --restart`

当 Codex 位于 Backend 子树时，`taskkill /T` 会把执行重启命令的 Codex 一并终止；第二个启动器
也会与已有监督循环竞争 PID、端口和日志所有权。

### Pudding 内部后台队列

队列消费者仍属于被重启进程，不能跨宿主退出继续运行。

### V1 直接迁移 Codex app-server

app-server 更适合深度事件投影，但当前 WebSocket transport 仍为实验能力；本阶段只需要稳定 MCP
工具调用、持久任务和外部重启边界。

## Consequences

- 本地开发从三个受管进程增加为四个；`dev-up.py --status/--logs/--down` 必须包含 Codex Service。
- Codex Task 拥有开发机完整本地执行权限；该决定只适用于可重置的开发设备，不能作为生产默认值。
- Codex Task 文件会增长，后续需要 TTL/归档清理策略。
- Service 自身崩溃时当前 V1 会重新排队遗留任务，可能创建新的 Codex thread；对修改型任务不得
  静默自动重试未知提交状态，V2 应增加阶段 journal/幂等策略。
- 生产部署需要用 Windows Service、systemd 或容器编排实现与 `dev-up` 等价的兄弟进程和 Backend-only
  restart 控制面。

## Verification

1. fake Codex：第一个 MCP Client 提交后断开，第二个 Client 按 `taskId` 得到 Completed。
2. real Codex：Service 返回指定 marker，且任务在 Client 断开期间保持 Running。
3. 外部重启：Backend PID 改变、Codex Service PID 不变、重启后 `/health` 为 200。
4. Pudding 重连：Workspace Codex Skill 为 Available，发现六个任务/重启工具。
5. staging 构建失败：当前 Backend PID 不变且结果为 `build_failed`。
