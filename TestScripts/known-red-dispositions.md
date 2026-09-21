# 前端既有红：逐例定性台账（AdminJest）

> **用途**：门禁 `TestScripts/test-pudding-suite-gates.ps1` 的 `AdminJest` **已登记具体名单**
> （`KnownRed` 三例，按**用例身份**判定）。本台账是 `KnownRed` 的**唯一依据**：只有在这里定性清楚、并带着证据的用例，才允许进 `KnownRed`。
> **纪律**：本台账只写**有证据的判断**；没查清的写「待查」，**不得**用猜测填格。

## 定性分类（本台账口径）
| 类 | 含义 | 处置 |
|---|---|---|
| **A** | **测试滞后**：生产按设计改了（含职责迁移/重命名/格式收紧），测试未同步 | 改测试（并把证据写进测试注释） |
| **B** | **配置/文案滞后**：i18n/路由/常量等外部配置与测试期望不一致 | 改配置或改测试，视哪侧是真相 |
| **C** | **真实缺陷**：生产行为不符合契约 | **修生产**（不得改测试迁就） |
| **D** | **测试自身问题**：本身不成立/自相矛盾/依赖环境 | 重写或移除，须留指针 |

## 当前基线（2026-09-21 实测）
- `npx jest` 全量 ⇒ **`Tests: 3 failed, 1349 passed, 1352 total`（2 个红套件）**
- 门禁：`AdminJest.KnownRed` 已登记 **3 例**（按**用例身份**判定；预算由名单派生，`AllowedFailures` 旋钮已删除）
- ✅ **剩余 3 例全是语音族**（已查清：孤儿组件 `VoiceConversationPanel.tsx` 生产内无任何引用）
  ⇒ 等用户定“接线 vs 移除”后再动测试（**无决策不动测试、不删组件**）。
- ✅ **稳定性**：此前 1 例 flaky（access-token 套件并行下超时）**已治好**，见下方「flaky 已修」一节。

## 逐例台账（3 例待查 = 语音族；已修的都移到下方「已修完的例」）

| # | 套件 | 用例 | 类 | 证据/状态 |
|---|---|---|---|---|
| 1 | `src/utils/adminRoutes.test.ts` | `admin workspace menu routing › resolves every configured admin icon name to a React element` | **C（已修）** | 见「已修完的例」表 |
| 2 | `src/pages/chat/client/agentChatApi.test.ts` | `agentChatApi › loads historical process items only for the selected message` | **A（已修）** | 见「已修完的例」表 |
| 3 | `src/pages/agent-template-settings/agentTemplateOwnership.test.ts` | `Agent template and instance field ownership copy › frames workspace Agent settings as instance identity without model overrides` | **A（已修）** | 见「已修完的例」表 |
| 4 | `src/pages/storage/index.test.tsx` | `StorageTrendChart › 渲染堆叠面积路径与图例标签` | **A（已修）** | 见「已修完的例」表 |
| 5 | `src/pages/chat/components/InputArea.test.tsx` | `InputArea status feedback › does not let the previous completed toast mask a new streaming state` | **A（已修）** | 见「已修完的例」表（三处漂移叠加） |
| 6 | `src/pages/chat/components/InputArea.test.tsx` | `InputArea status feedback › switches into voice mode and sends a transcript with voice metadata` | **A（部分已证）** | 失败点：`Unable to find [data-testid="voice-conversation-panel"]`；见下方「语音归属」一节 |
| 7 | `src/pages/chat/components/InputArea.test.tsx` | `InputArea status feedback › shows the voice mode unavailable state when browser microphone capture is unavailable` | **A（同族）** | 同族语音用例，预期形态与现组件不一致；细节待补 |
| 8 | `src/pages/chat/hooks/useChatState.selection.test.tsx` | `useChatState session selection races › preserves the visible compact result when switching to the new compacted session` | **A（已修）** | 见「已修完的例」表（承载载体迁移） |
| 9 | `src/pages/chat/components/IntentConsole.test.tsx` | `IntentConsole › sends voice transcript with voice metadata from the console boundary` | **A（同族）** | 同族语音用例（与 #6 同一归属问题） |
| 10 | `src/pages/chat/components/IntentConsole.test.tsx` | `IntentConsole › converts BMP images to PNG before uploading` | **A（已修）** | 见「已修完的例」表 |
| 11 | `src/pages/chat/components/DevPanel.test.tsx` | `DevPanel performance diagnostics › loads benchmark cases from the server and sends only the case prompt` | **A（已修）** | 见「已修完的例」表 |
| 12 | `src/pages/access-token-management/index.test.tsx` | `Access Token 管理页（ADR-075 §15.3） › 创建抽屉默认最小 scope，workspace 为空不能提交` | **D（已修）** | 见「flaky 已修」一节（并行资源竞争击穿 30s 默认超时，已按标定放宽至 90s） |
| 13 | `src/pages/access-token-management/index.test.tsx` | `Access Token 管理页（ADR-075 §15.3） › 撤销弹窗显示强确认警示，未确认不调用后端` | **D（已修）** | 同上（同一套件、同一根因） |
| 14 | `src/pages/access-token-management/index.test.tsx` | `Access Token 管理页（ADR-075 §15.3） › 撤销 Modal 填写原因后提交 expectedVersion` | **D（已修）** | 同上（同一套件、同一根因） |

