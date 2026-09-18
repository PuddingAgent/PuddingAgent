# G92-1 [S1] 实施规划
日期：2026-09-18。角色：规划子代理；本文不是实施、测试通过或部署声明。

## 依据与引用约定
下文短名均展开为本节完整路径；短名:行号是源码定位，短名 §章节是文档依据。建议中的测试/接口为拟实施项，不代表已存在。
- ADR = `Docs/07架构/106ADR-092目标驱动执行与分层验证闭环ADR.md`，第二版 Proposed。
- 设计 = `Docs/Features/Goal目标驱动执行与分层验证闭环设计-2026-09-15.md`。
- C规格 = `Docs/Reports/G92-1-S1c-设计与施工规格-2026-09-18.md`。
- A规格 = `Docs/Reports/G92-1-S1a-合同覆盖门设计与施工规格-2026-09-18.md`。
- 调用面 = `Docs/Reports/G92-1-S1删除项调用面取证-2026-09-18.md`。
- 矩阵 = `Docs/Reports/G92-1-验收证据与缺口矩阵-2026-09-17.md`。
- 任务书 = `temp/s1-plan-brief-2026-09-18.md`；F1–F10 直接采信，不重新查询生产。
- Core 下文均指 `Source/PuddingCore/Goals/`；Svc 均指 `Source/PuddingPlatform/Services/Goals/`；Tests 均指 `Source/PuddingPlatformTests/Services/Goals/`。

## 【事实】当前基线与证据边界
1. S1-a 已提交 be04d37，覆盖门定向验证 269/269、22/22、前端 5/5，未部署；只拦精确 source=bounded_planning，未知 source 不拦。来源：任务书 §3 F1；矩阵 §11.2–§11.5。
2. S1-b/MS-1 已提交 43898c1，删除完成门 scope/remaining；247/247、22/22 是已有定向证据，不是本轮重跑。Advance、计划闸与步骤投影仍保留。来源：任务书 §3 F2；矩阵 §15。
3. UI 一行目标无文本断言生成路径；已有纯工程门禁合同无法整理，9 行历史合同均为 bounded_planning。S1-a 不能单独上线。来源：任务书 §3 F3–F5/F9；矩阵 §12.1、§14.2–§14.3。
4. 输入身份组件已交付、15/15，未接线；Task-bound 的 general 计划编译失败已登记，不在本规划冒领修复。来源：任务书 §3 F6/F7。
5. UI 管新建/恢复/暂停/停止，Agent 不提供 Goal 工具；不得批量提交并发 WIP。来源：任务书 §3 F8/F10；矩阵 §14.1、§15.1。
6. 最新纠正优先：Scope 属于 GoalCheckContext，而不是 GoalCheckSpec。真实定义在 Core/GoalCheckContracts.cs:6–37、:105–120；矩阵 §15.4 已纠正任务书和调用面中的旧称谓。检查身份轴保留不等于保留完成门 scope。
7. DefinitionHash 当前仅覆盖 DefinitionRef/Kind/CommandTemplate，无每条文本期望值；ExpectedEvidence 是人读说明。来源：Svc/GoalCheckDefinitionRegistry.cs:49–58；Core/GoalCheckContracts.cs:32–36。
8. Worker 仅空合同时规划，新合同加载后只回填 Criteria/Checks；未回填 source。来源：Svc/GoalSettlementWorker.cs:85–107。此为新增静态风险，不是已复现线上误完成。
9. 合同 Store 同一 goal/epoch/objective 行原地替换，版本递增；内容不变比较不含 Source。审计写 ContractVersion=1。来源：Svc/GoalAcceptanceContractStore.cs:9–13、:60–95；Svc/GoalSettlementStore.cs:516。
10. 既有一次性 Replan 依赖无进展计数及 Task WorkUnit，不是一次性合同整理账本。来源：Svc/GoalSettlementStore.cs:1115–1165；矩阵 §13.4。
11. Runner 在分派具体 kind 前要求 WorkingDirectory；Planner 总是追加配置工程门禁。来源：Svc/GoalCheckRunner.cs:139–162；Svc/GoalAcceptanceContractPlanner.cs:87–91。只加文本 kind 不足以实现零工具、零项目短路径。
12. 证据策略对非 passed 报告提前返回；记录层依赖更新 dedup 才清旧报告。来源：Core/GoalCheckEvidencePolicy.cs:53–54；Svc/GoalCheckRecordStore.cs:99–112。旧失败失效必须有接线回归，不可只测旧成功。

