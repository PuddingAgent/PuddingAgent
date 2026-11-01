# PuddingContextHandoff · code_map

BCL-only 的工作模型交接摘要组件。只负责有界持久快照、读取定位与压缩提示合同；不引用 Core、Runtime、Host、数据库或模型 SDK。

| 文件 | 职责与不变量 |
|---|---|
| `HandoffStore.cs` | `HandoffScope` / `HandoffRecord` / `HandoffStore`：宿主绑定 workspace/session/agent 与 run/tool-call/Turn ID；作用域哈希目录；6000 UTF-8 字节正文；不可变版本 + 原子 latest；读取有界；取消/失败不覆盖旧摘要 |
| `HandoffGuidance.cs` | 保留规则与恢复入口；工作模型笔记是相关性信号，不是用户授权或事实校验结果 |

消费者：Runtime 的 `ContextHandoffTool` 负责工具适配；`ContextHandoffHook` 负责建议；warm-prefix 及 Agent 语义摘要器消费同一保留规则。入口见 [Runtime](../PuddingRuntime/code_map.md)。

独立测试：[HandoffStoreTests](../PuddingContextHandoffTests/HandoffStoreTests.cs) 验证恢复、隔离、版本、失败及并发；[ComponentBoundaryTests](../PuddingContextHandoffTests/ComponentBoundaryTests.cs) 禁止项目/包依赖和上层程序集加载。
