# WinUI 3 设置中心：DeepSeek 迁移任务书

日期：2026-09-27。范围：把 PuddingPlatformAdmin 的管理功能迁入 PuddingDesktop 原生设置。本文可直接作为 DeepSeek 的任务输入，不依赖聊天记录。

## 1. 用户裁定与本轮交付

用户要求参考 WorkBuddy 截图的入口与设置面板组织方式：从客户端菜单/设置入口打开居中面板，左侧分类、右侧选项卡与圆角卡片。本轮先搭骨架和占位；后续真实表单、数据读取与保存由 DeepSeek 完成。截图是视觉参考，不是功能规格；不引入 WorkBuddy 的积分、套餐、邀请、锁屏运行等本项目没有的功能。

**客户端与 Core 在同一进程，直接调用现有 Core 服务方法。不要新建 HTTP 客户端、REST API 适配层或逐接口转发函数。不要为迁移再封装一套 SettingsClient/SettingsApi/Repository。** Web 的 API 文件只用于追踪已有行为、参数与权限，并非新客户端的实现目标。

当前骨架有 **17 个分类、49 个选项卡、64 张卡片**。外观/材质/布局保留真实功能；运行中心已有入口可用；其他卡片禁用动作并显示迁移状态。现有聊天中的模型/密钥和基础角色编辑仍可用，卡片以“部分已有”标记，不能宣称所有模型/角色设置已原生化。

入口包括标题栏 Pudding 应用菜单、文件→设置、原生聊天侧栏设置。应用菜单另有外观与记忆直达。弹出面板包含搜索、无结果提示、关闭按钮、Esc、Tab 焦点循环、背景输入隔离及关闭后焦点恢复；窄窗口使用 NavigationView 自适应折叠。页面切换保留选项卡选择；搜索按分类、页签、卡片、字段名及任务 ID 匹配。

没有在本轮接入任何新增业务写入、迁移生产数据或部署到现有运行实例。占位页不是功能验收。

## 2. 先读这些文件

所有路径相对于仓库 `E:\github\AgentNetworkPlan\PuddingAgent`。

| 文件 | 用途 |
|---|---|
| `Agents.md`、`Agents-Hygiene.md`、`code_map.md` | 当前架构和精确提交规则；最新 WinUI 3 裁定覆盖历史 WPF 约束 |
| `Docs/Features/ADR-Desktop-WinUI3-Shell-Core-Boundary-2026-09-27.md` | 产品边界与进程内装配 |
| `Docs/Conventions/组件化交付规程.md` | 新组件先独立验证，再接入；禁止反向引用 |
| `Source/PuddingDesktop.Foundation/SettingsCatalog.json` | 分类、页签、卡片稳定 ID、字段清单、Web 来源、任务 ID 的机器可读目录 |
| `Source/PuddingDesktop.Foundation/SettingsCatalog.cs` | 纯展示目录与搜索；无服务调用、无业务配置 |
| `Source/PuddingDesktop/MainWindow.xaml` | 居中设置层、左侧 NavigationView、TabView、保留的外观控件 |
| `Source/PuddingDesktop/MainWindow.Settings.cs` | 导航、搜索、卡片渲染、焦点、快捷入口；后续按 card.Id 替换占位 |
| `Source/PuddingDesktop/MainWindow.Kernel.cs` | 现有数据目录、运行中心、原生聊天入口 |
| `Source/PuddingDesktop.Composition/DesktopKernelFactory.cs` | 进程内 Host、DI scope、停止/排空及会话生命周期 |
| `Source/PuddingDesktop.Composition/InProcessChatClient.cs` | 已有直接 Core 调用的实现参考；不要照此另起完整管理 API 转发层 |
| `Source/PuddingPlatformAdmin/config/routes.ts` | 管理路由的范围依据 |
| `Source/PuddingPlatformAdmin/src/services/platform/api.ts` | Web 字段/行为追踪入口；不是原生网络客户端蓝图 |
| `Docs/Tasks/Desktop-Admin-Settings-Source-Inventory-2026-09-27.md` | 按实际源文件抽取的字段、卡片、按钮与调用证据附录 |

## 3. 实现边界与调用方式

### 3.1 直接调用，保留编译期边界

现有结构是 WinUI Shell → Foundation / Composition；Composition → Core。Foundation 是 BCL 叶组件，不能引入 Host、ASP.NET、EF、文件配置业务类型。Shell 的 csproj 已有编译期禁止业务工程引用的门禁，不能为“直接调用”删掉它。

在现有 Composition 装配位置获得正确的服务生命周期并绑定已有业务方法给页面行为；执行的是进程内方法调用。不要在 View 中创建 HttpClient、构造 JWT、访问 localhost API、new Controller 或打开 SQLite。没有必要复制每个 Web endpoint 成一个原生接口/转发方法。展示代码/选择状态留在 WinUI；验证、保存、授权、索引、清理和运行状态继续归 Core。

若原业务只存在 Controller 内（账号、团队、部分工作区 CRUD 目前如此），将**有实际业务职责**的逻辑下沉为可独立测试的 Core 应用操作，Web 和 Desktop 共享它；这属于消除业务耦合，不是额外包装 REST。单独提交与验证后再接 UI。不要直接调用控制器方法并伪造 HttpContext。

### 3.2 生命周期与状态

每次异步操作绑定当前 Host、页面取消令牌与所选 workspaceId/agentId。停止 Core 时取消、排空后释放 scope；不要把 scoped DbContext 或旧 Host 服务保存在窗口单例中。迟到结果不得覆盖新选择/新 Host 的页面。仅在 UI Dispatcher 更新控件，离开页面停止轮询。

读操作显示加载、空数据、Core 未就绪、失败重试、无权限。写操作显示脏表单、字段校验、保存中、防重复提交、成功/失败；失败保留草稿；有版本的操作冲突后重新读取，让用户比较，不强制覆盖。切页/关闭存在未保存修改时才提示，不能对普通浏览重复确认。

配置文件优先，保存沿用 Core 的锁与原子替换。只提交已编辑字段，不能通过默认 DTO 值抹除未显示的高级配置。成功后重新读取真实值；按真实契约显示即时生效/后续调用生效/重开 Desktop 生效。

### 3.3 本机身份与敏感能力

原生客户端不增加登录页。本机聊天现有 `single-user` 身份不等于已具备所有 Web Admin 身份；DS-00 必须明确本机管理授权如何映射既有业务规则，并用测试固定。尤其 AccessToken Owner、scope、工作区授权与撤销审计不能靠任意构造用户名跳过。Web/远程认证保持独立，不能给远程 API 增加匿名准入。

密钥读取只返回 HasKey 或元数据；新值单向写入，保持/替换/清除为明确操作。Token 明文仅创建时显示一次。不要显示已有密钥、把 secret 写进 JSON 目录/日志/截图或生成迁移文档样例。保留页面已有破坏性动作确认（删除、归档、清理、撤销、冻结），取消确认不得调用 Core。

## 4. 已核对的 Core 复用入口与缺口

下表是源代码追踪入口，不是要求新增的类型。

| 领域 | 直接复用入口 | 需要注意 |
|---|---|---|
| LLM 服务商/模型 | `LlmProviderFileService`；`LlmProviderApiController` / `LlmModelApiController` 的真实调用 | 基础原生局部编辑已存在；保留高级字段及密钥语义 |
| 配额 | `LlmProviderApiController.GetQuota/UpsertQuota/ResetDailyQuota`（具体方法名以源码为准） | 当前 quota GET/PUT/reset-daily 均 `NoContent()`；先完成 Core 实现，不能伪造保存成功 |
| 语音 | `VoiceProviderFileService`；`VoiceProvidersController` | 配置为 `config/voice/providers.json`，与 LLM 配置分开 |
| 全局模板 | `AgentTemplateFileService`；`GlobalAgentTemplateApiController` | 模板目录与 Markdown 为既有真源；包含预设列表/导入 |
| 角色实例 | `WorkspaceAgentFileService`、`IAgentAccessLevelService`；`WorkspaceAgentApiController` | 模板 ID、实例 ID、工作区 ID 不混用；权限与冻结检查保留 |
| 工具 | `IPuddingToolCatalogService`、`IToolPermissionPolicyService`；`CapabilityApiController` | ManifestOnly 不是已加载可执行插件 |
| Skill Hub | `ISkillHubService`；`SkillHubController`、`SkillPackageApiController` | Hub 技能和旧文件包分开；发布版本/进化/安装台账保留 |
| 记忆 | `IMemoryLibraryAdminService`；`MemoryLibraryAdminController` | 所有选择都携带 workspaceId/agentId；归档与来源语义保留 |
| 存储 | `StorageInventorySnapshotStore`、`StorageInventorySampler`、`StorageMaintenanceCoordinator`、`StorageMaintenanceJobStore`、`StorageRetentionPolicyService` | 总览读缓存；刷新是异步 refreshId；清理通过作业，不由 UI 枚举/删除文件 |
| 密钥 | `IKeyVaultService`；`KeyVaultController` | 不调用明文注入功能来展示密钥列表 |
| 审批 | `IToolApprovalAllowlistStore`、`IToolApprovalAuditStore`；`ToolApprovalAdminApiController` | Controller 中的筛选、校验与审计也要核对，不是取到 store 就可跳过 |
| AccessToken | `ExternalAccessTokenService`、`ExternalTaskApiOptionsProvider` | 明文一次、版本并发与 Owner 身份是业务约束；不能移植 HTTP Context 依赖 |
| 用户/角色/团队/部分工作区 | `AppUserApiController`、`AppRoleApiController`、`TeamApiController`、`WorkspaceApiController` 目前直接依赖 `PlatformDbContext` | 先下沉应用操作，WinUI 不直接接 DbContext |
| 诊断 | `IRuntimeActivitySink`、相关 Timeline/事件/Run 服务；`RuntimeDiagnosticsController` 等 | 按真实事件和游标呈现，避免轮询叠加和假进度 |
| 编排 | `IAgentOrchestrationStore`、`AgentOrchestrationAuthoringService`、`AgentOrchestrationHttpHookService` | 管理、修订、布局、执行分别复用现有职责；编辑图不等于发布或运行 |

