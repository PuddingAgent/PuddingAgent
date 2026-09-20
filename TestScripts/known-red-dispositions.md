# 前端既有红：逐例定性台账（AdminJest）

> **用途**：门禁 `TestScripts/test-pudding-suite-gates.ps1` 的 `AdminJest` 目前是 `KnownRed = $null`
> （名单未登记 ⇒ **仅按预算判**）。**唯一**能把它收紧为"具体名单"的办法，是把每一例定性清楚并登记在此。
> **纪律**：本台账只写**有证据的判断**；没查清的写「待查」，**不得**用猜测填格。

## 定性分类（本台账口径）
| 类 | 含义 | 处置 |
|---|---|---|
| **A** | **测试滞后**：生产按设计改了（含职责迁移/重命名/格式收紧），测试未同步 | 改测试（并把证据写进测试注释） |
| **B** | **配置/文案滞后**：i18n/路由/常量等外部配置与测试期望不一致 | 改配置或改测试，视哪侧是真相 |
| **C** | **真实缺陷**：生产行为不符合契约 | **修生产**（不得改测试迁就） |
| **D** | **测试自身问题**：本身不成立/自相矛盾/依赖环境 | 重写或移除，须留指针 |

## 当前基线（2026-09-21 实测）
- `npx jest` 全量 ⇒ **`Tests: 10 failed, 1342 passed, 1352 total`（6 个红套件）**
- 门禁：`AdminJest.AllowedFailures = 10`、`KnownRed = $null`
- ⚠️ **稳定性**：其中 1 例**疑似 flaky**（见下方「疑似不稳定」一节）⇒ **预算保持 10、不按 9 收紧**，
  否则该 flaky 一抖动就会误报。

## 逐例台账（9 例待查 + 1 例 flaky；已修的都移到下方「已修完的例」）

| # | 套件 | 用例 | 类 | 证据/状态 |
|---|---|---|---|---|
| 1 | `src/utils/adminRoutes.test.ts` | `admin workspace menu routing › resolves every configured admin icon name to a React element` | **C（已修）** | 见「已修完的例」表 |
| 2 | `src/pages/chat/client/agentChatApi.test.ts` | `agentChatApi › loads historical process items only for the selected message` | **A（已修）** | 见「已修完的例」表 |
| 3 | `src/pages/agent-template-settings/agentTemplateOwnership.test.ts` | `Agent template and instance field ownership copy › frames workspace Agent settings as instance identity without model overrides` | 待查 | 疑似文案（i18n）期望不一致，未复核 |
| 4 | `src/pages/storage/index.test.tsx` | `StorageTrendChart › 渲染堆叠面积路径与图例标签` | 待查（疑似 B） | 早前列入"测试/配置滞后"，未复核 |
| 5 | `src/pages/chat/components/InputArea.test.tsx` | `InputArea status feedback › does not let the previous completed toast mask a new streaming state` | 待查 | 与语音无关，独立原因 |
| 6 | `src/pages/chat/components/InputArea.test.tsx` | `InputArea status feedback › switches into voice mode and sends a transcript with voice metadata` | **A（部分已证）** | 失败点：`Unable to find [data-testid="voice-conversation-panel"]`；见下方「语音归属」一节 |
| 7 | `src/pages/chat/components/InputArea.test.tsx` | `InputArea status feedback › shows the voice mode unavailable state when browser microphone capture is unavailable` | **A（同族）** | 同族语音用例，预期形态与现组件不一致；细节待补 |
| 8 | `src/pages/chat/hooks/useChatState.selection.test.tsx` | `useChatState session selection races › preserves the visible compact result when switching to the new compacted session` | 待查 | 与压缩会话切换竞态有关，未复核 |
| 9 | `src/pages/chat/components/IntentConsole.test.tsx` | `IntentConsole › sends voice transcript with voice metadata from the console boundary` | **A（同族）** | 同族语音用例（与 #6 同一归属问题） |
| 10 | `src/pages/chat/components/IntentConsole.test.tsx` | `IntentConsole › converts BMP images to PNG before uploading` | **A（已修）** | 见「已修完的例」表 |
| 11 | `src/pages/chat/components/DevPanel.test.tsx` | `DevPanel performance diagnostics › loads benchmark cases from the server and sends only the case prompt` | **A（已修）** | 见「已修完的例」表 |
| 12 | `src/pages/access-token-management/index.test.tsx` | `Access Token 管理页（ADR-075 §15.3） › 创建抽屉默认最小 scope，workspace 为空不能提交` | 待查 | 该套件耗时 209s，未复核 |
| 13 | `src/pages/access-token-management/index.test.tsx` | `Access Token 管理页（ADR-075 §15.3） › 撤销弹窗显示强确认警示，未确认不调用后端` | 待查 | 同上 |
| 14 | `src/pages/access-token-management/index.test.tsx` | `Access Token 管理页（ADR-075 §15.3） › 撤销 Modal 填写原因后提交 expectedVersion` | 待查 | 同上 |

