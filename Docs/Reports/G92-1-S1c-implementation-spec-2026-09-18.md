# S1c 实施规格（G92-1 slice0a 持久化续作）
> 日期：2026-09-18。依据：temp/s1c-spec-brief-v2-2026-09-18.md + temp/s1c-facts.md + Docs/Reports/G92-1-S1c-slice0a-persistence-review-2026-09-18.md。
> 上限 150 行；结论带 file:line，无锚点处标【推断】；信息分区：【事实】/【建议】/【已批准决策】。

## S1 施工切片表
（待写）

## S2 代码级要点
（待写）

## S3 测试清单
（待写）

## S4 风险
（待写）

## S5 不做清单
（待写）

## S6 待确认
（待写）
## S1 施工切片表

【已批准决策】切片依据（temp/s1c-facts.md §A，不再讨论）：D1 ordinal 精确匹配/拒 contains/不 Trim；D2 新受控 kind + 方案 A 独立 DefinitionRef；D3 合同整理走 A1 通道；D4 与 S1-a 同批生效；D5 零工具调用且不得只信模型自述。

| 片 | 目标 | 触点文件 | 改动要点 | 验收断言（含反例） |
|---|---|---|---|---|
| 1 | 词表+spec 载体+序列化 | `GoalVerificationContracts.cs:136-150`；`GoalCheckContracts.cs:14-28`；`GoalVerificationPersistence.cs:29-36` | +常量 `TextAssertion="text-assertion"`；spec +`string? ExpectedText`；确认随 ChecksJson 序列化往返 | 断言：新旧 spec 序列化往返字段一致。反例：旧 spec（无该字段）反序列化不得抛异常、hash 不变 |
| 2 | 注册表：定义+拒绝集+哈希 | `GoalCheckDefinitionRegistry.cs:12-16`（record）、`:27-45`、`:52-58`、`:106-111` | record +`string? ExpectedText=null`；+1 条目（Ref 见 S2-3）；hash 载荷仅 `!IsNullOrEmpty(ExpectedText)` 时追加 `\|{ExpectedText}`；TryBuildCommand 拒绝集并入新 kind | 断言：新条目 hash=sha256("checks/text-assertion.md#equals\|text-assertion\|\|<text>")；旧三条目 hash 与改动前逐字节一致（金样值）。反例：`ExpectedText=" "`（纯空白）必须参与拼接，不得被当空丢弃 |
| 3 | Runner 纯文本分支+文本供给链 | `GoalCheckRunner.cs:129-151`（门禁 `:135-143` 之前）、`GoalSettlementWorker.cs:159-186`、`TurnExecutorAdapter.cs:151-165` | 门禁前插入 `if(text-assertion) return EvaluateAssistantText(...)`；GoalCheckContext +`FinalAssistantReply`（含 turnId/seq 来源）；Worker 经新读取入口按 (conversationId,turnId) 取 `turn.completed` 的 `reply`，缺失⇒failed(evidence_missing) | 断言：Workdir 空白时 text-assertion 仍产出终态报告；reply 与 ExpectedText ordinal 相等⇒green。反例：build/test/file-evidence 在 Workdir 空白仍 evidence_missing；reply 大小写或前后空白差异⇒failed |
| 4 | Policy 放行+证据要求 | `GoalCheckEvidencePolicy.cs:107-115`（分类）、`:117-122`（前缀校验样式）、`:199-211`（终局分派） | +`isTextAssertion` 分类；要求 EvidenceRef 前缀 `assistant-output:{turnId}@` 且与 context 来源一致；终局分派在 file-evidence 同层 `return report`；不要求 InvocationId/ReportRef/ExitCode | 断言：合规报告放行；未知 kind 仍 unsupported_check_kind。反例：缺前缀或 turnId 不一致⇒failed(EvidenceMissing) |

## S2 代码级要点

