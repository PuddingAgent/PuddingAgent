# 原生聊天审批：真实执行链路与接入门禁

状态：Proposed；2026-09-27 源码核查。此文件是实现方案，不是审批闭环已经交付的证明。

## 1. 已确认的现状

| 层 | 当前证据 | 结论 |
|---|---|---|
| Web 卡片 | `Source/PuddingPlatformAdmin/src/pages/chat/components/ApprovalCard.tsx` | 显示请求、参数、风险、过期时间，提供 allow_once / always_allow / deny |
| Web 决定接口 | `Source/PuddingPlatform/Controllers/Api/ApprovalController.cs` | 查找 approval.requested，校验状态后追加 approval.resolved；不是 Runtime 执行授权服务 |
| 会话事件 | `Source/PuddingPlatform/Services/ConversationEventStore.cs` | 有审批事件常量与 payload 校验，不等于存在请求生产者与结果消费者 |
| 既有回归 | `Source/PuddingWebApiTests/SessionApprovalDecideTests.cs` | 明确记录请求生产者缺失、always_allow 无消费者，验证未知审批返回 404 |
| Runtime 自动审批 | `Source/PuddingRuntime/Tools/Approval/InMemoryToolApprovalService.cs` | NeedHuman 与 DeferredDependency 区分；NeedHuman 引导用户使用 /authorize，不能等同于 Web 审批卡等待 |
| 票据端口 | `Source/PuddingCore/Tools/ToolApproval.cs` | IToolApprovalService 只有 SubmitAsync / CheckAsync；IToolApprovalTicketStore 只有 Save/Get/List，没有人工决定的原子转换端口 |
| 存储 | `Source/PuddingRuntime/Tools/Approval/FileToolApprovalStores.cs` | 有文件票据持久化；SaveAsync 的存在不证明人工竞争决定的 CAS 或消费恰好一次成立 |

本次全仓 C# 引用核查只找到会话审批常量、校验、决定控制器及测试，没有找到 Runtime 写 approval.requested 或消费 approval.resolved 的生产链路。不得直接把控制器调用改成函数调用后宣称审批可恢复工具执行；也不得以“创建一条审批结果事件”的测试替代执行闭环验收。

另有 PuddingController 的 IApprovalService/确认码审批以及 Runtime 的 IToolAuthorizationService。它们具有不同身份和授权粒度，不能按名称相近合并。现有 /authorize 是工具授权语义；原生按钮标为“允许本次操作”时必须绑定精确操作，不能偷偷转换成更宽的工具、会话或永久授权。

## 2. 聊天体验

待审批项必须出现在固定的“需要你的决定”区域，不能被长 Turn 最近 40 块窗口或历史分页隐藏；消息内保留对应活动卡及处理结果。切换角色时不串单，角色列表显示待处理数量。

卡片展示 Core 提供的工具、完整参数（可折叠/复制）、目标资源、工作目录、请求理由、影响说明、作用域、到期时间和当前状态。风险等级只能来自权威数据，没有等级时显示“未提供评估”，不能由 Native 猜测。参数作为文本显示，不执行其中内容。

Core 返回当前允许的决定集合，UI 只展示该集合。首个闭环先支持精确单次允许和拒绝；永久规则必须另行证明规则写入、作用域、冲突裁决和运行时消费，门禁通过前不展示“始终允许”。DeferredDependency 显示“等待审批依赖恢复”，不提供人工批准来绕过分类器依赖；安全策略终局拒绝也不伪装成待人工确认。

点击后禁用同一卡片重复提交，保留拒绝理由草稿。以 Core 返回的权威状态刷新，不乐观显示“已批准”。失败可重试；已处理、过期、操作变化、会话冻结分别显示原因。审批成功和工具执行成功是两个不同状态，必须分别呈现。

## 3. 唯一内核真源与调用边界

调用方向：Native ApprovalCard → PuddingChat 审批端口 → Composition → Core 人工决定应用服务 → Runtime 授权/执行状态机。Native 不引用 Runtime、数据库或 Controller，不写审批事件、不修改票据文件、不自行重发工具。

