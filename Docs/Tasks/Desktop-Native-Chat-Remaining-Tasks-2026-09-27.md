# Native WinUI 3 聊天剩余任务书

- 日期：2026-09-27；交接基线：`17c813e`，另有下文明确列出的未提交改动。
- 状态：开发已按用户要求暂停。本任务书用于恢复后的执行与验收，不表示重新启动开发或完整目标已完成。
- 目标：交付角色优先的原生聊天体验，参考原 Web 消息卡，流式呈现模型实际提供的思考内容、正文、工具调用与结果；Desktop 与 Core 同进程直接调用。
- 执行负责人：待分配。按任务逐项认领，不以窗口检查数量增加代替用户能力交付。

## 一、范围与约束

1. 角色是一级导航实体；草稿、附件、阅读位置、运行状态和审批均按角色/会话隔离。
2. `PuddingChat.WinUI → PuddingChat` 保持编译期边界；Composition 调用 Core 应用服务。业务执行与持久状态属于 Core，不在 View 复制运行状态机。
3. 聊天不走 HTTP/JWT/WebView，不要求客户端登录。第三方模型、远程图片的网络请求不属于 Desktop/Core 通信。默认可修改的 `D:\data` 配置沿用现有实现，本任务不重做。
4. 只展示服务商实际返回并允许呈现的思考内容；缺失时不得编造思考文本。工具调用与结果按 canonical invocation/Turn/Run 配对。
5. 先组件验证，再宿主接入。保留并行工作和运行数据，构建/测试串行，临时产物只放规定目录；原子任务验证后精确暂存并提交。
6. 此任务书覆盖聊天组件与相关接入。完整管理中心、右侧代码/Diff/终端/Agent 浏览器迁移属于 Desktop 总迁移计划，不在这里顺带扩大实施范围；聊天发出的宿主导航请求应有明确可用或不可用反馈。

依据：[迁移计划](../Features/Desktop-WinUI3-Migration-Plan-2026-09-26.md)、[完成度核对](../Reports/Desktop-Native-Chat-Completion-Audit-2026-09-27.md)、[审批设计](../Features/Desktop-Native-Approval-Integration-Design-2026-09-27.md)、[语音设计](../Features/Desktop-Native-Voice-2026-09-27.md)。历史章节中的计数与“待实现”描述是当时记录，以当前源码和最近验证为准。

## 二、已完成基线：不要重复建设

| 能力 | 已有实现/最近证据 | 仍不能据此声称 |
|---|---|---|
| 角色导航、原生消息/输入组件、草稿和附件隔离、历史与阅读恢复 | `PuddingChat`、`PuddingChat.WinUI`；85 项逻辑 / 260 项窗口检查 | 跨重启草稿/阅读持久化、完整系统交互验收 |
| 流式正文/思考/工具/委派，原生 Markdown/代码/图片/公式 | TurnFlow、TurnContentView、MessageCard 等；原生组件验证 | 全量 KaTeX 等价、任意大数据下性能达标 |
| 图片/文本文件、子代理检查、录音转写、消息朗读 | 独立合同、原生控件、Core 直接适配已接入 | PDF/Office 提取、真实设备/模型已验收 |
| 停止交互、长工具输出阅读 | `e4dc696`、`eac5014`：回执隔离、防重复停止、360 DIP 阅读区与翻页 | 执行已停止、外部副作用已回滚 |
| Core 进程内装配 | 3/3 Core 集成；Desktop PID 30456 挂载/重启/退出和目录锁释放通过 | 干净提交可发布或真实用户数据已部署 |
| 人工审批基础 | 独立 ApprovalCard、状态/CAS/操作快照/SQLite outbox/收件箱/取消；22 逻辑 / 10 存储测试 | 请求生产、持久暂停、决定消费及聊天审批闭环 |

最近日志：`temp/native-tool-output-viewport.log`、`temp/native-chat-core-current.log`、`temp/native-chat-product-current.log`。完整产品构建零错误、147 警告，来自共享脏工作树。产品报告：`temp/test-out/kernel-winui-644c164769734ba7a052741a7b3a4ce7/report.json`。临时证据可能被清理，恢复时先核实是否仍在，缺失则按命令重新生成。

## 三、任务顺序与依赖