其他现场缺口：Web 会话页 Agent 筛选当前 `options={[]}`，原生迁移应接真实角色目录并登记修复；首页 Core 状态卡在 Web 源码中为常量“就绪”，原生必须使用 `IDesktopKernel.Snapshot`。服务库中存在但 Web 页面未使用的 token-events/rebuild 等函数，不自动扩展为本轮必须移植的选项。

## 5. 路由全覆盖与边界

| 当前 Web 入口 | 原生归属 | 处置 |
|---|---|---|
| `/` | 用量与概览/管理概览 | 汇总卡与快捷入口 |
| `/llm-resource-pool` | 模型与服务商 | 全量迁移可用选项，配额缺口单列 |
| `/voice-models` | 语音模型 | 服务商、TTS、ASR |
| `/global-agent-template` | 角色与模板 | 模板目录、预设导入与编辑 |
| `/workspace/:id`、`/pudding/workspaces` | 工作区与渠道 + 角色与模板 | 工作区 CRUD、实例六组设置、工作流/知识库/技能/渠道/成员 |
| `/workspace`、`/workspace/:id/settings`、`/workspace-agent-template` | 同上 | 现有重定向不是额外业务页，不复制旧入口 |
| `/capability-management` | 工具与插件 | 注册表、插件包 |
| `/skill-management` | 技能与进化 | 六个原有页签，包括旧文件包 |
| `/keyvault` | 密钥与审批 | 元数据/轮换/删除/引用 |
| `/memory-library` | 记忆资料库 | 树、Book、章节、搜索、来源 |
| `/storage` | 存储与清理 | 总览、清理、保留策略 |
| `/tool-approval/allowlist`、`/tool-approval/audit` | 密钥与审批 | 白名单、事件审计、分类器健康 |
| `/system-config/access-tokens` | 外部访问 | Token 生命周期 |
| `/system-config/user-management`、`team-management`、`role-management` | 用户与权限 | Web/远程账号管理，不改变本机免登录 |
| `/runtime-management` | 运行与节点 | 节点列表、详情、能力、冻结；本机生命周期仍在现有运行中心 |
| `/session` | 会话与诊断/会话 | 只读表格/时间线与筛选 |
| `/diagnostics/overview`、`timeline`、`subagent-runs` | 会话与诊断 | 对应三个页签 |
| `/stats/tokens` | 用量与概览/Token 统计 | 汇总、趋势、模型明细、上下文层分析 |
| `/orchestration` | 任务与编排/编排配置 | 设置提供入口；复杂画布使用独立原生工作页 |
| 工作区任务入口、`workspace-tasks/*` | 任务与编排/调度、看板 | 虽非独立顶级管理路由仍纳入配置迁移；看板为独立工作页 |
| GlobalActions 的语言/主题/帮助 | 通用 | 主题已提供；语言与帮助待接入 |
| `/chat`、`/welcome` | 现有原生聊天/首次使用 | 非本轮重建对象；保留现有功能 |
| `/user/login`、Web `/bootstrap` | 保留 Web | Desktop 不复制账号登录/管理员口令初始化；复用本机首次使用 |
| `404`、`table-list`、`Admin.tsx`、旧 Welcome 展示 | 不迁移 | 无当前业务设置路由的脚手架/示例；不计入管理配置清单 |
| ProLayout `debug` SettingDrawer | 不迁移 | Web 开发工具，原生外观已有独立入口 |
| 截图套餐/积分/邀请/活动/开机自启/代理等 | 不自动新增 | 截图参考，不是 Pudding Web 既有配置；新增需求另立任务 |

## 6. DeepSeek 原子任务与依赖

每个任务依次做：核对源字段与方法 → 独立业务/展示验证 → 页面接入 → 集成验证 → 精确提交 → 更新卡片状态及本文。不要一次改完 17 个分类。优先完成高频配置，再迁移运维/数据管理。每项的字段范围见第 7 节及源证据附录。

### DS-00 — 接入基线与生命周期（P0，先行）

状态：**已完成（2026-09-27）**。接入方式、本机身份结论、逐步接入清单与复现命令见 [DS-00 设置接入基线与生命周期](../Features/Desktop-Settings-Operation-Boundary-2026-09-27.md)。

保留已完成骨架；确定本机管理权限与现有 Composition 的方法绑定位置。只为实际业务职责建立必要操作，不创建通用管理 API 包装器。实现并测试 Host 未就绪、停止中拒绝新操作、取消/排空、实例选择代次、版本冲突。保留外观配置和运行中心入口；补快捷键可单独切片。交付：一页接入说明、生命周期集成测试、无 Core 时所有设置分类仍可浏览。

已交付：`IDesktopKernel.RunSettingsAsync` + `SettingsOperationGate`（Foundation，BCL 叶）；`DesktopKernelFactory.Session` 实现 `ISettingsOperationHost`，每操作一个 Core DI 作用域；`LocalDesktopIdentity` 固定本机身份；13 项独立测试 + 1 项真实 Host 集成测试 + 6 项窗口检查。

### DS-01 — 通用、语言与关于（P1；依赖 DS-00）

状态：**已完成（2026-09-27）**。卡片 `language`、`help`、`about` 已从占位改为原生内容，catalog 与 CSV 状态同步为「已接入」。

保留主题/材质/宽度保存，迁移语言选择与帮助。关于读取实际构建版本，不写死版本；不展示调试路径作为普通用户表单。语言资源未齐全时只列真实支持语言。验收：重开后偏好一致、深浅/高对比资源可用、125%/150%/200% DPI 与键盘导航；Core 停止也可用。

已交付：`DesktopPreferences`/`DesktopPreferencesStore`（`desktop.preferences.json`，含语言）、`DesktopLanguages`（只列真实随构建提供的语言，本机为简体中文，并显式说明“新增语言需先补资源”与“重开 Desktop 生效”）、`DesktopProductInfo`（版本取自程序集元数据，帮助入口沿用 Web 头部的 GitHub 链接并标示为外部链接）。未做：多语言资源包、DPI/高对比人工走查。

### DS-02 — LLM 服务商与模型（P0；依赖 DS-00）

状态：**已完成（2026-09-27）**。五张卡（服务商连接、并发与速率、模型定义、上下文与计费、限额与用量）全部接入。

复用原生配置已有局部保存，替换 providers/model-definition 占位；依次补服务商并发、TPM/RPM、协议、能力、上下文与价格。服务商/模型嵌套选择和删除关联影响要明确。最后单独处理配额：先补 Core 配额状态与持久化并独立测试，再启用配额表单。验收：Keep/Replace/Clear 密钥、切换 Provider 的模型校验、保存一个字段后价格/其他模型不丢失、错误保留草稿；空配额端点不能导致“已保存”。

已交付：
- Core：`UpsertLlmProviderRequest.ClearApiKey`，`UpdateProviderAsync`/`UpsertProviderWithModelsAsync` 明确区分保持/替换/清除（替换时清除遗留 `ApiKeyRef`）；`CreateModelAsync`/`UpdateModelAsync` 落实“一个服务商最多一个默认模型”，与既有 `MergeModels` 语义一致。`LlmProviderFileServiceTests` 覆盖。
- 边界：`ILlmResourceSettings` + `LlmProviderEdit`/`LlmModelEdit`/`ApiKeyChange` + `LlmSettingsText`（纯表单助手）在 Foundation；`DesktopLlmResourceSettings` 在 Composition，经 `IDesktopKernel.RunSettingsAsync` 调用 `LlmProviderFileService`，无 HTTP/逐接口转发。
- 原生设置：`models/providers`（服务商连接 + 并发与速率）与 `models/models`（模型定义 + 上下文与计费）两个页签改为原生表单；`LlmProvidersSettings`/`LlmModelsSettings`。删除动作有确认对话框并说明关联影响；Core 未就绪时表单禁用、显示真实原因，绝不假成功。

已登记缺口：
- 模型 `description`：`PuddingLlmModelConfig` 没有该字段，本页不提供模型描述输入（不造假控件）。

配额实现（本轮补完）：
- Core：`PuddingLlmProviderConfig.Quota`（`dailyTokenLimit`/`monthlyTokenLimit`/窗口起点/`updatedAt`）写入 `llm.providers.json`；新增 `LlmProviderQuotaService` 负责限额校验与用量推导，`LlmProviderApiController` 的 GET/PUT/reset-daily 不再是 `NoContent()`，provider 详情的 `quota` 也不再是硬编码零值。
- 口径：**限额来自配置文件，已用 token 从 token 账本实时推导**（`llm_gateway_usage_events` + `TokenUsageEvents`，与统计口径同源），因此 `reset-daily` 只把日窗口起点推进到“现在”，不删除或改写任何账本数据，也不影响自然月计数；自然日/自然月切换照常生效（`WindowStart` = max(自然周期起点, 重置时间)，且不超过“现在”）。
- 独立测试：`LlmProviderQuotaServiceTests` 覆盖限额持久化与校验、用量按 provider 归因、重置窗口语义与账本不变、窗口起点钳制。