1.【事实】词表是字符串常量类非 enum（`GoalVerificationContracts.cs:136` 注释），新增 `public const string TextAssertion = "text-assertion";`（kebab-case 对齐既有 wire 值，`:137-150`）。
2.【事实】spec 新字段名：`GoalCheckContracts.cs:17`（Kind）附近 + `public string? ExpectedText`；null=非文本断言。需确认 `GoalVerificationPersistence.SerializeChecks` 白名单式序列化是否需登记新字段【推断：若按属性自动序列化则免登记，实施时以 SerializeChecks 实现为准】。
3.【事实】新 DefinitionRef 字面量：`"checks/text-assertion.md#equals"`（对齐既有 Ref 风格 `GoalCheckDefinitionRegistry.cs:20-25`）；该条目 `CommandTemplate=""`、`RequiresTestEvidence:false`，且并入 TryBuildCommand 的「永不建命令」拒绝集（`:106-111` 现仅显式拒绝 file-evidence）。
4.【已批准决策】哈希方案 A 精确拼接（现载荷 `sha256("{Ref}|{Kind}|{CommandTemplate}")`，返回 `"sha256:"+小写hex`，`GoalCheckDefinitionRegistry.cs:52-58`）：
   - `var payload = $"{d.DefinitionRef}|{d.Kind}|{d.CommandTemplate}";`
   - `if (!string.IsNullOrEmpty(d.ExpectedText)) payload += $"|{d.ExpectedText}";`
   - 「仅非空时拼接」的精确条件 = **IsNullOrEmpty，禁用 IsNullOrWhiteSpace**：D1 默认不 Trim ⇒ 纯空白期望文本是合法载荷，WhiteSpace 判断会把它当空丢弃（新条目语义错 + hash 不可复现）。旧三条目 ExpectedText=null ⇒ payload 字节与现状一致 ⇒ hash 不变。
   - 产出侧 hash 由 planner 计算（`GoalAcceptanceContractPlanner.cs:179/:239/:283`）——须改经 `ComputeDefinitionHash`/`TryGetDefinitionHash` 统一取值，禁止旁路拼串。
5.【事实】Runner 插入位置：`ExecuteOneAsync` 顶部、WorkingDirectory 门禁（`GoalCheckRunner.cs:135-143`）**之前**插入分支（方案 1，评审 Q5 推荐）；不触碰 `:137` 判据与既有 kind 路径。分支内 `string.Equals(spec.ExpectedText, context.FinalAssistantReply.Text, StringComparison.Ordinal)`；拒绝 contains、不 Trim（D1）。报告 EvidenceRef=`assistant-output:{turnId}@{seq}`【推断：字面量格式为评审建议，待 S6-3 确认】。
6.【事实】文本供给链：终态 reply 写入点 `TurnExecutorAdapter.cs:156-161`（`turn.completed` payload.reply）；读取入口按 `(conversationId, turnId)` 查 ConversationEvents(type=turn.completed)，或走 `ConversationTranscriptFold.cs:168`/`:118` 的 AssistantText。现状 `GoalCheckContext`/capsule 无正文字段（`GoalVerificationContracts.cs:13-58`），`GoalSettlementWorker.cs:159-186` 组装处需注入；缺失⇒failed(evidence_missing)，不得凭模型自述放行（D5）。
7.【事实】Policy 放行要点：分类标志区（`GoalCheckEvidencePolicy.cs:107-115`）+`isTextAssertion`；证据要求仿 file: 前缀校验（`:117-122`）；终局分派（`:199-211`）在 `isFileEvidence` 同层 `return report`。text-assertion **不是 executed kind** ⇒ 不适用 InvocationId/ReportRef/ExitCode 检查（`:124-142`）；未命中任何放行分支⇒仍落 `:209-211` unsupported_check_kind（fail closed）。
8.【事实+建议】零工具调用独立校验：记账=`GoalSettlementStore.cs:214-215`（evidence 范围 `:173-178` = [AcceptedSequence,TerminalSequence] 内 CountAsync(ToolCallRequested)）→`:276`→`:492/:501` 写回；父 Turn 覆盖完整，**不含子代理**（`:203-210` 委托用量单独汇总）。【建议】完成裁决时要求含 text-assertion 条件的 iteration.ToolCalls==0，落点 `ConservativeGoalIterationVerifier.cs:52-84`；违反⇒不通过。模型自述不作判据（D5）。
9.【事实】前端：不新增受阻码⇒`goalBlockerCodes.ts` 不改；未知码有兜底（`:86-89`、`GoalBanner.tsx:382-384`）。若 S6-1 裁决新增 blockerCode，须同步 `goalBlockerCodes.ts:20-83` + `goalBlockerCodes.test.ts:9-36` 白名单。

## S3 测试清单

【建议】新增 ≤8 条（模板对照 `GoalCheckRunnerTests.cs:621-623` 动态算 hash 的 file-evidence 用例结构）：

