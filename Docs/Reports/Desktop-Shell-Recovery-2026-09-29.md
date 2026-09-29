# Desktop 恢复主线：执行与验证记录

## 分支与保存

- 用户授权：完整保存当前成果到 B，从 `765b964f5aedf621fe9d9ffd04b5a4a9bf64924e` 重建 master，仅迁入 WinUI Shell UI，保留独立 Core 和 Web，筛选必要修复并验证。
- `B`（分支说明“桌面开发”）指向 `a23ebf3e8ec90c65d37b8a9fbd46ceb139445220`。父提交为原 master `7132536`；原有提交历史全部可达。快照收录 37 个有实质差异的未提交/新增文件，包括架构文档、在途代码、已跟踪的仓库 memory 文件；换行规范化后无实质差异的文件未产生额外 diff。
- 建快照后确认工作区干净，再移动本地 master 到指定基线。未删除其他 worktree，未推送或强推远端；master 已取消旧远端跟踪，避免误 pull 把废弃方向重新合入。
- 构建缓存、忽略的临时产物和本机凭据未加入 Git，也未因恢复而清除。B 保留原始开发快照，不代表在途改动全部通过验证。

## 恢复后的实现

1. WinUI 保留 B 中的窗口材质、色彩资源、圆角控件和标题栏风格。业务工作台作为完整 WebView2 区域承载，角色、聊天、业务设置继续由现有 Web 前端提供。
2. `DesktopApplicationCoordinator` 仅替换 WPF 窗口/Dispatcher 接线。旧 CoreProcessSupervisor、重启策略、配置/token、Bootstrap、Debug 和 Browser Bridge 源文件链接编译，继续启动 `PuddingAgent.exe --desktop-child`，不创建进程内 Host。
3. `PuddingBrowser.WinUI` 提供 WinUI surface 与 DispatcherQueue，浏览器 runtime/context/page/DOM 共用旧驱动源文件；少量条件编译仅适配 WinRT WebView2 签名。可信工作台与 Agent 浏览器使用独立用户数据目录。
4. 原生区域仅包含工作台容器、Agent 浏览器、运行中心和启动设置；主题可保存。角色演示、原生聊天状态、进程内 Kernel 合同、旧 skeleton 启动脚本已移除。
5. 关闭策略沿用启动器设置，托盘适配 WinUI；没有托盘时关闭会执行停机，避免隐藏后无法找回。切换 DataRoot 后要求重开 Desktop，防止浏览器/Bootstrap 留在旧目录。

## Core 修复取舍

| 来源 | 处理 |
|---|---|
| `d4c071f` | 移入终端输出排空、关闭通道与异常保护及原回归测试 |
| `f1f5f21` + `197761b` | 仅提取 Core/Runtime 的工具准入类型透传与等待不计失败；未引入原生聊天模块 |
| B 的 DataRoot lease | 复用文件句柄租约，在 Console/DesktopChild 入口获得并随进程释放；补独立互斥回归 |
| `ff1e58d` / `ad0a3fa` 中的独立业务修复 | 最小修复既有 Web 控制器的角色 ID 投影、重复邮箱冲突与默认工作区删除保护；不搬入原生管理页面或整套服务重构 |
| B 的 `S5SupplyTestDoubles` 补齐 | 全量构建发现旧基线两个替身缺少 ProbeDocuments；复用 B 中完整实现，保持真实引擎委托与未知计数语义 |
| 原生聊天/管理、进程内 Composition、移除 Web SPA | 不引入 |
| 启动优化、审批持久暂停、索引维护新增接入、在途历史回放重构 | 保存在 B，不作为 Shell 恢复的前置。按独立功能重新评估依赖和验收，不整体合并、不声称已迁入 |

## 验证方法与证据

所有输出位于 `temp/build` / `temp/test-out`，原始日志位于 `temp/recovery-*.log`。数据库使用 SQLite 在线备份，源连接只读，包含一致 WAL 状态，不直接复制正在使用的 db 文件。数据库副本只用于 SchemaProbe；探针不启动 Host、Worker、任务调度、连接器或模型调用。

真实 UI/生命周期使用 `TestScripts/test-pudding-desktop-launcher.ps1`，为每次运行创建独立 DesktopHome、空 DataRoot 和随机空闲端口。运行既有 Core，可打开真实 `/admin/` 工作台，测试不使用生产凭据。

已完成的首轮真实生命周期证据：Shell PID `1448`，Core PID `38476 → 12876`。工作台加载、浏览器标签/导航/稳定 Agent 目标与人工接管、无业务 Host 装配、重启换 PID、停止 Core 后 Shell 仍存活全部通过。外部确认 Shell/两个 Core 均退出、端口释放、DataRoot 租约可重新打开。此证据不代表生产环境部署或真实模型 smoke。