| ID | 优先级 | 任务 | 状态 | 前置 | 负责边界 |
|---|---|---|---|---|---|
| NC-00 | P0 | 收口暂停现场的审批等待/熔断修复 | 已改、7 项定向测试通过、未提交 | 无 | Runtime |
| NC-01 | P0 | 真实审批请求与持久暂停/原 invocation 恢复 | 未实现 | NC-00、既有 A1 | Runtime + Platform 执行调度 |
| NC-02 | P0 | Core 审批应用服务、事务通知和生命周期整合 | 未实现 | NC-01 的持久契约 | Core/Platform + Composition |
| NC-03 | P0 | 原生固定待审批区与角色提示闭环 | 卡片已有，工作台未接 | NC-01、NC-02 | Chat/WinUI |
| NC-04 | P1 | 真实模型端到端聊天与取消验收 | 未验收 | 当前聊天；审批部分依赖 NC-03 | 集成/外部验收 |
| NC-05 | P1 | 整窗视觉、系统输入和无障碍验收 | 部分组件验证已有 | 当前聊天 | WinUI/产品 |
| NC-06 | P1 | 长会话与流式性能测量、修复 | 局部优化已有，整体未测 | 当前聊天 | Chat/Projection/WinUI |
| NC-07 | P1 | 真实语音设备与供应商验收 | 接入已有，硬件未验 | 当前语音接入 | WinUI/Composition |
| NC-08 | P1 | 可复现产品构建与最终交付核对 | 共享工作树 smoke 通过 | NC-00～07 必选门禁 | 发布/外部验收 |

可独立安排 NC-05/06/07；本表不授权新建代理、线程或调用真实麦克风。执行时按当前用户授权与选定测试资源推进。

## 四、逐项实施与验收

### NC-00：收口暂停现场

仅以下四个未提交文件属于本聊天任务的暂停现场：

- `Source/PuddingRuntime/Services/AgentExecution/FailedToolCallTracker.cs`
- `Source/PuddingRuntime/Tools/Platform/ToolInvocationService.cs`
- `Source/PuddingRuntimeTests/Services/FailedToolCallTrackerTests.cs`
- `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.HumanDecision.cs`

改动意图：HumanDecisionRequired 不计工具门面的错误熔断；HumanDecisionRequired/DependencyWait 不计重复失败，也不清除先前真实失败。真实拒绝仍计错误，既有预算和硬拒绝保持生效。

暂停前启动的测试最终通过 7 项，日志 `temp/native-approval-admission-fuse.log`；构建有既有代码/依赖告警，不能称为零警告。恢复后检查文件是否被他方继续修改，复核此语义与 ADR-091 一致、既有失败拦截仍有效，更新相应设计/代码地图后仅提交上述改动。不得顺带提交 Host 索引、设置页、dev-up 或 memory 工作。

完成标准：定向测试通过且代码复核/独立提交完成。它不是“Run 已暂停”的证据，不能关闭 NC-01。

### NC-01：持久暂停与精确恢复

入口：`PuddingToolRegistry.cs` 的 NeedHuman、防火墙结果；`AgentExecutionService.Streaming.cs` 的工具批次循环；`ExecutionRunCoordinator.cs`、`ChatExecutionWorker.cs` 的租约与 worker 释放。同步/非流式工具路径也必须核查，不能只修 Native 可见的一条路径。

实施要求：

1. 先定义可持久化恢复点和版本：绑定 workspace/agent/session/run/turn/invocation，包含原工具/参数/定义/执行目录快照、当前工具批次、已完成结果及必要的会话历史/预算/曝光状态。逐字段说明恢复来源，禁止只存一个 approvalId 然后重跑整轮。
2. 请求与暂停事实需有可恢复的一致性边界。沿用既有执行调度，不另造第二个 worker；落盘后释放执行槽位。当前内存 ResumeAnchor 不具备上述恢复能力，LeaseLost/重排整个命令不是审批暂停方案。
3. 恢复消费原 invocation，执行前重验策略、身份、目录及操作快照；已完成工具不重复。已消费但结果未知进入 DispatchUnknown 并对账，不能自动派发第二次。
4. 拒绝、到期、停止、角色冻结、Core 重启有明确权威转换；等待不计执行错误但仍受明确的等待期限/资源上限约束，不无限占用 worker。