### DS-03 — 语音服务商、TTS 与 ASR（P1；依赖 DS-00、DS-02 表单经验）

状态：**已完成（2026-09-27）**。三张卡（voice-providers / tts / asr）分三次可验接入，Core 侧同时补齐了密钥与默认项语义。

三张卡分三次可验提交。选项按当前 VoiceProviderFileService 的文件结构和校验处理；多值音色/语言/格式/采样率用原生列表编辑。验收：TTS 与 ASR 默认项互不覆盖；新增/编辑/删除持久化，秘密不回显；数组与能力开关来回保存不丢失。

已交付：
- Core：`UpsertVoiceProviderRequest.ClearApiKey`（保持/替换/清除，且拒绝同时替换与清除）；provider 与模型的字段/采样率校验；**默认项真源同步**——运行时只读根上的 `Default{Tts,Asr}{Provider,Model}Id`，因此 `Create/Update/Delete` 模型与删除 Provider 都会维护根指针，并保证每个列表最多一个默认项，且 TTS 与 ASR 互不影响。
- 数据修复：`default-data/config/voice/providers.json` 的 `defaultAsrModelId` 原本指向不存在的 `qwen3-asr-flash`（真实模型为 `qwen3-asr-flash-realtime`），且 dashscope 与 xunfei 的 ASR 模型同时标记 `isDefault`；已按运行时真源修正。
- 边界：`IVoiceResourceSettings` + `Voice*` 编辑记录 + `VoiceSettingsText`/`VoiceDefaults` 在 Foundation；`DesktopVoiceResourceSettings` 在 Composition，经 `IDesktopKernel.RunSettingsAsync` 调用 `VoiceProviderFileService`。
- 原生设置：`voice/providers`、`voice/tts`、`voice/asr` 三个页签改为原生表单；多值音色/格式/语言/采样率用逗号分隔的原生列表编辑，未提交数组沿用现值、空数组才是清空；TTS/ASR 页签显示运行时真实默认项。

已登记缺口：Core 的语音模型配置没有 `description` 字段，模型卡片按原字段矩阵不含该列；无。

### DS-04 — 模板与角色实例完整设置（P0；依赖 DS-02、DS-06、DS-07 目录）

状态：**已完成（2026-09-27）**。六页签全部交付：目录与基础 / 角色与 Prompt / 模型与记忆 / Smart 子代理 / 执行护栏 / 能力与 Skill（`agent-grants`，依赖 DS-06、DS-07 的工具与 Skill 目录，二者完成后补齐）。

按六页签拆任务：目录/基础（含预设导入）→ 文档 → 模型/记忆 → Smart → 能力/Skill → 护栏。模板与实例保存目标必须可见；已有基础编辑不可回退。Markdown 编辑要保留换行与未修改内容；七种 Smart 子代理路由全部覆盖。验收：模板变更不误写实例覆盖，实例编辑不改全局模板；模型联动有效；授权继承清晰；局部编辑不清空其他 JSON/Markdown；冻结目标与选中目标一致。

**已交付（目录/基础切片）**
- Core 侧无需改动：`AgentTemplateFileService` / `WorkspaceAgentFileService` 已具备模板 CRUD、预设列表与导入、实例 CRUD 与冻结。
- 边界：`IAgentDirectorySettings` + `AgentTemplateEdit`/`AgentInstanceCreate`/`AgentInstanceEdit`/`AgentAvatarOption` 在 Foundation；`DesktopAgentDirectorySettings` 在 Composition，经 `IDesktopKernel.RunSettingsAsync` 调用 Core。模板与实例的「基础信息」保存都先读回存储记录再整体提交，因此 Prompt/Markdown 与未展示的策略字段不会被清空。
- 原生设置：`agents/directory` 页签原生表单——工作区选择、全局模板列表/新建/删除/基础信息编辑、随产品预设列表与导入、角色实例列表/从模板新建/基础信息编辑/删除/冻结解冻；冻结与停用分开显示（`DescribeState`）。
- 头像改为真实目录项：模板与实例的 `avatarId` 由 `IAgentAvatarCatalog.List()` 提供下拉，**不再提供 `avatarEmoji` 输入框**——`AgentTemplateFileService` 的 DTO 里 `avatarEmoji` 恒为 null，模板头像实际是目录里的图片项，做成自由文本就是一个不生效的控件。

**文档切片已交付**
- 模板文档（`systemPrompt` / `userPromptTemplate` / `personaPrompt` / `agentsPrompt` / `toolsDescription` / `bootstrapTemplate` / `memoryPrompt`）与实例文档（SOUL / AGENTS / TOOLS / BOOTSTRAP / MEMORY / HEARTBEAT）按文档分别编辑保存；切换文档不会丢失未保存内容。
- 并发保护：模板保存前比对读取时的文档集指纹（改动即要求重新读取），实例保存直接使用 Core 的 SHA-256 版本令牌；冲突一律阻止覆盖并提示重新读取。
- 「区分模板默认值与实例覆盖值」：实例页签显示「与模板默认值一致（未覆盖）／实例已覆盖模板默认值」，并可查看来源模板字段的原文；模板保存绝不写入任何实例。
- 真实行为登记：模板没有对应默认内容时，新建实例的 manifest 不引用该文档，`ReadDocumentAsync` 会拒绝读取（`EnsureCanonicalManifestReference`）。页面不整页失败，而是标注「保存会创建文件并修复引用」，保存后引用即被 Core 修复（Composition 测试覆盖）。

**模型/记忆切片已交付**
- 模板与实例各自的「默认对话模型 / 记忆模型 / Embedding 模型」三对 provider-model 选择，外加 `memorySearchMode` 与 `reasoningEffort`；来源模板与实例的保存目标在页面上分开呈现。
- 目录校验：模型必须存在于所选服务商、服务商必须启用、模型不得废弃；对话/记忆只接受非 embedding 模型，Embedding 项只接受 embedding 模型；服务商与模型必须同时指定或同时留空（边界也拒绝半填对，Core 侧同样如此）。
- 不抹除未知值：`memorySearchMode` 只列 off/instant/deep，但存储中的其他值会作为「现有值」出现在下拉里并可原样保存；`reasoningEffort` 是自由文本（取值由服务商约定，例如 low/medium/high/max），不构造本地枚举。
- 真实行为登记：新建实例在创建时会继承模板的模型默认值（`CreateAgentAsync` 的 `?? template?.X`），因此在实例页看到的是继承值而不是空值；保存实例策略不会回写模板（Composition 测试断言）。
- 同一切片做了小重构：模板 upsert 统一由 `TemplateRequest` 构造，基础信息、文档、模型策略三处共用，避免三份长参数列表漂移。

**Smart 子代理切片已交付**
- 七个子代理角色（explorer/researcher/planner/reviewer/developer/deployer/tester）各自的 `{providerId}/{modelId}` 路由；只在角色实例侧提供（全局模板没有 Smart 字段，与 Web 一致）。
- 与 Core 同规则：半填的服务商/模型对在边界被拒绝，而不是被 `FromChoice` 静默写成「未路由」；格式错误（缺少 `/`、`/` 后为空）在表单与边界双重拦截。
- 真实行为登记：`UpdateAgentProfileAsync` 会把 manifest 中已存的 Smart 字段**强制写回**（Core 有意保护基础资料编辑不清路由），因此原生 Smart 保存必须走 `UpdateAgentAsync`；Composition 测试同时断言「基础资料保存后路由仍在」。
- 小重构：实例 profile upsert 统一由 `InstanceRequest` 构造，基础信息、模型策略、Smart 三处共用。

**执行护栏切片已交付**
- 模板与实例各自的 `maxRounds` / `maxElapsedSeconds` / `maxToolCallsTotal` / `containerImage`；实例保存走 `UpdateAgentProfileAsync`，因此 Core 会保留 Smart 路由（Composition 测试同时断言路由未被清掉）。
- 不发明上限：Core 对这些值只做 `??` 继承、不额外钳制，因此表单只拒绝任何预算都无法成立的取值（≤ 0、镜像含空白或超长），百万级预算仍然允许。
- 空容器镜像 = 明确清除覆盖（保存为空串），不会回退成模板值；测试固定这一点。
- 发现并纠正 Core 内部不一致：`WorkspaceAgentDto` 上 `MaxToolCallsTotal` 的默认字面量是 100，而 `AgentTemplateFileService`（新建模板）与 `WorkspaceAgentFileService`（新建实例回退链）实际写入 400。契约按文件服务的真实默认值 400 对齐；未改动 Core，只登记差异。

**本切片登记的缺口**
- 模板 ID / 名称等格式校验目前只在表单层（`AgentDirectoryText.Validate`）；`AgentTemplateFileService` 不拒绝非法 ID，尚未下沉到 Core。
- 角色实例没有 `sortOrder` 字段（`WorkspaceAgentDto`/`UpdateWorkspaceAgentRequest` 都没有），因此实例卡片不提供排序输入，排序只在模板上编辑。
- 模板文档的并发保护是「读取指纹」而非 Core 侧版本号：模板契约没有版本字段，指纹由 Desktop 侧计算（已用测试固定顺序无关/空白敏感）。

### DS-05 — 工作区与渠道（P1；依赖 DS-00、DS-04）

状态：**已完成（2026-09-27）**。七张卡全部接入：工作区、工作区成员、渠道服务商、渠道绑定与凭据、知识库、工作区技能配置、工作流定义。