## 【建议】P1：S1-c 三个裁决
### P1-a：判据来自哪里
| 选项 | 行为后果 | 反例/失败模式 | 裁决 |
|---|---|---|---|
| A：断言 canonical 回合最终输出 | 可不写文件、不调用工具，一轮交付；需候选/胶囊/检查上下文接线 | 读取 prompt、历史回合、流式片段或模型自填 passed，会把指令本身当证据 | 推荐作为“只输出指定文本”专用路径；依据：设计 §5/§11，C规格 §2/§3.1 |
| B：断言命名工件内容 | 适合用户明确要求文件的目标，可做存在与内容检查 | 为纯文本目标强制落盘引入工具；预存非空文件不证明本次要求已满足 | 不作为纯文本默认；仅用于文档目标；依据：设计 §5/§11，Svc/GoalCheckDefinitionRegistry.cs:24–25 |
| A 的 contains 变体 | 实现便宜，但仅证明包含某子串 | 期望“OK”，实际“NOT OK”或“OK，另外做了修改”也可能通过 | 拒绝作为“只输出”的默认；改精确匹配。依据：设计 §11 的“只输出”，C规格 §3.1 的 contains 是候选非权威 |

推荐细则（依据：设计 §5–§6/§11；C规格 §2/§3.1）：
- 期望值来自用户原始要求或经服务端验证的合同，不来自被检查输出；模型可提议映射，不能自行改变用户字面量。
- 对明确“只输出 X”使用 ordinal 精确文本比较；空白、换行、大小写是否允许变换必须写进定义，默认不 Trim、不 contains、不宽松 Unicode 归一。
- 对不明确的自然语言不猜 expectedText；进入一次有界整理/必要时澄清，不把普通业务目标当成文本复述目标。
- 输入是该 Goal/epoch/iteration 绑定的已结束 canonical Turn 最终 assistant 输出，附 TurnId/terminal sequence/原文摘要哈希与原始引用；不得选择任意“包含 X”的聊天消息。具体转录读取 API【未证实】，实施第一片须定位并确认无需扩大 Runtime 改动面。
- 同时验证本 Goal 本轮工具调用为 0，且 canonical 执行事实完整；若存在子调用必须计入或保守拒绝。不能仅相信模型说“未调用工具”。现有 candidate.ToolCalls 有记账落点（Svc/GoalSettlementStore.cs:491–501），它是否覆盖全部子调用【未证实】。
- 文本检查走纯程序分支，位于 WorkingDirectory 门禁之前；Planner 文本分支不追加 bounded build/test、不发现项目。来源：事实 11；设计 §11。
- 用户可见输出不能为了合同整理附带 JSON 元信息；明确文本目标走确定性派生、不额外发一轮规划回合。复杂目标的结构化提议走平台结果适配，不混入精确文本验收内容。依据：设计 §2/§5/§9/§11。

### P1-b：kind、定义版本与证据身份
| 选项 | 行为后果 | 反例/失败模式 | 推荐 |
|---|---|---|---|
| 新增受控 text_assertion kind | 可以专门校验 canonical 输出与零工具条件，无进程要求 | 只新增词表未接 Runner/Policy/持久报告，仍是 unsupported 或报告丢失 | 推荐，复用 Criterion/CheckSpec 容器但增加有类型断言参数；依据：Core/GoalCheckContracts.cs:6–37，C规格 §3.2 |
| 复用 Semantic | 少加枚举，但现有分支没有文本比较逻辑 | 有非空证据和 passed 就可能被接纳，不能证明 exact match | 不推荐；并非所有语义检查都无效，而是现有策略不足以承担此确定性义务；依据：Core/GoalCheckEvidencePolicy.cs:200–207 |
| 复用 FileEvidence | 可复用文件存在流程 | 输出不是文件；存在且非空不等于内容达标 | 不推荐；文件目标另做内容检查，依据：Svc/GoalCheckDefinitionRegistry.cs:24–25、:40–44 |
| 复用其他 kind + 新 DefinitionRef | 理论上可以实现同样严格分派 | 通用 kind 的进程证据要求/执行器假设可能冲突，审核面反而增大 | 无充分收益，不选；依据：Core/GoalCheckEvidencePolicy.cs:109–207；具体兼容性【未证实】 |