完成标准：可控工具真正触发 NeedHuman；批准前副作用 0，单次批准后 1；双击/并发/恢复不增加；同批次先前工具不重跑；等待释放槽位；跨重启恢复通过；DeferredDependency、硬拒绝不产生人工放行入口。请求落盘、决定落盘、通知前、消费后故障点均提供测试和持久记录证据。

### NC-02：Core 决定服务与事件接入

以 `PuddingApproval`/`PuddingApproval.Sqlite` 为已验证组件，新增 Core 应用端口，Composition 直接调用；读取和决定均校验本机身份、角色与会话归属。Web 决定入口也应调用同一服务，不能继续仅追加 approval.resolved 后暗示执行已恢复。

事务 outbox 消费者按 approvalId/version 持久去重；先提交再通知原生订阅，丢通知可恢复扫描。将 `CancelAsync` 的未消费许可撤销接入真实停止路径，明确“已消费不可撤销”和运行中工具取消的区别。注册 DI 前先验证组件；处理宿主重启时订阅/任务释放。

完成标准：真实请求可读、决定 CAS/幂等/冲突/过期正确；未知 ID 拒绝；通知重放不重复执行；Core 停止取消等待且重启可恢复；Native 调用没有新增聊天 HTTP。保留 Web 未授权/未知 ID 负向测试，增加真实请求正向闭环。

### NC-03：工作台审批区

复用独立 `ApprovalCard` 和 `PuddingChat/Approvals.cs`，接入 ChatWorkspace 固定“需要你的决定”区域；角色列表展示权威待处理数量，消息活动保留关联状态。参数只读、可展开/复制；风险缺失如实显示，不猜测风险。

只提供 Core 允许的单次允许/拒绝。稳定 decisionId 重试原决定；切换角色、卡片回收和异步晚到不得串单。区分“已批准”“执行中”“工具成功”，不要以 UI 乐观状态驱动执行。

完成标准：长 Turn 超过 40 块、历史翻页、窄窗、角色切换仍可发现待审批项；审批结果同步到固定区/活动卡/角色提示；失败可重试；完整真实 Core 端到端通过。永久授权不作为首个闭环的隐含实现。

### NC-04：真实模型聊天验证

先记录用户明确选择的测试 DataRoot、角色和供应商/模型，确认数据目录单宿主占用；不得复制 D:\data 中密钥绕过测试资源选择。使用明确的新构建 PID。

覆盖：首次角色聊天、连续多轮、真实流式思考/正文、工具输入输出与错误、长任务中停止、角色切换后恢复、子代理详情、图文/文本附件，以及 NC-03 完成后的批准/拒绝。将 session/turn/run/invocation 与 canonical 事件、截图和最终结果逐一对应；无思考输出的模型允许如实空缺。

完成标准：真实输入到 UI、Core 执行与持久记录一致；无串角色、漏事件、重工具、假成功。记录费用/延迟和已知模型限制，不以 fixture 替代真实供应商证据。

### NC-05：UI/UX 与系统交互矩阵

- Windows 11 浅/深色、高对比度，Mica/Acrylic 可用及系统关闭透明效果时的回退；检查内容可读性而不是只验证设置了材质类型。
- 320/360/520/900 DIP 聊天宽度；100/125/150/200% DPI、文字缩放；三栏收放、角色 Flyout、长名称、长路径、错误提示和固定审批区。
- 中文 IME 候选确认、Enter 换行、Ctrl+Enter、键盘长按、焦点顺序；真实剪贴板、选择器、Explorer 拖放；文本选择和复制。
- Narrator：角色名/状态/未读、消息、工具展开、按钮与审批状态；动态流式更新不能每 token 打断播报。
- 大工具输出的嵌套滚轮/触摸板、页首恢复、返回最新消息与历史锚点。

完成标准：按场景保存新构建截图/步骤/结果，修复截断、不可达、焦点陷阱、低对比与阅读跳动。现有窗口断言只证明组件行为，不能代替真实 IME/Narrator/触摸板验收。

### NC-06：性能与资源

在记录硬件、构建、数据集和采样方法后测试：1000 条历史、多于 40 个活动块的单 Turn、800 段以上回复、至少百万字符工具输出、持续流式追加和反复切换/展开/收起。测量 UI 卡顿、事件到显示延迟、内存/控件数、历史/明细读取耗时和退出释放。