**资源切片（桌面端已交付）**
- `workspaces/resources` 一张页签三张卡：知识库（kbType 沿用 Core 的 VectorStore/Graph/FileIndex，文档数只读）、工作区技能（MCP configJson 交给 Core 解析）、工作流（definitionJson 可留空）。
- 空资源 ID = 新建，非空 = 就地更新；三张卡各自带确认删除。
- 界面只做 JSON 形状预检；MCP 规范化、工作区隔离与文档统计仍由 Core 负责，跨工作区 ID 会被拒绝（集成测试固定该行为）。
- 集成测试覆盖真实 Host：建库/改库、跨工作区写入被拒、非法 MCP 配置被拒、空定义可保存、删除后清空。

**资源切片（Core 部分已交付）**
- 三个直接使用 `DbContext` 的 Controller（知识库、工作区技能、工作流）合并下沉为 `WorkspaceResourceService`；控制器只做语义结果 → HTTP 状态码映射。
- 核心不变量：**工作区隔离**——跨工作区的 kbId/skillId/workflowId 一律 NotFound（有测试固定），列表也按 `WorkspaceEntityId` 过滤。
- MCP 技能的 `configJson` 沿用 Core 自己的 `McpServerConfig` 解析与规范化，非法配置直接 BadRequest；非 MCP 类型保持原样。MCP 变更后按需刷新连接管理器，且管理器允许缺省（测试用无管理器的组合验证）。
- 工作流 `definitionJson` 允许为空（Core 原样存储），非空时必须是合法 JSON。
- `WorkspaceSkillApiController` 的 `runtime-status` 仍直读 MCP 连接管理器：它不是工作区 CRUD，未下沉。
- 验证：新增 5 项独立测试，并跑通**整个 PuddingPlatformTests 套件 1420 项**确认 Controller 改写未改变 Web 行为。

**渠道切片（已完成）**
- 服务商：Core 内置定义（当前仅飞书），桌面端只能改名/描述/启用状态，不提供新增或删除服务商。
- 渠道：新建/编辑/删除、绑定 Agent（必须属于当前工作区）、App ID、**只写 App Secret**、流式回复、语音回复与音色、特权用户 Open ID、启用状态。
- 密钥语义以 Core 实现为准：留空 = 保持已保存的密钥（适配器发 null）；Core **没有**「清除密钥」操作，因此界面不提供该选项，新建渠道在没有已保存密钥时必须填写。返回模型只有 `HasAppSecret` 布尔值，密钥从不回到界面。
- 音色是自由文本（Core 不校验取值），界面给出「留空时 Core 使用 Cherry」的提示而不编造白名单。
- 集成测试：停用服务商被拒、绑定不存在的 Agent 被拒（Core 报 KeyNotFound）、留空编辑保留密钥、重复飞书 App ID 被拒、删除后清单清空。
- 错误语义登记：停用服务商是 `InvalidOperationException`，服务商不存在与绑定 Agent 不存在是 `KeyNotFoundException`，未实现类型是 `NotSupportedException`；界面分别映射为不同提示。

**工作区/成员切片（已完成）**
- 桌面端：工作区列表/详情、新建、编辑（名称/描述/用户档案/团队与公司访问策略/启用状态）、冻结与解冻、确认删除；成员列表与增删、成员权限。
- 内置默认工作区：界面上没有删除入口也不能被停用，Core 侧同样拒绝（双重防线，各有测试）。
- `UserProfile` 必须是合法 JSON 才允许保存，避免写入不可读内容；团队归属不可在原位更改（与 Core 契约一致）。
- 集成测试覆盖真实 Host 的建/改/冻结/删除，以及跨工作区成员操作与不存在对象都会被拒绝；适配器不吞 Core 的错误。

**工作区/成员切片（Core 部分已交付）**
- `WorkspaceApiController` 原先直接使用 `DbContext`，按本任务书「直接 DbContext 的旧 Controller 先下沉业务操作」下沉为 `WorkspaceService`（`PuddingPlatform.Services`），控制器只做语义结果 → HTTP 状态码映射。
- 规则固定进独立测试：默认工作区不可删除；Team 必须存在；访问策略必须是已定义枚举值（`"99"` 也拒绝）；用户必须存在；同一工作区成员不重复；**跨工作区删除成员返回 NotFound，绝不错删**；更新一个工作区不影响另一个。
- 踩坑登记：`EnsureCreatedAsync` 会同时应用模型种子数据，测试种子必须幂等；`AppUsers.Email` 有唯一索引。
- 待接：桌面端 `workspaces/basic`（工作区列表/新建/编辑/删除/冻结 + 成员增删与权限）。

工作区基础/成员、知识库、工作区技能、工作流、渠道服务商、渠道实例分别提交。直接 DbContext 的旧 Controller 先下沉业务操作。飞书配置保留 appId/appSecret、特权用户、Agent 绑定、流式回复、TTS 开关和音色；只显示真实支持的服务商字段。验收：工作区隔离、成员权限、不存在/删除中的角色绑定错误、密钥保持/替换，Core 重开后数据一致；客户端初始化不导入 Web Bootstrap 登录流程。

### DS-06 — 工具与插件（P1；依赖 DS-00）

状态：**已完成（2026-09-27）**。`tool-registry` 与 `plugin-catalog` 两张卡以只读形式接入；能力目录可供 DS-04 的授权页签使用。

已交付：`IToolPluginSettings`（Foundation）+ `DesktopToolPluginSettings`（Composition，读 `IPuddingToolCatalogService` / `PluginManifestCatalog` / `PluginDiagnosticsReader`）+ `MainWindow.ToolPluginSettings.cs`（`tools/registry`：搜索、分类/来源/状态、参数 Schema；`tools/plugins`：包 ID/版本/状态/校验原因/清单路径、声明工具、最近诊断）。可执行性只认运行时 `Available`，`ManifestOnly` 与未知状态一律不可执行；Composition 测试写入真实 manifest-only 与无效 `plugin.json` 固件，验证包状态、声明工具计数与校验原因。按任务书「不发明安装/执行动作」，本切片不提供安装、启用、卸载或执行入口。

先只读工具注册表与插件包，再实现 Web 真实存在的管理动作。核查 name/ID、Schema、加载状态、来源与错误；能力目录给 DS-04 使用。验收：无效 Manifest 可诊断，ManifestOnly 不能被标成可执行；搜索/空状态/刷新不挂 UI，不发明安装/执行动作。

### DS-07 — Skill Hub 六页签（P1；依赖 DS-00）

状态：**已完成（2026-09-27）**。六个页签全部接入：概览、事件审计、技能库、EVO MAP、安装台账、旧技能包。

**授权页签（DS-04 `agent-grants`）已随 DS-06/DS-07 解锁并接入**
- `agents/capabilities`：模板授权（能力 + 技能包）支持搜索/添加/移除/保存；实例授权显示与模板的偏差。
- 语义边界：模板是新实例的继承来源；实例在创建后是独立快照。页面提供「采用模板授权 / 明确不授权 / 保持实例当前值」三种显式动作，并明确写出「保持当前值不会重新继承模板」。
- 「空列表 = 明确不授权」与「未指定 = 保持当前值」在契约、适配器与界面三层都分开表达（`AgentGrantSelection.Unspecified`），避免把空选择误作删除全部授权。
- 集成测试覆盖真实链路：建模板 → 存授权 → 建实例（继承）→ 明确不授权（偏差可见）→ 保持当前值（不变）→ 采用模板（重新一致）。

**旧技能包切片（桌面端已交付）**
- `skills/legacy`：台账列表与详情、元数据编辑（名称/描述/启用/排序，不动文件与版本）、确认删除（同时尝试删除对象）、上传新包、上传新版本（替换文件）、预签名下载链接（默认浏览器打开）。
- 文件选择不可用时（选择器异常）回退到路径输入框，并把原因显示出来，不静默失败。
- 上传/下载依赖对象存储：Core 未配置或不可达时显示真实错误；适配器还会在校验前先打开文件，路径错误不会产生任何副作用。
- 差异登记：卡片写「zip/tar.gz/tgz」，Core 只接受 `.zip` 与 `.tar.gz`，桌面端不放宽（有测试固定该行为）。

**旧技能包切片（Core 部分已交付）**
- 按 §3.1 先下沉：`SkillPackageApiController` 中的校验（id `^[a-z0-9\-]+$`、名称非空、扩展名 `.zip`/`.tar.gz`）、重复 id 判定（冲突而非覆盖）、对象键构造与文件名净化、换版本时「先上传新对象再删除旧对象」（删失败只记日志，不丢上传）全部下沉为 `SkillPackageService`；控制器只做 `SkillHubResult` → HTTP 状态码映射，Web 行为不变。
- 可测性：引入对象存储端口 `ISkillPackageObjectStore`（`MinioStorageService` 实现），`SkillPackageServiceTests` 用假存储 + 内存 SQLite 覆盖 5 组规则，包括「被拒绝的上传不得触碰对象存储」与「换版本被拒时保持原文件与对象」。
- 登记差异：卡片写的是「zip/tar.gz/tgz 文件」，而 Core 只接受 `.zip` 与 `.tar.gz`；桌面端不擅自放宽，`.tgz` 仍会被拒绝（如需支持应作为独立需求改 Core）。
- 待接：桌面端 `skills/legacy` 表单（列表/创建/编辑元数据/删除/上传新版本/下载链接）。上传依赖 MinIO 配置，未配置时必须显示 Core 的真实错误而不是假成功。