1. 序列化往返：text-assertion spec（ExpectedText="OK"）经 ChecksJson 写读一致；旧格式 spec 反序列化 ExpectedText=null 不抛异常。
2. 金样 hash：build/test/file-evidence 三条目 DefinitionHash 与改动前**逐字节**相等；新条目 hash=sha256("checks/text-assertion.md#equals|text-assertion||OK")【推断：以实现 payload 串为准，空 CommandTemplate 产生连续 "||"】。
3. 纯空白载荷：ExpectedText=" " 参与拼接且与空白 reply ordinal 相等⇒green（锁 IsNullOrEmpty 判断，防 IsNullOrWhiteSpace 回归）。
4. Runner 正例：Workdir=null + reply==ExpectedText ⇒ green，EvidenceRef 前缀 assistant-output: 且含 turnId。
5. Runner 反例：reply 大小写差异 / 前后空白差异 / contains 关系（期望 "OK"，回复 "OK!" 或 "xOKx"）⇒ 全部 failed（D1 ordinal 锁）。
6. 门禁回归锁：build/test/file-evidence 在 Workdir 空白 ⇒ evidence_missing（门禁行为不变，片 3 反例）。
7. Policy 负例：缺 assistant-output: 前缀或 EvidenceRef 中 turnId 与 context 来源不一致 ⇒ failed(EvidenceMissing)；未知 kind 仍 unsupported_check_kind（`:209-211`）。
8. 失效链：ExpectedText 变更 ⇒ spec hash 变化 ⇒ 同 CheckId 旧绿报告 invalidated `definition_changed`（`GoalCheckEvidencePolicy.cs:85-87`；验证须走新 epoch——同 epoch planner 早退不重算，`GoalAcceptanceContractPlanner.cs:45-53`）。

【事实】必须保持不变：
- `PlatformTests ~Goals` 基线 247 通过/0 失败（HEAD 64eaba1，父级已建库前基线）。
- `GoalCheckRunnerTests.cs:621-623` file-evidence 正/负用例；`GoalAcceptanceContractPlannerTests.cs:329` DefinitionRef 断言。
- 既有三类 kind 的全部 Runner/Policy 行为（含 unsupported_check_kind 降级路径）。

## S4 风险

1. 子代理工具调用不计入 ToolCalls（`GoalSettlementStore.cs:203-210`）⇒ "零工具调用"门槛可经委托绕过。控制：planner 对文本断言场景不派生 build/test 条件（合同只在空合同生成 `GoalSettlementWorker.cs:85-107`）＋ S6-2 裁决是否并入 SumDelegatedUsageAsync。
2. 拼接条件误用 IsNullOrWhiteSpace 丢纯空白断言。控制：用例 3 金样锁。
3. 纯文本分支误插门禁（`:135-143`）之后 ⇒ 无 Workdir 环境（`GoalSettlementWorker.cs:164-166` 可为 null）全量 evidence_missing，C2 达不成。控制：用例 4+6 双向锁。
4. Turn 已结束但无终态 reply（异常中断/空回复）⇒ 文本缺失。控制：failed(evidence_missing) 终态化，不回 pending 重试【推断：EvidenceUnavailable 回 pending 语义（`GoalCheckRecordStore.cs:233`）会造成永不收敛】。
5. 同 epoch 内期望文本变更不重算（planner 早退 `GoalAcceptanceContractPlanner.cs:45-53`）⇒ C3 只能靠 hash 变化在新 epoch 失效旧绿灯。控制：用例 8 明确走新 epoch 路径，并在合同整理（A1 通道，D3）时以新 DefinitionHash 重排检查。

## S5 不做清单

1. 不动三轴：CriterionRevision 无递增点（planner 硬编码 1，`GoalAcceptanceContractPlanner.cs:202/:260/:306`）、ContractVersion 无失效链读取、InputFingerprint 与期望文本无关——均维持现状，另卡处理。
2. 不接线 `GoalCheckInputIdentity`（零生产调用点）——避免一次性全量 `stale_input_fingerprint`（评审 Q2/风险 4）。
3. 不改 `GoalCheckRecordStore`（dedup 键吃 identity 三要素，kind 无关；新 spec 必须带齐三要素即可）。
4. 不改前端 `goalBlockerCodes.ts`（前提：不新增受阻码；若 S6-1 裁决新增则另计）。
5. 不引入 Agent 侧创建/修订/恢复 Goal 的工具（D3 禁止）；不做 contains/Trim/Unicode 归一放宽（D1 禁止）。

## S6 待确认

1. 零工具调用违反时的裁决形态：verdict 不通过（无新受阻码）vs 新 blockerCode（需前端 `goalBlockerCodes.ts:20-83` + 测试白名单同步）——需用户裁决。
2. 子代理工具调用是否并入"零工具调用"判定（现 `SumDelegatedUsageAsync` 单独汇总、不进 ToolCalls，`GoalSettlementStore.cs:203-210`）。
3. EvidenceRef 字面量 `assistant-output:{turnId}@{seq}` 是否采认（现仅为评审建议格式，未见既有先例）【推断】。
4. ExpectedText 是否设长度上限（进 hash 与 ChecksJson 的体积控制）；上限校验放 planner（生成时拒绝）还是 registry（登记时拒绝）。
5. D4 要求 S1-a（`be04d37`，已提交未部署）与本批同车生效——部署顺序与验证窗口需用户确认。