推荐版本合同（依据：设计 §5–§6；事实 7/9/12）：
- DefinitionHash 必须覆盖定义版本、kind、比较模式、期望文本完整字节表示、归一策略、零工具约束；使用无歧义结构化序列化，不拼接未经转义的用户字符串。
- expectedText 变化即定义变化；同时提升受影响 CriterionRevision/ContractVersion，不靠 source 改名获得“新证据”。仅提升版本也不能省略哈希载荷，否则独立复用/漏升版本会错误接受旧期望结果。
- InputFingerprint 负责实际输入身份：文本为绑定的不可变最终输出及执行事实身份；文件/代码为声明依赖的原始字节与环境/BuildId。不能用期望值替代实际输入哈希，也不能用 epoch 替代内容身份。
- 新 kind 必须接 Registry、Runner、EvidencePolicy、CheckRecordStore 及 Worker 持久报告读取；测试禁止靠 stub 自报 passed 放行。依据：Svc/GoalSettlementWorker.cs:110–141，Svc/GoalCheckRunner.cs:60–126。
- 保留 GoalCheckContext.Scope 和当前 dedup 编码；但现编码缺 criterionId/contractRevision，与设计 §6 有差距。增加同定义双条件冲突测试，并在身份收敛片显式决定去重协议升级，不能暗改 K2。依据：Svc/GoalVerificationPersistence.cs:29–50；Svc/GoalCheckRecordStore.cs:42–74；矩阵 §15.4。

### P1-c：一次有界合同整理通道
| 通道 | 后果 | 反例/失败模式 | F8 与推荐 |
|---|---|---|---|
| A0：仅放宽 Worker 闸，重跑现 Planner | 不新增工具/接口，代码小 | 同一个无标记 objective 仍生成工程门禁，Planner 非空拒写；没有任何新信息 | 不冲突 F8，但不足以闭环；不单独采用。依据：Svc/GoalSettlementWorker.cs:85–107；Svc/GoalAcceptanceContractPlanner.cs:46–66 |
| A1：现有 Agent 回合提出结构化合同提议，平台验证提交 | 保留 objective；自然语言义务映射到受控条件；可覆盖普通 UI 目标 | 无持久一次性闸会重启/换 epoch 重复整理；无版本 CAS 会迟到覆盖合同 | 推荐为一般目标通道；不新增 Agent Goal 工具，符合设计 §5/§9 和任务书 F8 |
| B：Agent 可调用创建/修订/恢复 Goal 工具 | 看似可自助修补 | 引入用户明确排除的 Agent 控制面；Agent 可绕义务控制 | 排除，直接冲突 F8；来源：矩阵 §14.1 |
| C：UI 引导输入证据/编辑合同 | 用户授权清晰，适合歧义与缩减义务 | 要求所有用户改写证据行使普通目标不再是一句话，文件存在声明仍不证明覆盖 | 作为必要澄清/授权修订后备，不作为普通目标唯一通道；设计 §5/§9/§11 |

推荐 A1 + 确定性文本短路径 + C 后备；不是单纯“追加一个文本条件就完整”。依据：设计 §5（原始要求映射、禁止非空即覆盖）。
- 合同状态显式区分缺失/待整理/覆盖已验证/需用户确认；具体存储表示【未证实】，第一实施片先做协议与持久化评审。source 只记录来源，不能独自证明覆盖。
- 一次性整理按 goalId + 经授权的 objectiveVersion 计数，跨 epoch/重启保留；预留与 outbox 同一短事务，重放用同一操作 ID，对不确定执行先对账，失败也不得悄悄再开规划循环。依据：设计 §4/§6/§8。
- 提议以 expected contract revision/goal aggregate version/epoch 提交，服务端验证原始要求覆盖、受控 kind、参数与权限，拒绝删义务、自报 passed 和陈旧提议；合同修订、失效受影响证据及后续动作原子提交。依据：设计 §4–§6。
- 不用 ContractVersion>1 代表整理已花费：输入指纹变化也可能使 SaveAsync 升版；整理未成功又不会升版。不得复用将被 MS-2 删除的 WorkUnit Replan。依据：Svc/GoalAcceptanceContractStore.cs:81–95；Svc/GoalSettlementStore.cs:1115–1165。
- 成功：完整合同进入检查/执行；失败/歧义：一次性用户确认或持久不可推进状态，无相同 LLM 轮询。UI 恢复不得自动重置已耗整理预算；实质目标修订由用户授权。依据：设计 §3/§5/§8；F8。
- 覆盖门收紧为“覆盖未经验证不得 Complete”，未知 source 也需实际覆盖证明；失败/pending 优先级保持，A 表负例不改预期。不得把所有 source!=bounded_planning 自动当完整。依据：A规格 §2.1；矩阵 §11.3；设计 §5。

