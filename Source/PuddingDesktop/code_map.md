# PuddingDesktop — WinUI 3 骨架

2026-09-27 按用户裁定在原工程重建。产物仍为 `PuddingDesktop.exe`；旧 WPF 在 `../PuddingDesktop.WpfArchive`，只保留迁移测试基线。

| 文件 | 职责 |
|---|---|
| `App.xaml(.cs)` | Windows 11 控件/主题，独立骨架单实例和激活转发，隔离配置、异常日志、组合入口 |
| `MainWindow.Kernel.cs` | 内核配置、原生 ChatWorkspace 装配、生命周期按钮、退出协调与真实进程 smoke |
| `MainWindow.xaml(.cs)` | 原生菜单、角色导航、草稿、五类文档标签、设置与运行中心、窄窗口折叠 |
| `MainWindow.Settings.cs` | 居中设置层、分类/页签/占位卡片、字段搜索、背景输入隔离、Esc/焦点恢复；DS-00 绑定所选工作区/角色到设置选择代次；DS-01 语言与关于页签改为原生内容；DS-02 决定哪些页签有原生内容 |
| `MainWindow.AgentDirectorySettings.cs` | DS-04 目录切片：全局模板与预设导入、工作区角色实例的新建/基础编辑/删除/冻结解冻，头像走真实头像目录下拉 |
| `MainWindow.AgentDocumentSettings.cs` | DS-04 文档切片：模板文档与实例文档的独立编辑，草稿跨文档切换保留，保存携带指纹/SHA 冲突令牌 |
| `MainWindow.AgentModelSettings.cs` | DS-04 模型/记忆切片：模板与实例的对话/记忆/Embedding 三对模型选择、检索模式、推理强度；目录中已不存在的存储值会被保留并标注 |
| `MainWindow.AgentSmartSettings.cs` | DS-04 Smart 切片：角色实例的七条子代理模型路由；半填选择被拒绝而不是写成「未路由」 |
| `MainWindow.AgentGuardrailSettings.cs` | DS-04 护栏切片：模板与实例的执行预算与容器镜像；空镜像表示明确清除覆盖而不是回退模板 |
| `MainWindow.ToolPluginSettings.cs` | DS-06 只读工具目录与插件清单/诊断：搜索过滤、参数 Schema、插件校验原因与最近诊断；不提供安装/启用/执行动作 |
| `MainWindow.SkillHubSettings.cs` | DS-07 概览/事件切片：Hub 统计、进化动作分布、安装排行与审计事件查询（含事件详情）；本切片只读 |
| `MainWindow.SkillLibrarySettings.cs` | DS-07 技能库切片：检索、技能/版本详情与 Markdown 全文、元数据编辑、软退役（带确认）、发布新版本、安装登记（台账语义提示） |
| `MainWindow.SkillEvolutionSettings.cs` | DS-07 EVO MAP 与安装台账：谱系树/边、台账筛选与详情、按 Agent 的更新检查；只读 |
| `MainWindow.SkillPackageSettings.cs` | DS-07 旧技能包：台账列表与详情、元数据编辑、确认删除、上传新包/上传新版本（文件选择器 + 路径输入）、预签名下载链接；上传失败显示 Core 真实错误 |
| `MainWindow.AgentGrantSettings.cs` | DS-04 授权页签：模板授权搜索/添加/移除/保存；实例授权对比模板并区分「采用模板值 / 明确不授权 / 保持实例当前值」 |
| `MainWindow.WorkspaceSettings.cs` | DS-05 工作区与成员：列表/详情/新建/编辑/冻结解冻/确认删除（内置默认工作区无删除入口）、成员增删与权限 |
| `MainWindow.ChannelSettings.cs` | DS-05 渠道：服务商改名/描述/启用，渠道新建/编辑/删除、绑定 Agent、App ID、只写 App Secret（保持或替换）、流式与语音回复、特权用户 Open ID |
| `MainWindow.WorkspaceResourceSettings.cs` | DS-05 资源三张卡：知识库、工作区技能（MCP configJson 编辑器）、工作流（definitionJson 编辑器）；空 ID 即新建 |
| `MainWindow.MemoryLibrarySettings.cs` | DS-08 资料库切片：工作区/Agent/资料库选择（含确保默认库）、页面树只读渲染、节点创建、Book 打开/编辑/新建/归档、章节分页/编辑/新建/归档 |
| `MainWindow.MemorySearchSettings.cs` | DS-08 搜索与检查器：全文搜索（条数选择、结果与分数按 Core 原样显示）、章节元数据、来源引用与图谱指针（出边/反链分开）、「在资料库中打开」定位 |
| `MainWindow.ApprovalSettings.cs` | DS-10 授权与审批审计：规则增删改（含 effect allow/deny 与来源）、停用而非删除、审计事件筛选与统计；两张卡共用 security/audit 页签 |
| `MainWindow.AdminHomeSettings.cs` | DS-15 管理首页卡片：摘要（Core 状态/工作区/团队/节点/数据占用/磁盘可用）+ 四个导航快捷入口；**不复制工作台**，且每个来源独立失败并写进警告行 |
| `MainWindow.TokenUsageSettings.cs` | DS-15 用量两张卡：Token 汇总（窗口选择、按日汇总、明细展开）与用量事件列表（工作区/会话/服务商/模型筛选 + 分页，**无时间窗**并说明原因） |
| `MainWindow.SubAgentRunSettings.cs` | DS-14 子代理运行：运行列表（父会话/工作区/Agent/状态筛选 + Core 分页）、详情（任务/输出/LLM profile/trace/计数/降级标记）与事件预览 |
| `MainWindow.SessionDirectorySettings.cs` | DS-14 会话目录：工作区/渠道/用户（Core 侧）+ 模板/状态/角色/关键字（页侧）筛选、本地分页、会话详情（类型/角色/状态/主体/血缘） |
| `MainWindow.DiagnosticsSettings.cs` | DS-14 诊断两张卡：运行时间线（会话/Run/Trace/Agent/组件/状态筛选、排序、展示模式、分页、事件详情）与运行诊断概览（组件健康计数 + 近期失败事件） |
| `MainWindow.RuntimeNodeSettings.cs` | DS-13 运行时节点：节点与能力列表、状态/心跳/主机展示、冻结与解冻（必填原因，写入审计）；冻结拒绝该节点全部原生能力调用 |
| `MainWindow.TeamSettings.cs | DS-12 团队：团队增删改、成员与角色、团队下工作区（含访问策略）、工作区白名单（None 不可用）；默认工作区与「有工作区不能删团队」双重防线 |
| `MainWindow.UserSettings.cs` | DS-12 账号：列表与详情、新建（含二次确认密码）、编辑与启停、改密、角色全量替换、确认删除；密码只写不读 |
| `MainWindow.RoleSettings.cs` | DS-12 RBAC：角色列表与权限编辑（9 项 Core 权限可勾选）、系统内置角色禁用保存/删除、权限差异说明卡 |
| `MainWindow.AccessTokenSettings.cs` | DS-11 访问令牌：External API 策略、令牌列表与筛选、创建（明文只显示一次并可复制）、重命名与撤销（带 expectedVersion）；界面无 reveal/unrevoke |
| `MainWindow.SecuritySettings.cs` | DS-10 保管库与分类器健康：密钥元数据增删改（值只写、占位符可复制）、分类器健康快照（未接线时显示未知态） |
| `MainWindow.StorageCleanupSettings.cs` | DS-09 清理：类别多选（仅 Core 允许手动清理的类别）、早于天数、预览（截止/过期/候选/警告）、按幂等键创建作业、确认/取消、进度计数与事件列表 |
| `MainWindow.StorageSettings.cs` | DS-09 盘点与策略：缓存快照读取（不触发扫描）、显式刷新、趋势窗口 7/30/90、分类目录与受保护对象、保留策略按目标编辑并用读到的 revision 提交 |
| `MainWindow.VoiceSettings.cs | DS-03 语音原生表单：TTS/ASR 服务商、TTS 模型（音色/格式/采样率/四种能力）、ASR 模型（语言/采样率/三种能力）；逗号分隔列表编辑、默认项真源展示、Core 未就绪时禁用表单 |
| `MainWindow.LlmSettings.cs` | DS-02 LLM 资源池原生表单：服务商连接、并发与速率、模型定义、上下文与计费；Keep/Replace/Clear 密钥语义、删除确认、Core 未就绪时禁用表单并显示真实原因 |
| `Kernel/WinUiDesktopServices.cs` | Core 通过 DI 调用展示端口，DispatcherQueue 调度、取消及关闭处理 |
| `HostingProbeWindow.cs` | 手动/自检双 WebView2，独立 UDF 与离线页面，Cookie 隔离，原生浮层验证 |
| `PuddingDesktop.csproj` | WinUI 3/.NET 10/x64，非打包、AppSDK self-contained，禁止视图直接引用业务工程 |
| `../PuddingDesktop.Foundation` | 独立角色/文档/布局/配置状态与 `IDesktopKernel` 合同 |