**EVO MAP / 安装台账切片已交付**
- `skills/evolution`：单技能谱系与全局谱系（含节点上限），缩进版本树 + 边列表，节点身份沿用 Core 的 `{skillId}@{version}`。
- 谱系渲染不掩盖异常形态：纯环组件（没有可达根）会被显式列为「无根组件」而不是渲染成空页面；环处标记后停止展开；父节点缺失与悬空边都在摘要里计数。
- `skills/installs`：按 Agent/技能/条数的台账查询与详情（含内容哈希与上报者），以及按 Agent 的更新检查；台账再次强调「只是上报记录」。
- 更新落后判定只看版本字符串是否相同，不去解释 `v1` 或 `release-2` 这类非语义版本，也不猜先后。
- 集成测试：HTTP 发布 create + patch 两版后读回谱系（根/父/边/缩进树/全局谱系）、台账筛选，以及「已登记 1.0.0 → 最新 1.1.0 = 落后」。

**技能库切片已交付**
- 检索（搜索/标签/状态）、技能详情、版本列表与版本全文（Skill Markdown 只读展示）、元数据编辑、软退役（确认对话框）、发布新版本（进化动作白名单 + 非 create 必填父版本）、安装登记。
- 表单校验对齐 Core 真实词表：`SkillId ^[a-z0-9][a-z0-9-]{1,127}$`（不含点号）、进化动作 `create|patch|split|compress|retire|merge|fork`、状态 `active|deprecated|retired`、可见性 `global|workspace`；多条错误一次报全。
- Core 的 `SkillHubResult` 语义失败被转成真实异常，不再出现「HTTP 成功但其实被拒绝」。
- 安装台账按语义呈现：页面明确写出「台账只记录 Agent 上报的版本，不代表已安装或正在运行」。
- 集成测试覆盖：HTTP 发布 → 详情/版本全文 → 改元数据（版本与 Markdown 不变）→ 发布 patch 版本（父版本血缘）→ 重复版本被拒 → 登记安装 → 软退役（版本保留、状态筛选可见）。

**概览/事件切片已交付**
- `ISkillHubSettings`（Foundation）+ `DesktopSkillHubSettings`（Composition，按操作解析 scoped 的 `ISkillHubService`）+ `MainWindow.SkillHubSettings.cs`（`skills/overview`：技能/版本/安装/覆盖 Agent/已进化计数、进化动作分布、安装排行；`skills/events`：技能与条数筛选、事件列表与详情含 PayloadJson）。
- 显示规则：退役技能一律标为不可用；事件类型与来源按 Core 原始值显示，未知类型原样展示而不是猜一个名字。
- 集成测试用产品自身 HTTP 面发布技能与补丁版本后读回（概览计数、`patch` 动作分布、技能摘要、审计事件）。
- 顺带修复：同一 `InfoBar` 被挂到两个页签面板会让窗口在启动时抛 COMException；每个页签现在各有自己的通知条（工具/插件页签也一并修正）。
- 顺带记录 Core 侧约束：SkillId 只允许 `^[a-z0-9][a-z0-9-]{1,127}$`（不含点号）；`evolutionAction` 白名单为 `create|patch|split|compress|retire|merge|fork`。后续 `library` 切片必须按这套词表做表单校验。

依次完成概览、技能详情/版本、发布与元数据、EVO MAP、安装台账/更新、事件审计、旧文件包。旧包 zip/tar.gz/tgz 上传、下载、版本更新与 Hub 发布区分。原生文件选择后将流交给 Core，UI 不解压写运行目录。验收：版本/父版本/进化动作/内容哈希传递正确，退役可观测，台账不是实际安装成功的替身，包路径校验不回退。

### DS-08 — 记忆资料库

状态：**已完成（2026-09-27）**。四张卡全部接入：页面树、Book 与章节、记忆搜索、检查器。

**搜索与检查器切片已交付**
- `memory/search`：全文搜索（条数可选），结果与分数按 Core 返回原样显示——界面不重排序、不自己算相关度（Core 的 FTS 分数可能就是 0，照实显示）。
- 检查器：章节元数据（Book 标题/状态、章节类型/重要度/更新时间）、来源引用（ownerType/ownerId）、图谱指针（sourceType/sourceId，出边与反链分开计数）。
- **保留 Core 的两套键**：sources 与 pointers 用不同的键对，界面不会把它们混成一套；选中搜索结果时会同时填好两套键。
- 「在资料库中打开」= 把资料库页签切到命中的 Book；若该 Book 不在当前资料库页面树中会明确说明而不是静默失败。
- 踩坑登记：搜索结果 DTO 不含章节标题（只有 BookTitle 与 Snippet），因此检查器必须再读一次 Book 才能显示章节元数据。

**资料库切片已交付**
- 工作区 / Agent / 资料库三级选择，含「确保默认资料库」；页面树按 Core 返回的层级只读渲染并统计节点与 Book 数量。
- 节点创建（父节点/类型/名称/摘要）；Book 打开、编辑标题与摘要、按当前标题新建、归档；章节分页显示、编辑标题/内容/重要度、新建、归档。
- 作用域：所有调用都带 工作区+Agent；跨 Agent 访问章节由 Core 以 `UnauthorizedAccessException` 拒绝，界面单独映射为「Core 拒绝访问该资料库」。
- 归档按 Core 语义呈现为「标记已归档、内容保留」，确认框明确写出「归档不是删除」；重要度限定 0~1，留空按 0（未标注）处理。
- **发现并修复 Core 缺陷**：`MemoryLibraryAdminService.UpdateChapterCoreAsync` 只把 `req.Title` 放进返回 DTO，从未调用 `IMemoryLibrary.UpdateChapterTitleAsync`，因此「保存章节标题」在界面上看似成功、库里却仍是旧标题。已改为真实持久化标题并返回存储值，端到端断言覆盖。（P1；依赖 DS-04）

按角色选择图书馆，迁移页面树、Book、章节、检索结果和来源检查器。树/正文/右侧详情可放独立原生工作页，设置保留入口与筛选。验收：同名 Book 不混合身份；搜索跳转正确；归档需要确认；跨角色切换取消旧请求；编辑长文本不丢字；importance 范围与旧契约一致。

### DS-09 — 存储总览、清理与策略（P1；依赖 DS-00）

先快照/分类/历史趋势，再预览/确认/作业事件，最后保留策略。64 张卡片是交付粒度，Web 中分类饼图、7/30/90 天趋势、统计报表、可清理项、受保护项、作业列表等子卡均需覆盖，不得只做一个总数。验收：读取总览不触发磁盘扫描；refreshId 状态正确；取消预览零删除；受保护数据不可选；作业失败/取消/超预算可见；版本冲突不覆盖。只用隔离测试 DataRoot。

### DS-10 — 密钥、审批与审计（P0/P1；依赖 DS-00）

密钥列表/元数据/轮换/删除为第一切片；规则 CRUD/禁用为第二切片；事件统计与分类器健康为第三切片。精确 command/argumentsJson 与 workspaceId 原样保留；不可用 UI 模糊匹配代替 Core 审批规则。验收：列表不含 secret，轮换取消无写入，禁用后实际规则不再生效，审计来源/人/工单完整，分类器不可用明确展示。

### DS-11 — 外部 AccessToken（P1；依赖 DS-00 的本机管理身份结论）

先运行策略与元数据只读，再创建/一次性秘密显示/重命名/撤销。八类 scope：tasks.read/write/comment/evaluate/command、workspaces.read、agents.read、messages.send；Workspace 清单至少一项。验收：过期上限/Active 上限、版本冲突、秘密二次读取不可得、撤销不可恢复、名称修改不扩大授权。不得新造“查看令牌明文”或“硬删除”按钮。

### DS-12 — 用户、团队与 RBAC（P2；依赖 DS-00、DS-05）

优先下沉 Controller 中账号/密码/角色/成员操作并保持现有验证。三页签区分 Agent 角色和权限角色；团队工作区的 teamAccessPolicy/companyAccessPolicy 与直接成员 accessLevel 不混合。验收：密码只写；重复 ID 和非法权限失败可见；角色分配/删除约束与现有行为一致；远程账号管理不会让本机聊天出现登录要求。

### DS-13 — 运行节点（P1；依赖 DS-00）

迁移节点汇总、在线/离线/降级、详情、能力、嵌入式冻结/解冻和原因。现有本机生命周期直接用运行中心，不另外实现第二份状态机。验收：过期心跳不会显示在线，冻结只影响目标节点；断线错误可重试；关闭页停止轮询；真实启动/退出仍需进程外验收。

### DS-14 — 会话与诊断（P1；依赖 DS-04、DS-00）

会话表格/时间线、诊断概览、事件时间线、子代理 Run 四页签分别交付。补 Web Agent 选项空列表问题。精确保留过滤器、列、分页和错误详情，数据来自 canonical 事件/Run 投影。验收：相同 subSessionId 的多次 Run 分开；快速切选择不串数据；大列表分页/取消；内核不可用时保留错误原因，不显示假“就绪”。

### DS-15 — 用量与管理概览（P2；依赖 DS-00、DS-02）

迁移 Token/费用/缓存命中率/计量请求四个汇总、消耗构成、按日/月趋势、模型表、上下文层表及原筛选；不把 api.ts 未被该页面使用的服务函数算作必做 UI。首页四类摘要与四个快捷导航分别处理，Core 状态读真实快照。验收：未知 Provider/模型保留、无数据不等于零费用、加权命中率正确、RMB/1M token 单位不变、各账本不重计。

### DS-16 — 任务调度与看板入口（P1/P2；依赖 DS-04、DS-00）

P1 先调度策略/扫描状态/决策原因，P2 再独立原生看板/列表、详情、编辑、评论、评价、事件、执行链接及命令。遵循任务版本/ETag、终态约束和执行语义；同一任务不重复派发。验收：Assigned/Delivery ACK 不冒充执行成功；候选/窗口/Availability/Execution 都可追踪；冲突重新读取；取消/恢复/重排/归档走现有 Core 命令。