## 【建议】P2：有序实施切片与收口
下表是后续实施任务书，不授权本次改这些文件；超出本次只读范围的触点以文档引用交接，不在本轮读取/修改。
| 顺序 | 原子目标 | 触点文件 | 依赖 | 独立验收证据 | 风险/边界 |
|---|---|---|---|---|---|
| 0 | 固定不可删负例与契约审阅 | Tests/GoalContractCoverageGateTests.cs、GoalSettlementWorkerCheckIntegrationTests.cs；合同协议规格 | F1/F2 基线 | 现有 A 用例不改；增加 null/未知 source、初建合同整包刷新负例；记录失败基线 | 先签字文本来源、整理账本、版本历史保存方式；依据：A规格 §4，设计 §10 |
| 1 | S1-c 文本规格/派生 | Core/GoalCheckContracts.cs、GoalVerificationContracts.cs；Svc/GoalObjectiveEvidenceParser.cs、GoalAcceptanceContractPlanner.cs、GoalCheckDefinitionRegistry.cs | P1-a/b | 明确文本不产生工程门禁；contains 反例、换期望哈希失效、非文本目标不误识别 | 不靠任意自然语言正则猜合同；依据：P1-a/b、设计 §5/§11 |
| 2 | S1-c canonical 文本检查端到端 | Svc/GoalSettlementStore.cs、GoalSettlementWorker.cs、GoalCheckRunner.cs；Core/GoalCheckEvidencePolicy.cs；相关 Tests | 1；真实转录读取接口核定 | Store/SQLite 回归：一轮、零工具、无工作目录/Task/Plan、持久 passed 后 Complete；错回合/多余输出/工具调用/截断拒绝 | 未确认真实输出读取前不宣称“只改七文件”；依据：C规格 §2；Svc/GoalCheckRunner.cs:139–162 |
| 3 | S1-c 有界整理事务与覆盖门闭环 | Svc/GoalAcceptanceContractPlanner.cs、GoalAcceptanceContractStore.cs、GoalSettlementStore.cs、GoalSettlementWorker.cs、GoalContinuationWorker.cs；Core 合同载体 | 0；P1-c 存储协议定稿 | 非文本 UI 目标一次整理后可推进；失败一次确认、重启/重复事件/换 epoch 不增次数；拒绝删除义务；覆盖缺失不得 Complete | 旧版本审计、source 刷新、ContractVersion=1 修正同片验证；设计 §4–§6，事实 8–10 |
| 4 | F6 接线的生产安全最小闭环 | Svc/GoalCheckInputIdentity.cs、Planner、Runner、CheckRecordStore、SettlementWorker/Store；Core/EvidencePolicy | 1–3 定义身份稳定 | 相关变化使旧成功/失败失效；无关变化凭范围复用；执行中/执行后提交前改动均不完成；BuildId 变化 | 只在 Planner 算一次不足；见下文。依据：设计 §6、任务书 F6 |
| 5 | MS-2 删除 Advance 与 Goal 计划闸/计划写入 | Core/GoalVerificationContracts.cs；Svc/GoalSettlementStore.cs（ApplyBoundPlanGates/Verdict、TryReplanBoundPlan、记账）；相关 Tests | S3 Task 适配契约与必需条件映射有可测试替代 | 调用归零；独立无 Plan 可完成；未完成必要 Task 条件不完成；Task 计划只由 Task 域操作；CAS 不半终态 | 不先删常量留下 producer；源码可先做隔离定向验证，生产 Task-bound 等 S3；调用面 B/C、矩阵 §13.4、设计 §7/§10 |
| 6 | MS-3 切断 Goal 决策/续行的步骤投影依赖 | Svc/GoalRunStore.cs、GoalContinuationWorker.cs、GoalQueryService.cs；Core/IGoalCommandService.cs；对外 Controller/UI 由 S4 协作方处理 | 5 + S4 消费者清单与切换协议 | Goal 续行不读取 WorkUnit/步数；投影展示条件/证据/下一动作，UI 不将迭代当百分比；旧 steps 入口去留有契约验收 | 仍需要做；用户界面/API 删除归 S4，不在 S1 宣称已删；调用面 B、设计 §9/§10 |
| 7 | S1 统一纯判定收口 | Core/GoalVerificationContracts.cs、GoalStateMachine.cs；Svc/ConservativeGoalIterationVerifier.cs、GoalSettlementStore.cs；定向 Tests | 1–6 的 S1 责任部分 | 唯一纯决策不含 I/O；Store 仅事务/fence/CAS校验，不独立升级 Complete；误完成负例全保留 | 命令/检查/恢复事件全接线归 S2；S1 未完成分支不能借移交标完成。依据：设计 §2/§10 |

F6 接线时机与位置（建议，依据：设计 §6；Svc/GoalCheckInputIdentity.cs:57–110；Svc/GoalCheckRecordStore.cs:42–112）：
- 在非文本目标可发布前完成片 4；文本不可变证据身份在片 2 完成，不强迫文本检查遍历项目目录。
- Planner 冻结“依赖声明与检查定义”，不是永久冻结当时工作树哈希；每次选证据/入队前按声明重新采集并使旧结果失效。
- Runner 执行前采集、结束后复核；不匹配结果不得 passed。最终 Complete 前由已有执行资源门禁或不可变快照保护输入，事务只核对冻结的身份令牌，不在 SQLite 写事务内扫描文件。
- RecordStore/Persistence 对齐合同 revision、criterionId、definitionHash、inputIdentity；保留 scope 轴但评审现有去重缺项，协议迁移与跨 epoch 重试预算归 S2 联动，不靠删除 scope 修复。
- 接线必须覆盖成功与失败、缺失后出现的文件、声明路径不合法、环境变化、并发修改；不确定依赖扩大范围或保持未知。现 Collect 依赖项目目录且文档称越界声明会忽略（Svc/GoalCheckInputIdentity.cs:63–85），适配层应把非法声明视为合同错误，不能静默缺项。