## 已修完的例（不在上面的待查列表内，留档以免重复调查）
| 套件 | 用例 | 类 | 处置 |
|---|---|---|---|
| `src/utils/adminRoutes.test.ts` | `admin workspace menu routing › resolves every configured admin icon name to a React element` | **C（真实缺陷）** | 路由配置用了 `hdd`（storage）与 `key`（system-config/access-tokens）两个图标名，但 `src/layouts/AdminLayout/menuIcons.ts` 的映射表缺这两项 ⇒ `resolveAdminMenuRoutes` 只能原样透传字符串 ⇒ **这两个菜单项在生产里渲染不出图标**（该映射的注释明写：Umi 全局 layout 插件关闭后，它是唯一把名字换成组件的地方）。**补映射**（`HddOutlined` / `KeyOutlined`）后该套件 **8/8 全绿**。 |
| `execution-flow/TurnStatus.test.tsx` | `retry 节点 → connecting（等待/重连模型）` | **A** | 手写 message 非 canonical 形态 ⇒ 改为生产实际格式 `LLM call retry 2/3. ...`（证据：`PuddingRuntime/Services/DirectLlmClient.cs:273` + `src/pages/chat/utils/modelRetry.ts` 刻意严格的正则） |
| `AgentMessageBubble.test.tsx` | `shows a sanitized reasoning summary ...` | **A** | 错归属断言 ⇒ 改「职责边界」断言（`queryByTestId('reasoning-disclosure-row') === null`） |
| `AgentMessageBubble.test.tsx` | `shows the latest reasoning line and expands ...` | **A** | 职责已迁至 `ReasoningDisclosureRow`（其测试已覆盖）⇒ 删除 49 行用例 + 原地留指针 |
| `src/pages/chat/client/agentChatApi.test.ts` | `agentChatApi › loads historical process items only for the selected message` | **A** | 生产已为历史过程项请求接入**取消信号**（切消息时中止在途请求）⇒ 期望由 `{ method: 'GET' }` 改为 `objectContaining({ method: 'GET', signal: expect.any(AbortSignal) })`（既不放松 method 检查，也不耦合信号内部形态）。该套件现全绿。 |
| `src/pages/chat/components/IntentConsole.test.tsx` | `IntentConsole › converts BMP images to PNG before uploading` | **A（契约变更滞后）** | **契约已变**：本地不再转换 BMP 而是**保留原文件**、由**服务端预处理**生成模型可用副本。三重依据：① `visionArtifactImage.ts` 文档注释明写该策略；② 同一模块的单测 `visionArtifactImage.test.ts` 断言 `image/bmp` **原样返回**（“uploads %s unchanged for server preprocessing”，当前为绿）；③ `git log -S "image/bmp"` 表明该条随模块引入（`74ae4e0`）就是有意为之。⇒ 用例重写为“**上传原文件不变**（name=clipboard.bmp、type=image/bmp）+ **未走 canvas 转换**（`drawImage` 未被调用）”并附依据注释。 |
| `src/pages/chat/hooks/useChatState.selection.test.tsx` | `useChatState session selection races › preserves the visible compact result when switching to the new compacted session` | **A（承载载体迁移）** | 先实测取证：debug 打印得 `DEBUG_TURNS=["A answer",""]` ⇒ 压缩后的 turn **存在但 `answerMarkdown` 为空串**，旧断言的两个措辞（`覆盖 8 条历史消息` / `已整理 8 条历史消息`）都永远不可能出现。真因：**承载载体已迁移**——文案写在 `assistant.timelineItems[].message`（`useCompaction.ts:140-167`），而 `answerMarkdown` 被**有意留空**（生产注释：“运行中不把「正在压缩上下文…」塞进 answerMarkdown：正文区是给用户看的答案…（用户反馈 2026-09-19：压缩卡片显示效果乱）”）。另需分清三条路径的措辞：手动 `/compact` 收敛为 **“上下文压缩完成”**（`useCompaction.ts:585`）、事件驱动路径用 `已整理 N 条历史消息`（`:467`）、recovery 路径仍用 `上下文已压缩，覆盖 N 条…`（`chatStateUtils.ts:389`）。⇒ 断言改为拼接 `answerMarkdown + timelineItems[].message` 后断言 **“上下文压缩完成”**（既贴真相又对载体不敏感）；临时 debug 行已移除，该文件 **18/18 全绿**。 |
| `src/pages/agent-template-settings/agentTemplateOwnership.test.ts` | `Agent template and instance field ownership copy › frames workspace Agent settings as instance identity without model overrides` | **A（文案迁移）** | 该用例用 `fs.readFileSync` 断言**源码文本**。旧读目标 `workspace/[id]/index.tsx` 已不是文案的家：`实例职责` 现住在 `workspace/[id]/WorkspaceAgentSettingsDrawer.tsx:263`；而 `模板默认值预览 / 个性化覆盖 / 覆盖头像 / 实例只保存工作区内身份、头像和启停状态` **生产内已完全消失**（全库检索只命中本测试文件，`git log -S` 最后触碰于 `6e2fd05`、`4b6a3d7` 两次 UX 改版）⇒ 是**有意改写**而非回归。处置：改读新归属文件，断言改为现存文案（`实例职责`、`模板只在创建时提供初始快照；Agent 创建后独立演进。`、`来源模板`）并**保留反向断言**（不含 `模型覆盖` / `高级 Prompt 覆盖`）⇒ 保住用例原意：工作区侧编辑的是实例身份、不提供模型覆盖。 |
| `src/pages/storage/index.test.tsx` | `StorageTrendChart › 渲染堆叠面积路径与图例标签` | **A（时间炸弹）** | 组件按 `Date.now() - days*24h` **过滤最近 days 天**、且不足 2 天即渲染空态（文案「历史快照不足…」）；用例 fixture 却把日期**写死** `2026-08-20/21/22`，到 2026-09-21 已滑出 `days=30` 窗口 ⇒ 只剩 ≤1 点 ⇒ 空态 ⇒ 无端变红。**生产逻辑正确。** 处置：fixture 改为按 `daysAgoIso(n)` **相对当前时间**生成并附注原因。同 describe 的「历史点不足时显示提示」一例本就取 1 点，保持不动。 |
| `src/pages/chat/components/InputArea.test.tsx` | `InputArea status feedback › does not let the previous completed toast mask a new streaming state` | **A（三处漂移叠加）** | 该例被三处**有意变更**同时打断，逐层查实：① **测试替身过期**——`./ComposerStatusDetails` 替身只渲染「运行中 N」却丢掉 `summary.statusLabel`，而生产的状态文案正是靠它渲染（`IntentConsole.tsx:736` `statusLabel: displayStatusText`、`ComposerStatusDetails.tsx:10`）⇒ 替身补齐该字段；② **UI 整合**——状态文案随 `runtimeDetails` **只在工具栏圆环气泡打开时**渲染（生产注释：用户诉求 2026-09-19，旧上下文指示条已移除、改为圆环 + 点击出明细面板；`IntentConsole.tsx:969` 把 `<ComposerStatusDetails>` 作为 `runtimeDetails` 传给 `ContextUsageRing`）⇒ 测试里用替身直渲 `runtimeDetails`（考察的是**状态文案状态机**，与圆环外壳无关）；③ **占位文案改版**——运行中 placeholder 已改为「继续输入：Enter 排队，Ctrl/Cmd+Enter 插嘴当前 Agent…」（`IntentConsole.tsx:843`）⇒ 同步期望。 |