## 已修完的例（不在上面的待查列表内，留档以免重复调查）
| 套件 | 用例 | 类 | 处置 |
|---|---|---|---|
| `src/utils/adminRoutes.test.ts` | `admin workspace menu routing › resolves every configured admin icon name to a React element` | **C（真实缺陷）** | 路由配置用了 `hdd`（storage）与 `key`（system-config/access-tokens）两个图标名，但 `src/layouts/AdminLayout/menuIcons.ts` 的映射表缺这两项 ⇒ `resolveAdminMenuRoutes` 只能原样透传字符串 ⇒ **这两个菜单项在生产里渲染不出图标**（该映射的注释明写：Umi 全局 layout 插件关闭后，它是唯一把名字换成组件的地方）。**补映射**（`HddOutlined` / `KeyOutlined`）后该套件 **8/8 全绿**。 |
| `execution-flow/TurnStatus.test.tsx` | `retry 节点 → connecting（等待/重连模型）` | **A** | 手写 message 非 canonical 形态 ⇒ 改为生产实际格式 `LLM call retry 2/3. ...`（证据：`PuddingRuntime/Services/DirectLlmClient.cs:273` + `src/pages/chat/utils/modelRetry.ts` 刻意严格的正则） |
| `AgentMessageBubble.test.tsx` | `shows a sanitized reasoning summary ...` | **A** | 错归属断言 ⇒ 改「职责边界」断言（`queryByTestId('reasoning-disclosure-row') === null`） |
| `AgentMessageBubble.test.tsx` | `shows the latest reasoning line and expands ...` | **A** | 职责已迁至 `ReasoningDisclosureRow`（其测试已覆盖）⇒ 删除 49 行用例 + 原地留指针 |
| `src/pages/chat/client/agentChatApi.test.ts` | `agentChatApi › loads historical process items only for the selected message` | **A** | 生产已为历史过程项请求接入**取消信号**（切消息时中止在途请求）⇒ 期望由 `{ method: 'GET' }` 改为 `objectContaining({ method: 'GET', signal: expect.any(AbortSignal) })`（既不放松 method 检查，也不耦合信号内部形态）。该套件现全绿。 |
| `src/pages/chat/components/IntentConsole.test.tsx` | `IntentConsole › converts BMP images to PNG before uploading` | **A（契约变更滞后）** | **契约已变**：本地不再转换 BMP 而是**保留原文件**、由**服务端预处理**生成模型可用副本。三重依据：① `visionArtifactImage.ts` 文档注释明写该策略；② 同一模块的单测 `visionArtifactImage.test.ts` 断言 `image/bmp` **原样返回**（“uploads %s unchanged for server preprocessing”，当前为绿）；③ `git log -S "image/bmp"` 表明该条随模块引入（`74ae4e0`）就是有意为之。⇒ 用例重写为“**上传原文件不变**（name=clipboard.bmp、type=image/bmp）+ **未走 canvas 转换**（`drawImage` 未被调用）”并附依据注释。 |

> 本次修复后，`IntentConsole.test.tsx` 由 2 红降为 1 红（仅剩语音族）。

### 疑似不稳定（flaky，需单独查）
| 套件 | 用例 | 证据 |
|---|---|---|
| `src/pages/access-token-management/index.test.tsx` | `Access Token 管理页（ADR-075 §15.3） › 撤销 Modal 填写原因后提交 expectedVersion` | **该套件本轮未被改动**，但在两次全量之间失败了：上一轮失败名单**不含**它（该盘 failed=10），本轮**含**它（仍然 failed=10）⇒ 同一代码下结果不一致 = 不稳定的强证据。该套件单次耗时 ≈ 209s。**待查方向**：是测试自身时序/清理问题，还是生产侧竞态（如果是竞态那就是真缺陷）。 |

> 本次两个套件**整体转绿**（2 suites / 12 tests 全过），全量 failed 由 13 降到 **10** ⇒ 这两处原本共 **3 例**在失败（含台账命名列表未单独列出的一例）。

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
3. 每修完一例：跑该文件 → 跑全量 → **收紧 `AllowedFailures`** → 更新本台账与 `README.md` 基线。