S1 完成判据（依据：设计 §10；矩阵 §11.6/§15.6）：
1. S1-c 两条通路有真实 Store/SQLite 证据；未知覆盖不得 Complete；文本正例与工程绿业务缺失负例同时成立。
2. MS-1 已有证据继承，MS-2 的 Goal 决策/计划写入删除和 MS-3 的 Goal 引擎依赖删除完成；检查身份 scope 不误删。
3. 完成判据只由纯规则产生，合同/证据/版本/授权校验明确；身份接线对 S1 完成语义所需部分闭环。
4. 若 MS-2 等 S3 尚无替代，S1 保持未完成，不能用“Task-bound 当前坏了”豁免；S4 对外投影删除另列未完成。
5. 源码与测试交付、外部部署、新构建验收分别记账；S1 源码完成不等于 ADR-092 产品完成或四类生产 smoke 已通过。

## 【建议】P3：发布批次、验收与回退
| 批次 | 内容/顺序 | 上线后可执行与可观察验收 | 放行条件 |
|---|---|---|---|
| B0 隔离验证，不上线 | 已交付 S1-a/MS-1 加片 0–4 | 固定源码版本运行定向 Core/Platform 与 SQLite 回归；保留命令、exit code、通过/失败数与日志；禁止用历史 269/247 代替新验证 | S1-c 全闭环，停止“只放宽闸”方案；设计 §10/§11、F9 |
| B1 首个安全组合 | S1-a + S1-b/MS-1 + S1-c + 必要输入身份接线同批生效；不含需 S3 才安全的 Task 删除 | UI 新建“只输出 READY”：单 Goal 一轮、tool_calls=0、build/test 检查数=0、最终文本精确、Complete；再运行明确文件/代码目标，审阅条件映射与输入身份 | 非文本条件类型无实际执行器则不得宣称通用 Goal 放行；依赖持久暂停/有界整理安全缺口未补时推迟整批，不能先上覆盖门 |
| B1 负例/恢复 | 与上行同一构建 | 配置工程检查全绿但缺业务条件：不得 Complete；未知 source 同样；预置旧纯门禁活跃目标经一次整理可前进或一次明确确认，重启后不重复整理；不存在无限同分支轮询 | 误完成=0；覆盖门 source 刷新正确；无需 Agent 工具；依据：设计 §5/§11、矩阵 §14 |
| B2 引擎收敛组合 | MS-2 + MS-3 的 S1 核心部分，与 S2 等待/恢复、S3 Task 唯一写入口依赖协调部署 | 同 Goal 真实失败→修复→通过；pending/审批无变化窗口模型调用增量=0；事件重放、暂停取消、lease过期不双结算；Task CAS竞争无半终态 | Task-bound 先解决 F7，S3接线及文本/文件/代码/Task四类 smoke 齐备；不得用 general 不可达作为安全开关，设计 §10 |
| B3 S4 收尾 | 条件投影/API/UI 对齐、旧 goal_queue 生产注册/提示删除 | UI 生命周期按钮实际可用；无计划不提示缺步骤；检查生产只有一个循环与状态入口，Agent goal.md故障不影响 Goal | 不保留常驻双实现；依据：设计 §1/§9/§10 |

所有批次由外部控制器选择无存活执行者窗口，备份并记录 Schema版本、目标commit、BuildId和实际加载组件摘要；每个 smoke 关联 goalId/iteration/turn/check/contract revision 与 usage。缺值标未知，不填0。依据：设计 §10/§11；矩阵 §2 G7、§4。
无需本次执行上述命令或修改生产；本规划只规定验收动作。故障注入在隔离环境，不能重启正在产证据的 Core。依据：矩阵 §4。