先记录基线并在报告中冻结可接受阈值，再定向优化。重点核查完整 Markdown 重解析、单 Turn 全展开、历史明细全量读取、已加载正文常驻内存；优先有界读取/呈现与稳定锚点，不用截断源内容假装解决。

完成标准：同数据集前后可比较；事件顺序/内容无丢失，回收后资源趋稳，滚动/输入可用。已有 801 块构造耗时不含布局/帧率，不能直接当产品性能指标。

### NC-07：语音真实验收

显式用户操作开启麦克风；验证权限拒绝、设备缺失/拔出、两分钟/8 MiB 上限、录音结束先释放设备再转写、切角色/离开/退出释放。真实 ASR 显式加入草稿不自动发送；真实 TTS 读当前 canonical 正文，停止与角色切换正常，来源元数据可回读。

完成标准：至少一套实际设备/选定供应商通过正常和失败场景；记录音频格式、供应商/模型及限制，不记录密钥或非测试录音。持续全双工、自动唤醒/自动发送不在当前已确认语音迁移范围。

### NC-08：发布与最终收口

1. 以精确提交构建，不能夹带共享工作树的未提交合同重命名或其他功能。复用已附加的隔离验证工作树时先检查其两份旧合同补丁/子模块，不覆盖或恢复未知修改。
2. 串行执行组件、Core 集成、完整 Desktop 生命周期检查；归档提交号、包/程序集哈希、PID、测试资源、日志与截图。147 个构建警告及 NU1903 等依赖告警需分类登记并按发布要求处理，不把测试通过等同于告警解决。
3. 新构建启动、Core 停止/重启、正常退出与异常退出恢复要由外部控制器观察；不得用旧单实例进程或产品内 Agent 自证部署生效。
4. 逐条更新完成度核对和迁移计划，将历史状态与当前结果区分；记录尚未接受的限制，用户确认取舍前不得自行删除必选门禁。

完成标准：NC-00～07 必选项完成、产品包可复现、实际效果与证据一致，任务书全部有状态/提交/证据记录后再判断整体目标完成。

## 五、恢复时的命令与交付格式

先 `git status --short`，阅读最新 Agents.md、code_map.md 和组件交付规范；核对暂停现场，以下命令串行执行，按改动范围选择，不无理由重复全套：

```powershell
# NC-00 定向复核
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --artifacts-path temp/build/native-approval-runtime --nologo -p:CollectCoverage=false --filter "FullyQualifiedName~FailedToolCallTrackerTests|FullyQualifiedName~HumanDecision_ExecutorPreservesTypedDenialWithoutExecuting"
# 独立审批组件
dotnet test Source/PuddingApprovalTests/PuddingApprovalTests.csproj --artifacts-path temp/build/native-approval --nologo -p:CollectCoverage=false
dotnet test Source/PuddingApproval.SqliteTests/PuddingApproval.SqliteTests.csproj --artifacts-path temp/build/native-approval-sqlite --nologo -p:CollectCoverage=false
# 原生组件、直接 Core 集成、完整产品
pwsh -NoProfile -File TestScripts/test-pudding-native-chat.ps1 -SkipCoreIntegration
dotnet test Tests/PuddingNativeChat.IntegrationTests/PuddingNativeChat.IntegrationTests.csproj -c Release --artifacts-path temp/build/native-chat-integration --nologo -p:CollectCoverage=false
pwsh -NoProfile -File TestScripts/test-pudding-desktop-kernel.ps1
git diff --check
```

每项交付记录：任务 ID、负责人、状态、变更文件、提交号、验证命令/数据集、通过与失败证据、已知限制、下一依赖。检查数量只是辅助信息，必须说明覆盖的行为。

## 六、待产品确认的扩展项（不伪装成既有能力）

PDF/Office 正文提取、跨重启草稿/阅读书签、持续语音、完整公式兼容、永久授权规则编辑等分别确认价值和范围后另列任务；当前没有足够依据将其全部纳入首个聊天交付。若用户决定纳入，补充验收标准和依赖后更新本书，不暗中扩展或缩减范围。

原开发任务保持暂停。本轮仅登记剩余工作，不启动 NC-00 或其他实施任务。