> 本次修复后，`IntentConsole.test.tsx` 由 2 红降为 1 红（仅剩语音族）。

## flaky 已修（D 类：并行资源竞争击穿 30s 默认超时）
| 套件 | 用例 | 类 | 处置与证据 |
|---|---|---|---|
| `src/pages/access-token-management/index.test.tsx` | `创建抽屉默认最小 scope…` / `撤销弹窗显示强确认警示…` / `撤销 Modal 填写原因后提交 expectedVersion` | **D（已修）** | 失败原因实测为 `Exceeded timeout of 30000 ms for a test`（**超时**，非断言不符、非生产行为问题）。**标定数据**：单独跑 ⇒ `8/8 通过、整套 49.092s`；全量并行 ⇒ 整套 **209s（约 4 倍）**且上述三例**间歇性**超时（同一提交下时有时无 ⇒ 先前表现为 flaky）。⇒ 按 jest 官方建议在本文件顶部 `jest.setTimeout(90_000);` 并写明标定依据。**验证**：修复后全量 ⇒ 该套件三例全部转绿，全量 failed **10 → 7**。 |

> 该套件单次 ≈209s，仍是全量最慢；若今后再出现“同一提交下时有时无”，优先怀疑**并行资源竞争**而非生产缺陷。

> 本次两个套件**整体转绿**（2 suites / 12 tests 全过），全量 failed 由 13 降到 **10** ⇒ 这两处原本共 **3 例**在失败（含台账命名列表未单独列出的一例）。