回退裁决：
- “无 DB 迁移、单 commit revert”仅对已限定的 MS-1 运行时字段删除有既有依据（矩阵 §12.4/§13.3），不能推广为整个 S1。
- S1-c 新检查参数即使存于现有 JSON、不新增列，也改变持久协议；旧二进制可能不识别新 kind。一次性整理计数、历史合同版本、CAS载体能否完全复用现有结构【未证实】，必须在片 0 做存储评审。设计 §4–§6/§10，Svc/GoalAcceptanceContractStore.cs:81–95。
- source 长度 32 对 35 的附带修复依据 C规格 §2/§6；已有 bootstrap TEXT 证据见调用面 A3，但实体/所有提供方未在本次范围读取，不能据此承诺跨层无需迁移。
- 未启用新生产路径/未产生新数据时，可对隔离原子提交做代码 revert 验证；组合发布必须保持 S1-a/S1-c 配对，不能单撤文本/整理路径留下覆盖门。禁回到工程门禁误完成语义。依据：F9、设计 §10/§11。
- 升级失败且无新增数据，由外部控制器按备份恢复；已产生新数据则停止执行、保存新增账本及副作用事实，优先前向修复或显式转换。不得让旧二进制写新协议、盲目还原备份。依据：设计 §10“回退”。

## 【事实】P4：对齐检查表的当前状态
本表“已满足”只指明确列出的源码/既有测试层；“无法验证”代表本轮无对应执行证据，不暗示通过。验收动作列是建议。
ADR 正文本身无 §10/§11 编号；任务所称 §10/§11 实际指其链接的完整设计，按该设计逐项列出。
| 设计条目 | 当前状态 | 依据 | 建议验证方式 |
|---|---|---|---|
| §10 S1：合同覆盖验证/删工程门禁兜底 | 未满足（指定 source 负例已满足源码层） | 矩阵 §11.2–§11.5，任务书 F1/F3 | 保留 A；加 null/未来source/初建同轮/多义务漏项，SQLite与新构建重演 |
| §10 S1：统一判定 | 未满足 | 调用面 A2/C，Svc/GoalSettlementStore.cs:463、:833 | 查全部 Complete producer，Store不得二次改写业务判定；纯规则测试 |
| §10 S1：删除 advance/remaining/完成门scope | 未满足；MS-1 子项已满足 | 矩阵 §15.2/§15.6 | MS-2/MS-3 引用归零，保留检查身份scope；Task域替代回归 |
| §10 S2：薄Worker、无LLM等待、检查完成重评、租约/事件幂等 | 未满足 | Svc/GoalSettlementWorker.cs:110–143；设计 §10 | 隔离 pending/审批/租约超时，观测无新模型调用与一次有效重评 |
| §10 S3：绑定唯一入口/新认领/无租约可结算/原子终态/Store不遍历计划 | 未满足 | 任务书 F7；调用面 A2/B；设计 §7/§10 | Task适配可达后竞争Tracker、CAS与无认领等待结算；全事务回滚 |
| §10 S4：同一投影/删旧循环生产注册与提示/保留goal.md | 无法验证整体；步骤投影仍待改 | 矩阵 §15.6；调用面 B；设计 §10 | 协作方核对注册与提示注入，UI按钮/投影回归 |
| §10 横向：先负例、小提交、不扩建双循环 | 已满足负例基线；后续过程无法验证 | 矩阵 §11.2/§15.3；设计 §10 | 每片保留基线和逐文件白名单，检查无平行Coordinator |
| §10 横向：S1/S2定向验证，Task切换等S3和四类smoke | 未满足整体验收 | 矩阵 §15.6；任务书 F7 | B0–B2分别记录，不把Task不可达当成功 |
| §10 切换：版本化升级/旧enum显式映射/旧终态保留/未知活跃暂停 | 无法验证 | 设计 §10；本次无部署 | 升级旧库副本，逐类Active/Blocked/BudgetExhausted/终态对照，不重解释数字 |
| §10 回退：保全新数据/禁止旧二进制写新Schema | 无法验证 | 设计 §10 | 隔离恢复演练，分别验证有/无新数据场景 |
| §11 goal.md四种故障不影响启动/续行/重启/裁决 | 无法验证 | 设计 §1/§11 | 同目标分别缺失/损坏/超限/拒绝访问，比较生命周期且无前置goal工具 |
| §11 指定文本一轮、零工具、零build/test、无Task/Plan | 未满足 | 任务书 F4；C规格 §2 | B1正例及错文本/多输出/工具副作用反例 |
| §11 不可删G92负例 | 未满足全链；指定source裁决单测已满足 | 矩阵 §11.2–§11.5；A规格 §2.1 A | 真实Worker→Store→投影→UI重演；业务缺项/未知source/无Task绑定不得Complete或冒称Task完成 |
| §11 DONE/回复结束/TODO全勾选不是证明 | 无法验证全链 | 矩阵 §15.3有负例族但非全场景；设计 §11 | 注入自述已完成、条件仍unknown/failed，断言不Complete |
| §11 同Goal失败→修复→通过，仅检查受影响条件 | 未满足 | 任务书 F6，矩阵 §11.6 | 输入改动前后比较逻辑检查与真实invocation，无双层重复验证 |
| §11 pending/审批未变无轮询，匹配事件一次决策 | 未满足 | 矩阵 §8.4 G10；设计 §11 | 固定观察窗统计关联模型调用增量，重放匹配/不匹配事件 |
| §11 相关/无关/执行中变化使旧成功失败正确失效 | 未满足 | 任务书 F6；Core/GoalCheckEvidencePolicy.cs:53–54 | F6片4矩阵，补执行结束至提交间的竞态 |
| §11 重放/超时/崩溃/暂停取消旧结果安全 | 无法验证完整组合 | 矩阵 §2 G4；设计 §11 | 隔离故障点注入，验fence、单结算、无复活、副作用先对账 |
| §11 Task完成与Tracker/CAS竞争，恢复新ActiveTask | 未满足 | 任务书 F7；设计 §7/§11 | S3集成与Task smoke，不通过Agent自建Goal绕行 |
| §11 新构建BuildId/usage对账，缺值不是0 | 无法验证本批 | 矩阵 §2 G7、§8.3；设计 §11 | 构建-加载hash关联，Goal/Turn/Check费用与usage逐项对账 |
| §11 源码/SQLite/四类新构建smoke分别记录；首批指标 | 未满足整体 | 矩阵 §11.4/§15.6；设计 §11末段 | 首批误完成=0、无变化等待额外LLM=0，记录耗时/input/miss/费用；24h/7d不阻塞小目标 |

