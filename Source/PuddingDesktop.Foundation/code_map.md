# Desktop Foundation

独立 BCL 叶组件，包含 Shell 展示状态与可独立测试的内核生命周期协调；不引用 Host、不打开业务数据库。

| 文件 | 职责 |
|---|---|
| `WorkContext.cs` | 角色实例、主会话、文档来源和类型合同 |
| `ShellState.cs` | 角色选择代次、草稿隔离、文档身份/归属与关闭行为 |
| `ShellLayout.cs` | 有界宽度与窄窗口自动折叠，不覆盖用户偏好 |
| `SettingsCatalog.cs` / `SettingsCatalog.json` | 原生设置的 17 分类 / 49 页签 / 64 卡片、字段搜索与迁移任务来源；纯静态目录，无业务调用 |
| `SkeletonSettingsStore.cs` | 已删除，由 `DesktopPreferences.cs` 取代 |
| `SettingsOperations.cs` | DS-00 接入边界：`ISettingsScope`/`ISettingsOperationHost` 端口、`SettingsOperationGate`（未就绪/停止拒绝、取消排空、内核与选择代次）、`SettingsVersionGuard` 版本冲突；纯 BCL，无 DI/Host 引用 |
| `LocalDesktopIdentity.cs` | 本机单用户身份常量与序号匹配；不来自输入、命令行或 HTTP，也不等于 Web Admin 角色 |
| `DesktopPreferences.cs` | 真实桌面偏好（外观 + 语言）与原子保存；`DesktopLanguages` 只列随构建提供的语言，并声明需重启生效 |
| `DesktopProductInfo.cs` | DS-01 关于卡：版本取自程序集元数据（不写死）、外部帮助入口判定与只读配置位置描述 |
| `LlmSettingsContracts.cs` | DS-02 LLM 设置边界：`ILlmResourceSettings`（任务形状，非逐接口转发）、provider/model/quota 编辑记录、`ApiKeyChange`（Keep 为默认）、`LlmQuotaStatus` 与纯表单助手 `LlmSettingsText` |
| `VoiceSettingsContracts.cs` | DS-03 语音设置边界：`IVoiceResourceSettings`、TTS/ASR 模型记录、`VoiceDefaults`（运行时真源）与纯表单助手 `VoiceSettingsText` |
| `AgentDirectoryContracts.cs` | DS-04 角色目录切片边界：`IAgentDirectorySettings`、模板/实例/预设/头像记录、`AgentDirectoryText`（对象状态区分冻结与停用） |
| `PuddingDesktop.Foundation.csproj` | 编译期拒绝任何项目/包引用；输出限于 temp/build/winui3 |

独立测试：`Source/PuddingDesktop.FoundationTests`。组件不负责真实角色注册、执行授权或 Host 装配。`InProcessKernel` 串行化启动/停止、取消与失败恢复，并把设置操作按内核代次拒绝/排空；`IDesktopServices` 提供展示端口；`IKernelSessionFactory` 由 Composition 适配 PuddingHost。70 项独立测试通过。接入方式与本机管理身份结论见 [DS-00 设置接入基线与生命周期](../../Docs/Features/Desktop-Settings-Operation-Boundary-2026-09-27.md)。