## 通用教训（从这些例里抽出来的）
1. **fixture 写死日期 = 时间炸弹**：凡断言依赖“最近 N 天/时间窗口”的用例，fixture 必须**相对 `Date.now()` 生成**，否则会“到某一天自己变红”。
2. **断言源码文本的用例最容易腐化**：文件职责迁移、文案改写都会打断它；判“测试滞后 vs 回归”用三件套：**全库检索现址 + `git log -S` 溯源 + 生产侧注释/语义**。
3. **超时型失败先分清**：`Exceeded timeout` ⇒ 计时/资源；断言不符 ⇒ 逻辑/契约。
4. **一层套一层的漂移要逐层查**：同一个用例可能同时被“替身过期 + UI 整合 + 文案改版”打断；**每修一层就跑一次**，看失败断言是否**往后移**（失败点后移 = 前面的层已修对）。
5. **`jest.mock` 工厂会被提升到 `import` 之前** ⇒ 工厂内**不能**引用外层 import 的 `React`，必须在工厂内 `require('react')`（否则整套件报 “Test suite failed to run”）。

## 语音归属一节（#6/#7/#9 的共同背景，已核查的三条事实）
1. `src/pages/chat/components/InputArea.tsx` 现在**只是别名转出**：`export default IntentConsole;`
   ⇒ `InputArea.test.tsx` 实际测的就是 `IntentConsole`。
2. **`VoiceConversationPanel.tsx`（529 行、自带测试）在生产代码中无人引用** ——
   全库检索 `VoiceConversationPanel` 只命中它自己与其测试；`git log -S "VoiceConversationPanel"`
   在 `InputArea.tsx` / `ChatMain.tsx` 上**零命中**（该文件只有一个初始导入提交）。
   ⇒ 它是**孤儿组件**：要么**接线**（谁渲染它、何时渲染，需产品决策），要么**删除**。
3. `ChatMain.test.tsx` 里出现的 `voice-conversation-panel` 是 **mock 掉的 `./IntentConsole` 桩**里手写的 div，
   **不能**当作"生产已渲染该面板"的证据。

**⚠️ 处置决定（本轮不做）**：#6/#7/#9 涉及"面板该不该由控制台渲染"的**契约选择**（接线 vs 移除孤儿组件），
无明确决策前**不动测试、不删组件**（避免"改断言迁就实现"与误删）。先把事实登记在此。

## 下一步
1. 逐例补齐 #1/#2/#3/#4/#5/#8/#10/#11/#12/#13/#14 的定性（先读用例断言 + 生产侧对应行为/文案）。
2. 语音归属（#6/#7/#9）等"接线 vs 移除"决策后再动。
3. 每修完一例：跑 `-Only AdminJest` → 跑全量 → **从 `KnownRed` 移除该例**（预算由名单派生，自动收缩）→ 更新本台账与 `README.md` 基线。