不可删负例的准确裁决：S1-a 只满足“bounded_planning+全passed不得Complete”的局部源码验收；不能据此声明整个 §11 已满足。未知source、初建同轮来源刷新、完整业务覆盖、Task归属及新构建UI链路仍需验证。依据：事实 1/8；矩阵 §11.3/§11.5；设计 §5/§11。

## 【建议】P5：风险、失败模式与控制
“新”表示本次读码/设计新增分析；静态发现不是已复现生产事故。
| 风险 | 失败模式及级别 | 控制/验收 | 依据 |
|---|---|---|---|
| F1覆盖门fail-open | P0：未知source绕过覆盖门；新来源上线后从理论风险变实际风险 | 完整性显式验证，unknown保持不完成；测试null/未来值，不靠改helper掩盖 | 矩阵 §11.3/§12.2，设计 §5 |
| F3永久阻塞 | P0：UI目标没有新条件，每轮命中同分支 | 两条通道同批，整理成功可推进/失败一次确认；不循环 | 任务书 F3/F4/F9，矩阵 §14 |
| F7 Task-bound不可达 | P0发布依赖：测试无法覆盖大多数general卡；修复后暴露MS-1语义变化 | 记录外部依赖，独立Goal先定向验；Task放行等S3，不冒领修复 | 任务书 F7；矩阵 §13.4/§15.5 |
| F10并发WIP | P1：覆盖/误提交他人测试或带走无关提交 | 后续实施先核对文件属主、逐文件提交；本轮仅指定temp文件 | 任务书 F10，矩阵 §15.1 |
| 新：Worker来源未刷新 | P0候选：新纯门禁合同Criteria非空但source仍null，和fail-open组合可误判 | 同轮完整重载合同source/revision/criteria/checks，SQLite回归；不只改verifier | Svc/GoalSettlementWorker.cs:97–107；矩阵 §11.3 |
| 新：合同历史/审计版本不可靠 | P0：原地覆盖旧定义，审计仍记1，修订后无法证明用哪个合同完成 | 保存版本快照/不可变引用；candidate携真实版本，事务CAS；历史迁移评审 | Svc/GoalAcceptanceContractStore.cs:81–95；Svc/GoalSettlementStore.cs:516；设计 §4/§5 |
| 新：仅修改Source不落库 | P1：SaveAsync unchanged判断不含source，整理成功标识可能不更新 | 明确source是审计元数据还是版本字段；测试同内容来源更新，不用source升级代替完整性 | Svc/GoalAcceptanceContractStore.cs:81–85 |
| 新：一次性预算伪实现 | P0：靠ContractVersion或重规划计数，崩溃/epoch可无限整理或误耗额度 | 持久操作身份、原子预留、重放对账、跨epoch次数不重置 | Svc/GoalSettlementStore.cs:1115–1165；设计 §4/§8 |
| 新：contains/输出串号 | P0：NOT OK、历史正确输出、用户prompt被当成功 | exact与canonical绑定，未结束/截断/旁白/额外工具均拒绝 | C规格 §3.1；设计 §5/§11 |
| 新：旧失败粘住 | P1：仅通过报告验身份，旧failed持续阻塞修复后输入 | 先按有效合同/身份筛全部报告，再决定failed/passed；实测旧失败变化重跑 | Core/GoalCheckEvidencePolicy.cs:53–54；Svc/GoalCheckRecordStore.cs:99–112 |
| 新：检查身份碰撞 | P1：不同criterion同定义/输入被批内跳过，缺一个报告；跨epoch重建刷新attempt | 补双条件/跨epoch用例；显式身份协议方案，不静默更改Scope；S2协同预算持久化 | Svc/GoalVerificationPersistence.cs:29–50；Svc/GoalCheckRecordStore.cs:42–89；设计 §6 |
| 新：输入采集覆盖不足/TOCTOU | P0：只在规划时算hash、非法声明忽略、提交前又被写，旧报告证明新输入 | 依赖清单准入、执行前后与提交冻结三处核验；无法证明则unknown | Svc/GoalCheckInputIdentity.cs:63–85；设计 §6 |
| 新：file-exists冒充内容覆盖 | P0：任何非空文档加工程绿就通过全部设计维度 | 文档存在与所需维度分开条件，受控内容检查；受限语义检查按预算且无工具 | Svc/GoalCheckDefinitionRegistry.cs:24–25；设计 §2/§5 |
| 已知附带：source长度与注释 | P2：35字符值与MaxLength32冲突，SQLite暂不暴露 | 独立小提交审查实体/bootstrap/序列化约束；本次未跨scope读实体 | C规格 §2/§6 |
| 新：错误回退/历史成功洗白 | P0：旧二进制读新kind、备份丢新增记录，旧Completed伪装新验收成功 | 组合回退、保全数据、历史证据原样展示；不单撤S1-c | 设计 §10；任务书 F9 |