### DS-17 — 编排管理及独立原生编辑器（P2；依赖 DS-00、DS-04、DS-06）

先图目录、修订、Graph Inputs、Hook 管理，再节点/边/布局编辑、组件设置、手动运行与运行控制。沿用 schema、revision 和 authoring 校验；画布不硬塞进设置卡片。验收：未发布草稿不变成活动修订；输入绑定/必填校验/非法边阻止发布；Hook 凭据不回显；运行取消经真实 Core；同图不同版本与并行 Run 可区分。

## 7. 全部卡片与字段矩阵

以下由本轮实际目录导出；`pages/` 和 `components/` 路径以 `Source/PuddingPlatformAdmin/src/` 为根，`Source/` 路径以仓库为根。字段合并为同一张卡仅为设置排版，不得漏掉原页签、抽屉、弹窗中的子项。精确控件名、动态表达式与按钮见证据附录。

### 通用 (`general`)

管理桌面偏好与常用入口。

#### 常规 (`preferences`)

- **界面语言** — `language` / DS-01 / 待迁移。将网页语言选择迁移为桌面本地偏好。
  - 选项：当前语言；可用语言；重启或即时生效提示。
  - 来源：`components/GlobalActions/index.tsx`。

- **帮助与使用说明** — `help` / DS-01 / 待迁移。承接网页帮助入口，使用原生入口打开说明。
  - 选项：帮助入口；外部链接明确标识。
  - 来源：`components/RightContent/index.tsx`。

#### 外观与布局 (`appearance`)

- **主题、材质与面板** — `appearance` / DS-00 / 已接入。沿用 Desktop 已实现的外观与布局设置。
  - 选项：跟随系统/浅色/深色；Mica/Mica Alt/Acrylic；角色导航宽度；编码工作区宽度；保存布局。
  - 来源：`Source/PuddingDesktop/MainWindow.xaml`。

### 模型与服务商 (`models`)

配置文本模型、服务商与配额。基础编辑已在聊天入口可用。

#### 服务商 (`providers`)

- **服务商连接** — `providers` / DS-02 / 部分已有。新增、编辑、启停和删除服务商；删除前说明关联模型。
  - 选项：模板；providerId；name；baseUrl；API Key 保持/替换/清除；description；isEnabled。
  - 来源：`pages/llm-resource-pool/index.tsx`。

- **并发与速率** — `provider-limits` / DS-02 / 待迁移。编辑服务商限流；保存时保留未展示参数。
  - 选项：maxConcurrentRequests；tokensPerMinute；requestsPerMinute。
  - 来源：`pages/llm-resource-pool/index.tsx`。

#### 模型目录 (`models`)

- **模型定义** — `model-definition` / DS-02 / 部分已有。模型列表、新增、编辑、删除；按服务商筛选。
  - 选项：modelId；name；protocol(openai/responses/anthropic)；description；capabilityTags；isDefault；isDeprecated；isEmbedding；sortOrder。
  - 来源：`pages/llm-resource-pool/index.tsx`。

- **上下文与计费** — `model-limits` / DS-02 / 待迁移。保留原有单位和范围校验。
  - 选项：maxContextTokens；maxInputTokens；maxOutputTokens；maxConcurrentRequests；inputPricePer1MTokens；outputPricePer1MTokens；cacheHitPricePer1MTokens。
  - 来源：`pages/llm-resource-pool/index.tsx`。

#### 配额 (`quota`)

- **限额与用量** — `quota` / DS-02 / 待迁移。Web 已有表单，但当前 Core 配额端点为空实现；须先补齐业务能力。
  - 选项：dailyTokenLimit；monthlyTokenLimit；今日已用 tokens；本月已用 tokens；配额状态。
  - 来源：`pages/llm-resource-pool/index.tsx`。

### 语音模型 (`voice`)

配置语音服务商、语音合成与识别。

#### 服务商 (`providers`)

- **语音服务商** — `voice-providers` / DS-03 / 待迁移。服务商模板、新增、编辑、启停、删除。
  - 选项：providerId；name；endpoint；apiKey；description；isEnabled。
  - 来源：`pages/voice-models/index.tsx`。

#### 语音合成 (`tts`)

- **TTS 模型** — `tts` / DS-03 / 待迁移。模型管理与默认模型设置。
  - 选项：modelId；name；path；voices；audioFormats；sampleRates；supportsStreaming；supportsInstructions；supportsVoiceCloning；supportsVoiceDesign；isDeprecated；isDefault；sortOrder。
  - 来源：`pages/voice-models/index.tsx`。

#### 语音识别 (`asr`)

- **ASR 模型** — `asr` / DS-03 / 待迁移。识别模型能力与语言配置。
  - 选项：modelId；name；path；languages；sampleRates；supportsEmotion；supportsTimestamps；supportsHotWords；isDeprecated；isDefault；sortOrder。
  - 来源：`pages/voice-models/index.tsx`。

### 角色与模板 (`agents`)

区分全局模板与工作区角色实例，编辑前明确目标。

#### 目录与基础 (`directory`)

- **全局模板与角色目录** — `agent-directory` / DS-04 / 待迁移。分别提供模板/实例列表、新建、编辑、删除及实例冻结/解冻。
  - 选项：工作区选择；模板或实例选择；来源模板；预设列表与导入；启用与冻结状态。
  - 来源：`pages/global-agent-template/index.tsx`。

- **基础信息** — `agent-basic` / DS-04 / 部分已有。复用已有角色编辑，补齐头像与来源；模板 ID 创建后按现有契约处理。
  - 选项：workspaceId；baseGlobalTemplateId/sourceTemplateId；templateId；name；role；description；avatarId/avatarEmoji；isEnabled；sortOrder。
  - 来源：`pages/agent-template-settings/sections/BasicSection.tsx`。

#### 角色与 Prompt (`prompts`)

- **角色定义与文档** — `agent-prompts` / DS-04 / 部分已有。按文档分别编辑、保存；区分模板默认值与实例覆盖值。
  - 选项：systemPrompt；personaPrompt/soulMdContent；agentsPrompt/agentsMdContent；toolsDescription/toolsMdContent；bootstrapTemplate/bootstrapMdContent；memoryPrompt/memoryMdContent；userPromptTemplate；heartbeatPrompt（实例）。
  - 来源：`pages/agent-template-settings/sections/PromptPersonaSection.tsx`。

#### 模型与记忆 (`models`)

- **默认模型与记忆策略** — `agent-models` / DS-04 / 部分已有。依赖服务商目录；服务商变化时校验模型归属。
  - 选项：preferredProviderId；preferredModelId；memoryLlmProviderId；memoryLlmModelId；embeddingProviderId；embeddingModelId；memorySearchMode(off/instant/deep)；reasoningEffort。
  - 来源：`pages/agent-template-settings/sections/ModelMemorySection.tsx`。

#### Smart 子代理 (`smart`)

- **子代理模型路由** — `smart-models` / DS-04 / 待迁移。仅角色实例具有当前 Web 的 Smart 配置入口。
  - 选项：explorerModel；researcherModel；plannerModel；reviewerModel；developerModel；deployerModel；testerModel；每项服务商/模型选择。
  - 来源：`pages/workspace/[id]/SmartRoleModelFields.tsx`。

#### 能力与 Skill (`capabilities`)

- **工具与技能授权** — `agent-grants` / DS-04 / 已接入。显示有效授权及继承来源；不要把空选择误作删除全部授权。
  - 选项：selectedCapabilityIds；selectedSkillPackageIds（模板）；skillPackageIds（实例）；搜索；添加/移除授权；继承/有效授权。
  - 来源：`pages/agent-template-settings/sections/CapabilitySkillSection.tsx`。

#### 执行护栏 (`guardrails`)

- **预算与运行环境** — `guardrails` / DS-04 / 待迁移。原样保留 Core 的校验与运行语义。
  - 选项：maxRounds；maxElapsedSeconds；maxToolCallsTotal；containerImage。
  - 来源：`pages/agent-template-settings/sections/GuardrailSection.tsx`。

### 工作区与渠道 (`workspaces`)

管理项目归属、连接渠道、知识库和成员。

#### 工作区 (`basic`)

- **工作区信息** — `workspace-basic` / DS-05 / 待迁移。列表、创建、编辑、删除；本机首次使用流程保持独立。
  - 选项：workspaceId；name；description；userProfile；isEnabled；团队归属/访问策略。
  - 来源：`pages/workspace/index.tsx`。

- **工作区成员** — `workspace-members` / DS-05 / 待迁移。成员列表、添加/移除和权限展示。
  - 选项：userId；accessLevel(ReadOnly/Write/Manage)；成员来源。
  - 来源：`pages/workspace/[id]/index.tsx`。

#### 渠道 (`channels`)

- **渠道服务商** — `channel-providers` / DS-05 / 待迁移。编辑已支持服务商的展示信息与启用状态。
  - 选项：providerId；name；description；isEnabled。
  - 来源：`pages/workspace/[id]/index.tsx`。

- **渠道绑定与凭据** — `channels` / DS-05 / 待迁移。新增/编辑/删除渠道；敏感值单向输入。
  - 选项：name；providerId；description；boundAgentId；appId；appSecret；privilegedUserOpenIds；streamingRepliesEnabled；ttsRepliesEnabled；ttsVoice；isEnabled。
  - 来源：`pages/workspace/[id]/index.tsx`。

#### 工作区资源 (`resources`)