默认加载进程内 Core DLL，使用 `PuddingChat.WinUI` 原生角色卡、消息卡、执行过程和输入框；通过 Composition 的 `InProcessChatClient` 直接调用 Core 服务。聊天不再加载 WebView2。“初始化与配置”由用户点击后打开独立管理页面。默认数据目录 `%LOCALAPPDATA%/Pudding/DesktopData`；`--demo` 仍为旧骨架静态布局样例并跳过自动启动。运行中心支持启停/重启，关闭先取消并排空聊天调用，再释放 Host；托盘与硬崩溃恢复待验收。

构建：`dotnet build Source/PuddingDesktop/PuddingDesktop.csproj -c Release --artifacts-path temp/build/desktop-kernel`。便捷入口：`TestScripts/start-pudding-winui-preview.ps1`；自检：`TestScripts/test-pudding-winui-skeleton.ps1`。结果及未验收项见 `Docs/Reports/Desktop-WinUI3-Skeleton-2026-09-27.md`。

Core 接入验证：`TestScripts/test-pudding-desktop-kernel.ps1`。Composition 是唯一引用 Host 的桌面装配层；Shell 禁止传递项目引用，View 不引用 Host/Runtime。

设置中心骨架及 DeepSeek 后续任务：[交接任务书](../../Docs/Tasks/Desktop-Admin-Settings-DeepSeek-2026-09-27.md)。本轮无新增业务 Client/API 包装器，后续直接绑定 Core 既有方法。默认数据目录已由 2026-09-27 裁定改为 `D:\data`（已保存值优先）；上文旧默认路径不再适用。DS-00 已交付：设置操作经 `IDesktopKernel.RunSettingsAsync` 进入 Core 作用域，未就绪/停止中拒绝并给出原因，切换角色作废迟到结果；见 [DS-00 接入说明](../../Docs/Features/Desktop-Settings-Operation-Boundary-2026-09-27.md)。