## 自查与 NOTES
- 已按 P1–P5组织；三个裁决均给选项/后果/反例/推荐；P4逐条覆盖设计§10/§11，局部源码通过不冒称运行验收。
- 技术现状有源码行号/文档章节；未证实接口、存储可复用性及运行行为已标记；建议与事实分区。
- 本次未执行构建/测试/部署/DB查询；未修改受跟踪文件，未调用被禁工具，未做git写操作；仅写指定temp产物。
- NOTES：两次Core路径未命中（DefinitionRegistry、InputIdentity），随后在允许的Svc目录定位；产出路径读取返回不存在，确认后新建。均继续执行，未中断。
- NOTES：长文工具输出被截断，必读设计和六份依据的缺段已分页补读；源码仅作定向抽查，不声称全仓审计。
- NOTES：采用矩阵§15.4纠正Scope归属；否定contains默认、ContractVersion充当一次性次数及整个S1“无迁移单提交回退”承诺。
- NOTES：未读取scope外实体/Runtime/UI/Host；相关触点仅引用指定报告，未来实施须由获授权协作者验证。

---

## 父级核验（默认助手 6a8 独立复核）

- 生成来源：subAgentId `206a9b48ec904ebb93e7541131fbb835-sub-9d51cfce`，模型 **fastrouter/gpt-6-astra**（`fallback_model` 设为与主模型相同 => 回退已禁用，产出确为 GPT-6），status=completed；任务书 `temp/s1-plan-brief-2026-09-18.md`。
- 核验方式：父级只读读码抽查，不采信子代理自述。

| 规划断言 | 父级独立核验 | 结论 |
|---|---|---|
| 事实 7：DefinitionHash 仅覆盖 DefinitionRef/Kind/CommandTemplate，不含 expectedText | 亲自读 `Source/PuddingPlatform/Services/Goals/GoalCheckDefinitionRegistry.cs` 的 `ComputeDefinitionHash`：`payload = "{DefinitionRef}|{Kind}|{CommandTemplate}"` | ✅ 属实。直接决定 S1-c 的硬约束：新增文本断言 kind 时，**期望文本必须进入 DefinitionHash 载荷**，否则旧绿灯不失效 |
| 交付边界 | 产出仅 `temp/` 与本文档；未修改任何受跟踪文件 | ✅ |

- **未核验项（如实声明）**：P5 中标注“新”的多数静态风险（Worker 来源未刷新、合同审计版本不可靠、一次性预算伪实现、输入采集 TOCTOU、file-exists 冒充覆盖等）**父级仅核验了 DefinitionHash 一条**，其余尚未逐条读码验证，**不得作为已验证结论使用**，实施前需逐条落实证据。
- 父级对三点裁决的初步意见（最终裁决权在用户）：P1-a 同意（精确匹配 canonical 回合最终输出，拒绝 contains）；P1-b 同意（新增受控 text_assertion kind + 期望值进 DefinitionHash + 同步提升 CriterionRevision/ContractVersion）；P1-c 同意 A1 方向（不新增 Agent 侧 Goal 工具，符合用户 2026-09-18 决定），但落地通道的具体形态待用户裁决。

