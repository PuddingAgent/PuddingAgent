# 原生子代理检查器

目标：主聊天只保留委派摘要，完整子代理内部过程进入独立原生检查器。对照 Web `SubAgentActivityDock.tsx`、`subAgentReducer.ts` 与 Core `SubAgentRunController`，不迁移 HTTP 路由，不将子代理正文混入父角色消息。

## 身份与读取边界

检查身份为 `SubAgentInspectionKey(RoleKey, ParentSessionId, RunId)`。RunId 必须来自 canonical 委派执行身份；池化子会话 ID 不能替代单次运行 ID。Core 读取前验证角色属于工作空间、父会话属于该角色，归档 Manifest 的 WorkspaceId/ParentSessionId/RunId 必须一致。UI 的回执身份检查只是拒绝晚到/错误结果，不能代替 Core 的归属校验。

当前归档入口是 `ISubAgentRunStore.GetRunArchiveAsync`，Web Controller 自行映射事件和摘要，不能从原生调用 Controller。Composition 应直接调用 Core 归档服务，返回 PuddingChat 的强类型展示快照；归档 JsonElement 的解析保留在适配/业务层，WinUI 不解析传输 JSON 或访问文件/数据库。

## 已完成：独立控件门禁

`PuddingChat/SubAgentInspection.cs` 定义只读端口、精确身份及展示快照。`SubAgentInspector` 展示状态、真实开始/结束时间、可用的轮次/工具次数、委派任务、活动流与完整结果；未知统计值不合成，归档缺失/降级与运行错误显式显示。活动复用 TurnContentView 的渐进展开与思考/工具渲染，完整结果不采用父消息中的 300 字摘要限制。

读取按次去重，失败可刷新；刷新失败保留上次快照并明确标示旧数据。异身份回执拒绝；Dispose 取消等待，迟到结果不再更新 UI。没有控制或重试子代理的按钮，也没有假装实时的定时轮询。

独立验证：BCL 50/50、原生窗口 115 项，零构建警告/错误（temp/native-subagent-inspector-final.log）。新增 7 项覆盖读取去重、三区/1000 字完整结果、归档降级、失败保留旧快照、跨 Run、跨角色、Dispose 后晚到响应。尚未加载生产 Core 归档。

## 后续接入门禁

1. Composition 实现 ISubAgentInspectionClient：复用进程内操作生命周期，读取前校验角色/父会话归属，读取后核对 Manifest 身份；缺失归档返回明确不可用。禁止把当前登录免鉴权理解为免资源归属检查。
2. 归档事件映射必须对照实际生产事件与 Web reducer，保留真实思考、工具调用/结果、执行顺序和归档降级信息；未知类型保留可读通用活动，不伪造输出。检查大归档读取开销，必要时在 Core 增加有界读取，不能只把全量数据读进 UI 后称为分页。
3. 委派卡以 DelegationExecutionId 打开独立检查区域/窗口，携带所选角色与当前父会话；没有精确执行 ID 时不提供猜测入口。关闭取消读操作；切换角色不得把旧回执显示到新角色。
4. 用真实临时归档验证映射、身份隔离、空/损坏归档、缺失事件提示和零 HTTP，再接产品入口并验证生命周期。Web 详情、事件与输出接口的存在不等于原生检查器已经接入。

## 进程内接入（2026-09-27 后续）

`InProcessChatClient.SubAgents.cs` 已实现只读端口：通过已有 ExecuteAsync 生命周期直接调用角色文件服务、SessionRepository 与 ISubAgentRunStore。先校验角色/父会话，再核对归档 Manifest 的工作空间/父会话/Run；非法路径形状的 RunId 在读取前拒绝。没有 Controller 调用或聊天 HTTP。

按真实生产归档字段投影 `subagent.llm.completed` 的思考/正文预览、`subagent.tool.started/completed/failed` 的调用、结果和错误；截断字段显示“归档仅保存截断预览”。归档数组顺序映射为稳定显示序列；未知事件保留类型和 payload 文本，不冒充已完成操作。完整结果读取 archive.Output，归档降级单独显示。当前归档服务读取整份文件，本适配不宣称 I/O 分页或大归档性能已经达标。

TurnFlow 保留 DelegationExecutionId，展开的委派卡只有在精确 RunId 和客户端能力都存在时显示详情按钮。MessageCard、失败请求的执行明细和独立活动 Run 均接入回调；ChatWorkspace 打开独立 ContentDialog 并携带捕获的角色/父会话身份。切换角色取消并关闭旧检查器，关闭对话框释放读取；完整子代理内容仍不并入父消息。

验证：原生窗口 118 项、BCL 50/50；真实 Core 组合 3/3（在既有零 HTTP 探针范围新增真实归档创建/完成/读取、工具失败信息、截断/未知事件、跨角色/会话/归档和非法 RunId 断言）。日志 temp/native-subagent-entry-final.log 与 temp/native-subagent-core-final.log。Core 构建保留既存分析器警告；未部署到运行中 Desktop，未调用真实模型。超大归档、主动刷新运行中归档的长期性能和真实委派交互仍待产品验收。

当前状态：组件、直接调用适配与聊天入口已接入并通过隔离验证；真实产品部署验收未完成。