- **知识库** — `workspace-knowledge` / DS-05 / 待迁移。知识库列表、新增、编辑、删除。
  - 选项：name；kbType(VectorStore/Graph/FileIndex)；description；isEnabled。
  - 来源：`pages/workspace/[id]/index.tsx`。

- **工作区技能配置** — `workspace-skills` / DS-05 / 待迁移。此处是工作区资源配置，不与 Skill Hub 安装台账混同。
  - 选项：name；skillType(MCP/BuiltIn/CustomScript/HttpTool)；description；configJson；isEnabled。
  - 来源：`pages/workspace/[id]/index.tsx`。

- **工作流定义** — `workspace-workflows` / DS-05 / 待迁移。列表、创建、编辑、删除工作流。
  - 选项：name；status(Draft/Active/Paused)；description；definitionJson；isEnabled。
  - 来源：`pages/workspace/[id]/index.tsx`。

### 工具与插件 (`tools`)

查看实际可用工具及插件声明。

#### 工具注册表 (`registry`)

- **工具目录** — `tool-registry` / DS-06 / 待迁移。搜索/筛选、查看工具定义与当前可用状态。
  - 选项：工具名称/ID；分类；来源；说明；参数 Schema；启用/可用状态。
  - 来源：`pages/capability-management/index.tsx`。

#### 插件包 (`plugins`)

- **插件清单与诊断** — `plugin-catalog` / DS-06 / 待迁移。区分已注册工具、仅 Manifest 声明和无效插件。
  - 选项：插件/工具统计；包 ID/版本/来源；工具声明；ManifestOnly；校验问题；详情与刷新。
  - 来源：`pages/capability-management/index.tsx`。

### 技能与进化 (`skills`)

迁移 Skill Hub 的六个页签。

#### 概览 (`overview`)

- **概览** — `skill-overview` / DS-07 / 待迁移。保留 Web 的实际查询、写入和错误语义。
  - 选项：技能总量/版本/安装统计；分布与最近事件。
  - 来源：`pages/skill-management/OverviewTab.tsx`。

#### 技能库 (`library`)

- **技能库** — `skill-library` / DS-07 / 待迁移。保留 Web 的实际查询、写入和错误语义。
  - 选项：搜索/标签/状态筛选；skillId/name/summary/tags；version/skillMarkdown；evolutionAction/parentVersion/publishNote；版本详情；编辑元数据；退役；安装登记。
  - 来源：`pages/skill-management/SkillsTab.tsx`。

#### EVO MAP (`evolution`)

- **EVO MAP** — `skill-evolution` / DS-07 / 待迁移。保留 Web 的实际查询、写入和错误语义。
  - 选项：技能选择；谱系节点/边；版本关系；详情定位。
  - 来源：`pages/skill-management/EvoMapTab.tsx`。

#### 安装台账 (`installs`)

- **安装台账** — `skill-installs` / DS-07 / 待迁移。保留 Web 的实际查询、写入和错误语义。
  - 选项：agentInstanceId；skillId；installedVersion；contentHash；安装台账；更新检查。
  - 来源：`pages/skill-management/InstallsTab.tsx`。

#### 事件审计 (`events`)

- **事件审计** — `skill-events` / DS-07 / 待迁移。保留 Web 的实际查询、写入和错误语义。
  - 选项：技能/事件/时间筛选；分页；事件详情。
  - 来源：`pages/skill-management/EventsTab.tsx`。

#### 技能包（旧） (`legacy`)

- **技能包（旧）** — `skill-legacy` / DS-07 / 待迁移。保留 Web 的实际查询、写入和错误语义。
  - 选项：skillPackageId/name/description/version；sortOrder/isEnabled；zip/tar.gz/tgz 文件；创建/编辑/删除；上传新版本/下载。
  - 来源：`pages/skill-management/LegacySkillPackages.tsx`。

### 记忆资料库 (`memory`)

按工作区和角色浏览、搜索与维护记忆。

#### 资料库 (`library`)

- **图书馆与页面树** — `memory-tree` / DS-08 / 待迁移。工作区 → Agent → 图书馆选择，刷新与默认库初始化。
  - 选项：libraryId；页面树；节点 name/summary/nodeType；父节点；创建 Page/Book。
  - 来源：`pages/memory-library/index.tsx`。

- **书籍与章节** — `memory-books` / DS-08 / 待迁移。阅读、编辑与归档；保留来源关联。
  - 选项：Book title/summary；章节 title/content/importance；章节分页；归档 Book/章节。
  - 来源：`pages/memory-library/index.tsx`。

#### 检索与来源 (`search`)

- **记忆搜索** — `memory-search` / DS-08 / 待迁移。搜索结果定位到真实页面或章节。
  - 选项：搜索词；范围；结果/匹配摘要；定位。
  - 来源：`pages/memory-library/components/MemorySearchResults.tsx`。

- **来源与引用** — `memory-inspector` / DS-08 / 待迁移。原生详情区呈现出处和引用关系。
  - 选项：元数据；sources；pointers；目标定位。
  - 来源：`pages/memory-library/components/MemoryInspector.tsx`。

### 存储与清理 (`storage`)

展示存储估算，交由 Core 执行清理与策略。

#### 空间总览 (`overview`)

- **总览与分类** — `storage-inventory` / DS-09 / 待迁移。所有统计保留估算标记和采样时间。
  - 选项：总占用；分类占比；分类统计；受保护对象；7/30/90 天趋势；刷新。
  - 来源：`pages/storage/index.tsx`。

#### 清理作业 (`cleanup`)

- **预览与提交** — `storage-cleanup` / DS-09 / 待迁移。先预览再确认；原生页面不扫描或删除数据文件。
  - 选项：数据类别；早于天数；预览估算；保护排除；确认提交；作业进度/事件；取消；超预算继续。
  - 来源：`pages/storage/CleanupPreviewModal.tsx`。

#### 自动清理 (`policy`)

- **保留策略** — `storage-policy` / DS-09 / 待迁移。按 target 更新策略，并处理版本冲突。
  - 选项：targetId；enabled；retentionDays；允许范围；策略版本；更新时间。
  - 来源：`pages/storage/StoragePolicyDrawer.tsx`。

### 密钥与审批 (`security`)

保管凭据、管理工具授权并查看审批审计。

#### 密钥保管箱 (`vault`)

- **密钥元数据与轮换** — `keyvault` / DS-10 / 待迁移。列表不返回明文；新增、编辑元数据、轮换、删除需区分。
  - 选项：名称；描述；分类(general/api/token)；标签；新密钥值；引用占位符复制。
  - 来源：`pages/keyvault/index.tsx`。

#### 审批白名单 (`allowlist`)

- **精确授权规则** — `allowlist` / DS-10 / 待迁移。搜索、创建、编辑、禁用；保留精确匹配语义。
  - 选项：toolId；workspaceId；status；command；argumentsJson；source；approvedByAgentInstanceId；approvedByUserId；approvalTicketId；reason；hitCount。
  - 来源：`pages/tool-approval/allowlist/index.tsx`。

#### 审批审计 (`audit`)

- **事件与统计** — `approval-audit` / DS-10 / 待迁移。只读筛选、分页与详情，展示工单及权限事件。
  - 选项：事件类型；工具/工作区/时间筛选；事件明细；提交/批准/拒绝统计。
  - 来源：`pages/tool-approval/audit/index.tsx`。

- **分类器健康** — `classifier-health` / DS-10 / 待迁移。显示可用性、失败原因与健康明细。
  - 选项：分类器状态；版本/就绪；调用/失败；诊断明细。
  - 来源：`pages/tool-approval/components/ClassifierHealthBanner.tsx`。

### 外部访问 (`access`)

管理外部 API 授权，保持远程认证边界。

#### Access Token (`tokens`)

- **令牌管理** — `access-tokens` / DS-11 / 待迁移。列表/详情/创建/重命名/撤销；新令牌仅显示一次。
  - 选项：名称；Workspace 允许清单；Scope 八种现有权限；有效期；Owner；状态；最后使用；版本；撤销原因。
  - 来源：`pages/access-token-management/index.tsx`。

- **API 状态与限制** — `external-api-status` / DS-11 / 待迁移。显示服务端准入状态和创建限制。
  - 选项：API 可用性；最长有效期；每人 Active 上限；创建错误/版本冲突。
  - 来源：`pages/access-token-management/components/SecretOnceModal.tsx`。

### 用户与权限 (`accounts`)

管理 Web/远程用户、团队和 RBAC；本机聊天保持免登录。

#### 用户 (`users`)

- **账号与头像** — `users` / DS-12 / 待迁移。新增/编辑/启停/删除、修改密码、分配角色。
  - 选项：userId；username；email；displayName；userType(Admin/SimpleUser)；isEnabled；avatar；初始/新密码；确认密码；角色集合。
  - 来源：`pages/user-management/index.tsx`。

#### 团队 (`teams`)

- **团队与成员** — `teams` / DS-12 / 待迁移。团队 CRUD、成员管理、团队工作区与访问策略。
  - 选项：teamId；name；description；成员 userId/role；workspaceId/name/description/isEnabled；teamAccessPolicy/companyAccessPolicy；工作区成员 userId/accessLevel。
  - 来源：`pages/team-management/index.tsx`。

#### 权限角色 (`roles`)

- **RBAC 角色** — `rbac` / DS-12 / 待迁移。平台权限角色与智能体角色分别命名。
  - 选项：roleId；name；description；workspace read/write/manage；team read/manage；user read/manage；agent run/manage；template read/manage；llm read/manage。
  - 来源：`pages/role-management/index.tsx`。

### 运行与节点 (`runtime`)

本机内核管理与远程运行节点分开呈现。