拟定任务形状端口（名称待组件实现时确定）：

- `ReadPendingAsync(role, session, cancellationToken)`：返回强类型请求、版本、绑定执行身份、到期时间和 AllowedDecisions。
- `DecideAsync(role, session, approvalId, expectedVersion, decisionId, decision, reason, cancellationToken)`：返回权威状态与冲突原因；decisionId 保证网络以外的本地重试也可幂等。

身份绑定至少包含工作空间、角色、会话、Run、Turn、tool invocation，以及规范化参数/工具定义/执行目录快照的指纹。提交决定时 Core 再次校验这些身份及请求版本。参数、目录、工具定义或执行主体变化后旧决定不得复用。

Core 决定服务拥有 Pending→Approved/Denied/Expired 的原子转换，禁止 Native 先 Get 后 Save。已批准结果在执行前重新检查绑定并以一次性消费语义授予精确 invocation。并发允许/拒绝只能有一个胜者；重试同一 decisionId 返回原结果，不再次授权。

事件仅作已提交状态的呈现与唤醒。请求状态、决定、执行续行意图必须具备可恢复的一致性边界；跨文件写入不能仅靠调用顺序保证。实现前应复用仓库既有事务/outbox 能力，明确恢复扫描，避免新增无依据的第二套执行调度器。仅在状态提交后通知 ICommittedEventSignal，Native 继续使用进程内订阅。

现有 Runtime NeedHuman 返回值不证明 Run 已处于可恢复的挂起点。因此必须先确定“保持 invocation 等待”或“通过现有执行调度恢复同一 invocation”的具体路径；禁止从 UI 合成一条用户消息或盲目重跑整个 Turn 来制造续行。

## 4. 组件交付顺序与文件范围

| 步骤 | 归属/拟修改范围 | 验证门禁 |
|---|---|---|
| A1 | Core/Runtime 人工决定合同与状态转换组件；核对 ADR-091 及当前分类器规则 | 先独立验证身份绑定、CAS、幂等、过期和一次性消费；不得先改 Host |
| A2 | Runtime NeedHuman 产生请求和执行续行适配；复用当前执行调度 | 在可控工具测试中证明暂停点、原 invocation 恢复及只执行一次；依赖不可用不得走人工批准 |
| A3 | Platform 请求/结果投影及提交通知；决定接口改为调用同一服务 | 先造真实请求再决定，验证请求/结果/执行一致；旧 404 测试保留未知 ID 用例，补正向闭环 |
| A4 | PuddingChat 审批合同/选择代次/提交状态；独立 WinUI ApprovalCard | 无宿主测试全部交互、过期、重试、双击、角色切换、长记录下待处理可见 |
| A5 | Composition 直接装配与 ChatWorkspace 区域 | 真实 Core + Native 零聊天 HTTP 集成，之后才能发布 Shell |

## 5. 必须提供的验收证据

1. 测试工具真实触发 NeedHuman，UI 展示相同参数和身份；未点击前工具副作用计数为零。
2. 允许一次后同一 invocation 只执行一次；重复决定、双击、并发允许/拒绝不增加执行次数。
3. 拒绝、到期、角色/会话不匹配、参数/目录变化都不能执行；UI 展示对应权威结果。
4. 请求创建后、决定提交后、通知发送前及执行消费后的故障点分别恢复；无丢失续行、重复副作用或幽灵待审批卡。
5. DeferredDependency 和策略终局拒绝不能通过原生决定入口被升级为允许；缺少能力时按钮不出现。
6. 切换角色、滚动到旧消息、超过 40 个活动块和消息控件回收均不丢失待审批入口；晚到结果不覆盖新角色。
7. Native 路径没有 HttpClient/REST、JWT、WebView；Web 与 Native 通过同一 Core 应用服务执行决定。

未通过以上门禁前，原生审批状态应明确记为“未接入”，不能在完成清单中仅凭控件或事件存在而打勾。本缺口不阻止继续实现其他聊天组件。