### 最终回归

| 项目 | 结果 |
|---|---|
| `PuddingAgentNetwork.slnx` 全量构建 | 0 错误；1066 条既有包安全、可空性与分析器等警告，未将恢复扩大为依赖升级 |
| Foundation（去掉原生业务模型后的版本） | 7 / 7 |
| WinUI 浏览器共用 DOM 驱动 | 7 / 7 |
| 旧启动器 / Bridge / 进程监督回归 | 247 / 247 |
| Host 的 Hosting / Desktop 定向回归 | 119 / 119 |
| Terminal 排空与关闭定向回归 | 67 / 67 |
| 工具准入等待定向回归 | 7 / 7 |
| 管理控制器与 ExecutionRun schema 回归 | 4 / 4 |
| Web 前端源码构建与 chat bundle 门禁 | 通过；同步入口 1,375,474 字节，chat 路由 342,888 字节；前端源码相对恢复基线无改动 |
| Debug 与 Release 完整产品发布 | 均成功，含 `PuddingDesktop.pri` / XBF、`core/PuddingAgent.exe`、`core/wwwroot/admin/index.html`；Shell 顶层不携带 Host / Runtime / Platform |

上述定向自动测试合计 **458 项通过**，并非全仓所有测试套件。构建后最后的 WinUI 修复由 Debug/Release 发布和实际生命周期复验覆盖。

最终 Debug 产品包生命周期（含本轮从源码重建的 Web）：Shell PID `39264`，Core PID `38776 → 42428 → 10876`。在前述检查上增加了托盘隐藏/恢复、强制结束测试 Core 后 Shell 存活及手动恢复 Web；外部确认三个 Core PID、Shell、端口和租约全部释放。截图检查了运行中心内容的排版；RenderTargetBitmap 不包含系统 Mica/原生标题栏，不把它当成完整材质、DPI 或输入法验收。自动 smoke 代码仅编入 Debug，Release 不接受 smoke 环境变量。

### 隔离业务数据检查

- 从 `D:\data\databases` 以只读源连接执行 SQLite 在线备份，平台副本大小 `9,870,876,672` 字节；仅副本运行 schema bootstrap，两次执行成功。
- 恢复版 Platform 模型对应 **76 张表**的实际字段查询全部成功；额外检查新增 `NOT NULL` 且无默认值、旧模型无法提供的列，未发现插入阻断。查询使用表别名限定字段，避免 SQLite 的双引号字符串回退造成假阳性。
- controller（131,072 字节）与 memory（338,178,048 字节）副本 `quick_check` 为 `ok`。平台大库备份已提交，但全库 `quick_check` 扫描因持续耗时被停止，**不宣称平台整库完整性检查通过**。
- 不启动副本上的后台任务、连接器或真实模型；未修改生产数据库、生产配置、凭据或运行中的 Core。当前结论是模型结构兼容与隔离空库宿主可运行，不等于所有历史业务写入、聊天流式输出、附件、真实模型和生产升级已验收。没有证据需要时不添加兼容层或对生产库执行降级。

### 复验入口

构建与发布统一在仓库隔离输出下运行：

```powershell
dotnet build PuddingAgentNetwork.slnx --artifacts-path temp/build/recovery-all -m:1
# 如需从源码重建 Web，在 Source/PuddingPlatformAdmin 执行 pnpm run build，
# 并把 PUDDING_ADMIN_OUTPUT_PATH 设为仓库 temp/build 下的绝对路径；
# 发布时通过 -p:PuddingAdminDistPath=<同一绝对路径> 打入该产物。
dotnet publish Source/PuddingDesktop/PuddingDesktop.csproj -c Debug --artifacts-path temp/build/recovery-publish -o temp/build/recovery-bundle
./TestScripts/test-pudding-desktop-launcher.ps1 -DesktopExe temp/build/recovery-bundle/PuddingDesktop.exe -CoreExe temp/build/recovery-bundle/core/PuddingAgent.exe
# 仅接受先制作好的 temp/test-out 副本：
dotnet run --project Tests/PuddingRecovery.SchemaProbe --artifacts-path temp/build/recovery-probe -- temp/test-out/recovery-data-copy/pudding_platform.db
```

本轮未推送远端，也未把恢复版本部署到生产 DataRoot。原始验证日志保存在被忽略的 `temp/recovery-*.log`。收尾清理隔离副本时，自动审批先后拦截了已校验路径的递归删除与逐文件删除，仅返回 `blocked by policy`；没有绕过拦截。数据库副本、生命周期测试目录和构建产物仍保留在 `temp/test-out` / `temp/build`，未加入 Git，待本机后续清理。结果与复验方法以本文为准。