#### 本机内核 (`local`)

- **数据目录与生命周期** — `local-kernel` / DS-00 / 已有入口。现有功能位于运行中心；从本页进入。
  - 选项：DataRoot 保存/恢复默认；重开生效；启动/停止/重启；PID/状态；独立诊断路径。
  - 来源：`Source/PuddingDesktop/MainWindow.Kernel.cs`。

#### 运行节点 (`nodes`)

- **节点目录与详情** — `runtime-nodes` / DS-13 / 待迁移。显示嵌入式/独立节点、能力；支持冻结/解冻的节点沿用现有准入。
  - 选项：总数/在线/降级/离线/活跃会话；nodeId；endpoint；host/IP；heartbeat；mode；hostType；capabilities；冻结原因。
  - 来源：`pages/runtime-management/index.tsx`。

### 会话与诊断 (`diagnostics`)

迁移只读运维视图与现有显式操作。

#### 会话 (`sessions`)

- **会话管理** — `session-directory` / DS-14 / 待迁移。查询/筛选会话、详情及页面已有操作。
  - 选项：sessionId；场景；Agent 模板；渠道；类型；状态；用户；创建/最近活跃；表格/时间线；工作区/Agent/状态筛选。
  - 来源：`pages/session/index.tsx`。

#### 诊断概览 (`overview`)

- **运行诊断** — `diagnostics-overview` / DS-14 / 待迁移。汇总状态、失败原因与查询条件。
  - 选项：Agent/会话/Run 筛选；运行状态；诊断指标；刷新/详情。
  - 来源：`pages/diagnostics/DiagnosticsPage.tsx`。

#### 事件时间线 (`timeline`)

- **运行时间线** — `runtime-timeline` / DS-14 / 待迁移。按 canonical 事件显示时间、关联 ID 和详情。
  - 选项：时间窗；事件类型；Agent/Session/Run；分页/游标；事件详情。
  - 来源：`pages/diagnostics/RuntimeTimelinePage.tsx`。

#### 子代理运行 (`subagents`)

- **运行归档与输出** — `subagent-runs` / DS-14 / 待迁移。区分复用的子会话身份和本次 Run。
  - 选项：parentRunId；runId；subSessionId；状态；输入/输出；工具事件；错误与时间。
  - 来源：`pages/diagnostics/SubAgentRunsPage.tsx`。

### 用量与概览 (`usage`)

按现有统计口径展示 Token、费用和首页汇总。

#### Token 统计 (`tokens`)

- **用量与费用** — `token-summary` / DS-15 / 待迁移。继承 Web 日期、服务商、模型等筛选及 RMB/Token 单位。
  - 选项：月份/时间范围；服务商/模型；输入/输出/缓存命中；金额；趋势；月度汇总。
  - 来源：`pages/stats/tokens/index.tsx`。

- **上下文层分析** — `token-details` / DS-15 / 待迁移。迁移当前上下文层分析表；不能将不同账本重复相加。
  - 选项：层；职责；影响；Token 压力；缓存表现；变化；主要原因。
  - 来源：`pages/stats/tokens/index.tsx`。

#### 管理概览 (`dashboard`)

- **管理首页卡片** — `admin-home` / DS-15 / 待迁移。承接首页摘要和快捷入口，不在设置中心复制完整工作台。
  - 选项：工作空间；可用空间；协作团队；Core 状态；开始对话/工作空间/模型服务/系统诊断快捷入口。
  - 来源：`pages/home/index.tsx`。

### 任务与编排 (`automation`)

承接调度策略与编排配置；复杂编辑器后续独立原生页面。

#### 任务调度 (`scheduler`)

- **自动调度策略** — `scheduler-policy` / DS-16 / 待迁移。先只读状态，再接入保存和调度操作。
  - 选项：enabled；eventDrivenEnabled；mode(shadow/authoritative)；scanIntervalSeconds；candidateLimit；maxStartsPerScan；版本冲突。
  - 来源：`pages/workspace-tasks/SchedulerDrawer.tsx`。

- **调度状态与判定** — `scheduler-status` / DS-16 / 待迁移。展示本轮扫描和候选判定，确认已执行而非仅派发。
  - 选项：Idle/Busy/Unknown；候选/可派发；启动/修复；Tracked/Cleanup；最近/下次扫描；决策原因；手动动作。
  - 来源：`pages/workspace-tasks/SchedulerDrawer.tsx`。

#### 任务看板 (`tasks`)

- **任务列表与详情入口** — `tasks` / DS-16 / 待迁移。列表/看板、编辑、评论、评价、事件及执行命令另建原生工作页。
  - 选项：workspaceId；任务内容/负责人/优先级/状态；执行链接；创建/编辑/分派/运行/取消/恢复/重排/归档；版本/ETag。
  - 来源：`pages/workspace-tasks/index.tsx`。

#### 编排配置 (`orchestration`)

- **图定义与版本** — `orchestration` / DS-17 / 待迁移。原生设置提供管理入口；画布与运行详情按独立工作页实施。
  - 选项：图列表/创建/更新；节点组件与设置；边；Graph Inputs；修订/校验/发布；布局；手动运行；Run 控制。
  - 来源：`pages/orchestration/index.tsx`。

- **HTTP Hook** — `http-hooks` / DS-17 / 待迁移。触发器配置与凭据保持服务端授权和版本语义。
  - 选项：Hook 列表/创建/启停；图/修订；输入映射；密钥；调用信息。
  - 来源：`pages/orchestration/HttpHookPanel.tsx`。

### 关于 (`about`)

版本、配置位置与迁移状态。

#### 产品信息 (`product`)

- **Pudding Desktop** — `about` / DS-01 / 待迁移。提供真实构建版本与帮助入口；不添加截图产品专有功能。
  - 选项：产品版本；WinUI 3；进程内 Core；配置位置；开源/帮助信息。
  - 来源：`Source/PuddingDesktop/MainWindow.xaml.cs`。


## 8. 每个任务的完成门禁

1. 所有迁移控件可追到 Web 源文件/字段和 Core 方法；未实现后端能力明确登记，不能返回假成功。
2. 在独立边界跑保存/校验/取消/冲突/敏感字段测试；新组件先通过 S1–S4，再接宿主 S5。不要用仅断言控件存在的测试充当业务验证。
3. 使用隔离 DataRoot 做真实 Core 直接调用集成；只读取本机 UI 的静态目录无需启动 Core。测试中不使用或清空 `D:\data`，不复制真实 LLM 凭据。
4. 有实际运行证据才把 catalog 的 card 状态从“待迁移/部分已有”改为已接入；同步本任务书与 `code_map.md`。一个已验证切片一个 commit，精确暂存，不提交他方 WIP。
5. 构建/测试串行使用 `--artifacts-path temp/build/desktop-kernel`；先 restore，再同目录 `--no-restore`。覆盖率路径用绝对路径落仓库 `temp/test-out`。不要清理其他仍被进程或协作者使用的构建目录。
6. UI 验收：所有分类可达、选择/搜索/返回保持一致、键盘/Esc、125%/150%/200% DPI、深浅/高对比、小窗口不裁切保存/取消按钮；敏感值不出现在诊断输出。
7. 交付报告区分源码/构建测试、隔离窗口 smoke、实际产品部署三层；没有重启目标 Desktop 就不能说生产已生效。

## 9. 本轮验证记录

见 `Docs/Reports/Desktop-Admin-Settings-Skeleton-2026-09-27.md`。本文的业务任务仍待 DeepSeek 实施，不因骨架测试通过而变更状态。

## 10. 实施进度

| 任务 | 状态 | 证据 |
|---|---|---|
| DS-00 接入基线与生命周期 | 已完成 2026-09-27 | [接入说明](../Features/Desktop-Settings-Operation-Boundary-2026-09-27.md)；Foundation 36 项、Composition 2 项（含真实 Host）、窗口 smoke 93 项通过 |
| DS-01 通用、语言与关于 | 已完成 2026-09-27 | `language`/`help`/`about` 三卡原生化；Foundation 47 项、窗口 smoke 104 项通过 |
| DS-02 LLM 服务商与模型 | 已完成 2026-09-27 | 五张卡接入；Core 补齐配额（限额入配置文件、用量来自账本、reset-daily 只推进窗口）；配额测试 4 项、Composition 真实 Host 端到端通过 |
| DS-03 语音服务商、TTS 与 ASR | 已完成 2026-09-27 | 三卡接入；Core 补密钥保持/替换/清除与默认项真源同步（TTS/ASR 互不覆盖）；语音 Core 测试 4 项 |
| DS-04 模板与角色实例 | 已完成 2026-09-27 | 六张卡全部接入（agent-directory / agent-basic / agent-prompts / agent-models / smart-models / guardrails / agent-grants）；授权页签在 DS-06、DS-07 完成后补齐 |
| DS-05 工作区与渠道 | 已完成 2026-09-27 | 七张卡全部接入；知识库/技能/工作流先下沉为 `WorkspaceResourceService`（5 项独立测试 + 全量 1420 项回归）再接桌面端 |
| DS-06 工具与插件 | 已完成 2026-09-27 | 两张只读卡接入；manifest-only 与无效清单有真实固件测试 |
| DS-07 Skill Hub 六页签 | 已完成 2026-09-27 | 六张卡全部接入；旧技能包的校验/对象键构造下沉为可测试的 `SkillPackageService`（含 5 项独立测试），桌面端复用同一操作 |
| DS-08 记忆资料库 | 已完成 2026-09-27 | 四张卡全部接入；顺带修复 Core 章节标题不落盘的缺陷 |
| DS-09 … DS-17 | 待实施 | — |
