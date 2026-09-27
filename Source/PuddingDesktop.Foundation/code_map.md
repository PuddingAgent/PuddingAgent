# Desktop Foundation

独立 BCL 叶组件，包含 Shell 展示状态与可独立测试的内核生命周期协调；不引用 Host、不打开业务数据库。

| 文件 | 职责 |
|---|---|
| `WorkContext.cs` | 角色实例、主会话、文档来源和类型合同 |
| `ShellState.cs` | 角色选择代次、草稿隔离、文档身份/归属与关闭行为 |
| `ShellLayout.cs` | 有界宽度与窄窗口自动折叠，不覆盖用户偏好 |
| `SettingsCatalog.cs` / `SettingsCatalog.json` | 原生设置的 17 分类 / 49 页签 / 64 卡片、字段搜索与迁移任务来源；纯静态目录，无业务调用 |
| （已删除）`SkeletonSettingsStore.cs` | 由 `DesktopPreferences.cs` 取代 |
| `SettingsOperations.cs` | DS-00 接入边界：`ISettingsScope`/`ISettingsOperationHost` 端口、`SettingsOperationGate`（未就绪/停止拒绝、取消排空、内核与选择代次）、`SettingsVersionGuard` 版本冲突；纯 BCL，无 DI/Host 引用 |
| `LocalDesktopIdentity.cs` | 本机单用户身份常量与序号匹配；不来自输入、命令行或 HTTP，也不等于 Web Admin 角色 |
| `DesktopPreferences.cs` | 真实桌面偏好（外观 + 语言）与原子保存；`DesktopLanguages` 只列随构建提供的语言，并声明需重启生效 |
| `DesktopProductInfo.cs` | DS-01 关于卡：版本取自程序集元数据（不写死）、外部帮助入口判定与只读配置位置描述 |
| `LlmSettingsContracts.cs` | DS-02 LLM 设置边界：`ILlmResourceSettings`（任务形状，非逐接口转发）、provider/model/quota 编辑记录、`ApiKeyChange`（Keep 为默认）、`LlmQuotaStatus` 与纯表单助手 `LlmSettingsText` |
| `VoiceSettingsContracts.cs` | DS-03 语音设置边界：`IVoiceResourceSettings`、TTS/ASR 模型记录、`VoiceDefaults`（运行时真源）与纯表单助手 `VoiceSettingsText` |
| `AgentDirectoryContracts.cs` | DS-04 角色目录切片边界：`IAgentDirectorySettings`、模板/实例/预设/头像记录、`AgentDirectoryText`（对象状态区分冻结与停用） |
| `AgentDocumentContracts.cs` | DS-04 文档切片：模板/实例文档槽位、文档集指纹（模板并发保护）、`IsOverride`（实例覆盖判定） |
| `AgentModelPolicyContracts.cs` | DS-04 模型/记忆切片：三对服务商-模型选择、目录校验（停用/废弃/embedding 归属）、检索模式与推理强度保留策略 |
| `AgentSmartRouteContracts.cs` | DS-04 Smart 切片：七个子代理角色槽位、`{providerId}/{modelId}` 解析/格式化/校验（与 Core 的 NormalizeSmartRoleModel 同规则） |
| `AgentGuardrailContracts.cs` | DS-04 护栏切片：执行预算与容器镜像记录、只拒绝任何预算都无法成立的取值（≤0/空白镜像） |
| `ToolPluginContracts.cs` | DS-06 工具/插件读模型：工具目录条目与参数、插件包/声明/诊断记录、`IsExecutable`（只有运行时 Available 才算可执行，未知状态 fail closed） |
| `SkillHubContracts.cs` | DS-07 全部分片：`ISkillHubSettings` 读模型与写入记录、概览统计与审计事件、Core 真实词表（SkillId 正则、进化动作/状态/可见性白名单）、技能库与版本发布、EVO MAP 谱系（`RenderLineage` 显式呈现无根组件与环、`DescribeLineage` 计数悬空边/缺失父节点）、安装台账语义与更新落后判定 |
| `SkillPackageContracts.cs` | DS-07 旧技能包切片：`ISkillPackageSettings`（列表/元数据/删除/上传/替换/下载链接）、`SkillPackageText`（扩展名与 Core 一致、id 与排序校验、字节数显示） |
| `AgentGrantContracts.cs` | DS-04 授权切片：可选授权项（能力/技能包）、`AgentGrantSet`、`AgentGrantSelection`（未指定 ≠ 明确不授权）、模板与实例的快照对比、目录外授权项校验 |
| `StorageContracts.cs` | DS-09 盘点/策略/清理切片：`IStorageSettings`、字节与占比显示、词表（安全级别/估算/刷新/作业状态/事件类型）、保留策略 CAS 与预览表单校验；清理相关的 Foundation 类型刻意改名为 Estimate/Run/Counters 以避开 Core 同名类型 |
| `ChannelContracts.cs` | DS-05 渠道切片：`IStorageSettings`、字节与占比显示（无总量时占比未知而不是 0%）、安全级别与估算状态词表、保留策略 CAS 校验（0 天非法、范围上下限） |
| `WorkspaceResourceContracts.cs` | DS-05 资源切片：`IWorkspaceResourceSettings`（知识库/技能/工作流，空 ID=新建）、Core 的三套取值词表、MCP 配置与工作流定义的 JSON 预检 |
| `MemoryLibraryContracts.cs` | DS-08 资料库切片：`IMemoryLibrarySettings`（工作区+Agent 作用域）、页面树渲染与拍平、Book/章节表单与重要度 0~1 校验、归档≠删除说明 |
| `MemoryLibraryContracts.cs` | DS-08 资料库+搜索切片：`IMemoryLibrarySettings`（工作区+Agent 作用域）、页面树渲染/拍平、Book/章节表单、搜索结果与来源/指针模型（sources 与 pointers 是 Core 的两套键） |
| `ChannelContracts.cs` | DS-05 渠道切片：`IChannelSettings`、服务商与渠道显示、`ChannelSecret`（只写不读；留空=保持，Core 没有清除语义）、Open ID 解析与回复方式描述 |
| `WorkspaceContracts.cs` | DS-05 工作区/成员切片：`IWorkspaceSettings`、状态显示（停用/冻结分开）、Core 的访问策略词表、UserProfile 必须是合法 JSON、内置默认工作区不可停用 |
| `PuddingDesktop.Foundation.csproj | 编译期拒绝任何项目/包引用；输出限于 temp/build/winui3 |

独立测试：`Source/PuddingDesktop.FoundationTests`。组件不负责真实角色注册、执行授权或 Host 装配。`InProcessKernel` 串行化启动/停止、取消与失败恢复，并把设置操作按内核代次拒绝/排空；`IDesktopServices` 提供展示端口；`IKernelSessionFactory` 由 Composition 适配 PuddingHost。157 项独立测试通过。接入方式与本机管理身份结论见 [DS-00 设置接入基线与生命周期](../../Docs/Features/Desktop-Settings-Operation-Boundary-2026-09-27.md)。
