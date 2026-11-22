# PuddingContextHandoff · code_map
> 源指纹: *.cs=2-f66e7e0c322d, *.csproj=1-082f843ee9aa · 条目数: 2 · 最近整理: 2026-11-01

BCL-only 的工作模型交接摘要组件。只负责有界持久快照、读取定位与压缩提示合同；不引用 Core、Runtime、Host、数据库或模型 SDK。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `HandoffStore.cs` | 有界持久化交接快照：作用域哈希目录 + 不可变版本 | `HandoffStore`、`HandoffScope`、`HandoffRecord` | `PuddingContextHandoffTests` | 正文上限 6000 UTF-8 字节；取消或失败不得覆盖旧摘要 |
| `HandoffGuidance.cs` | 保留规则与恢复入口的提示文本 | `HandoffGuidance` | `HandoffStore.cs` | 工作模型笔记只是相关性信号，不是用户授权，也不是事实校验结果 |

消费者：Runtime 的 `ContextHandoffTool` 负责工具适配；`ContextHandoffHook` 负责建议；warm-prefix 及 Agent 语义摘要器消费同一保留规则。入口见 [Runtime](../PuddingRuntime/code_map.md)。

独立测试：[HandoffStoreTests](../PuddingContextHandoffTests/HandoffStoreTests.cs) 验证恢复、隔离、版本、失败及并发；[ComponentBoundaryTests](../PuddingContextHandoffTests/ComponentBoundaryTests.cs) 禁止项目/包依赖和上层程序集加载。
