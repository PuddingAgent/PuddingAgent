# Desktop 空闲 CPU 与日志展示修复（2026-09-21）

## 结论与证据边界

现场高占用主要在 Desktop 的 WPF 图形线程。真正使隐藏页面仍持续消耗 CPU 的，是 WebView2CompositionControl 的 D3D 图像仍连接在 WPF 渲染链上；运行中心每秒全量属性通知和托盘更新是独立的放大因素。另外实测确认 IdleDetector 在空闲状态每5秒重复打印同一状态。三项修复分别提交：`3fe4ccb`、`834f281`、`5f23db5`。

本轮未复现截图 12.7% 的瞬时峰值。所有下表 CPU 都是 8 逻辑处理器下的整机时间口径：`ΔTotalProcessorTime / ΔwallTime / 8 × 100`，不能与任务管理器某一瞬间直接等同。0% 表示采样精度内无可见 CPU 时间增量，不表示理论零成本。

## 现场定位

- 修复前 Desktop PID 36768：40 秒采样，均值 2.280%、最大 2.969%；Core PID 42796：均值 0.201%。WebView 子进程在此窗口几乎无 CPU 增量。
- 10 秒线程采样中，TID 37760 消耗 2.000 CPU 秒，其余线程最多 0.015625 秒。原生指令快照包含 WPF、D3D、图形资源分配、共享资源打开和提交路径。指令快照含等待，不是 CPU 加权栈，不能据此把样本占比当作函数 CPU 占比；导出符号的大偏移不能当作精确函数名。
- WPR CPU 采集被系统策略拒绝（`0xc5585011`），因此采用线程 CPU、原生指令快照、SDK 本地反编译和控制变量实验交叉定位，没有报告不存在的 ETW 证据。
- 窗口原始/最小化/恢复为 2.327% / 0.700% / 2.490%。Mica 开/关/恢复为 2.083% / 1.937% / 2.262%，关闭 Mica 未解决问题。
- 隔离进程加载真实 MainWindow 和资源、不创建 WebView 时，静态设置页 CPU 为 0%。没有读取或复制生产浏览器 Profile；WebView 实验使用 `temp/` 下的独立 Profile。

SDK 1.0.4078.44 的绘制路径是 `GraphicsItemD3DImage.OnFrameArrived → RequestRender → CopyResource → SetBackBuffer/AddDirtyRect → WPF shared texture presentation`。`Visibility.Collapsed` 会通知浏览器 controller 隐藏，但没有把模板 `PART_image.Source` 从 WPF 图像资源链解绑。静态页面实验：隐藏仍约 0.146%，TrySuspend 成功后仍约 0.146%；解除 Image.Source 后为 0%，Dispose 后也为 0%。这将问题收敛到图像呈现链，而非后台 Agent 是否在调用模型。

## 两次部署的对照

每页停留 2 秒后采样 12 秒。第一次部署只修日志刷新；第二次再修隐藏图像呈现，使用同一页面选择脚本。

| 页面 | 原版 | 仅日志刷新修复 | 加入图像呈现修复 |
|---|---:|---:|---:|
| 运行中心 | 2.014% | 2.130% | 0.098% |
| 系统设置 | 1.709% | 2.474% | 0.000% |

第一次没有消除持续图形成本，不能把最终下降全归因于日志优化。第二次运行中心约下降 95%；截图峰值未在相同条件复现，因此不宣称“12.7% 精确降到 0%”。

工作台重新显示后，真实登录页恢复正常图像；30 秒采样 Desktop 均值 **1.019%**，Core 均值 **0.308%**。可见 WebView 的呈现仍有成本，本次没有替换 CompositionControl、关闭硬件加速或以销毁浏览器会话换取低占用。

后续运行中心 40 秒采样：Desktop 均值 **0.045%**、最大0.474%；Core 均值 **0.227%**。工作台显示/最小化/恢复对照为 **1.009% / 0.000% / 1.156%**。这些样本在 Core 降噪补丁之前采集，专门验证 Desktop 修复。

## 实现与约束

1. `CoreProcessLogBuffer.GetTail` 缓存未变化的尾部文本，Append 时失效；锁内维护有界队列及一致快照，保留 500 行。
2. `RuntimeCenterViewModel.RefreshTransient` 仅更新变化的日志/运行时长。原实现空日志还会在空串和占位符之间来回通知；现在一次性确定最终文本。运行状态变化仍由 coordinator 事件驱动。
3. `RuntimeCenterView` 只在页面可见且窗口未最小化时启用刷新 timer；恢复时立即补齐最新日志。隐藏不清空 Core 输出缓冲区。
4. `MainWindow` 不再因每项日志/时长属性通知更新 Shell；`DesktopTrayIconService.UpdateToolTip` 对相同提示幂等，避免重复 `Shell_NotifyIcon`。
5. `WebView2PresentationGate` 使用 SDK 明确声明的模板部件 `PART_image`，隐藏、卸载或最小化时暂存 ImageSource 并解绑，恢复时接回同一图像；覆盖 Workbench 与 Agent Browser。保留 CoreWebView2、页面、导航和自动化执行，未调用 TrySuspend 来暂停 Agent 浏览器。
6. Source 变化可能发生在 Freezable 正在挂接上下文的过程中；不能在回调中同步改回 Source，否则出现 inheritance-context 异常。按 Dispatcher Render 优先级合并延迟处理，忽略自身写入，Dispose 时解除所有订阅。
7. `IdleDetector` 分离“需要再次调用调度器”的 ReArm 标志与“已记录进入空闲”的日志标志。同一活动窗口只记录一次 Information，RecordActivity 后重新允许记录；不减少检查频次、不抑制调度回调，异常日志仍保留。

## 日志为何出现

截图中的 `periodic:skill.improve` 是后台技能改进，并非用户聊天任务。系统日志显示该次任务于 **09:09:57** 完成一个 Skill 的 1.0.0 → 1.0.1 更新，随后记录 `subconscious_job.complete succeeded`。这证明该截图时刻确有后台工作，不能称为无限空转日志。

另在09:43–09:44发现真重复日志：`Global idle threshold reached` 每5秒出现一次，原因是 Heartbeat 的 ReArm 让 IdleDetector 将每次检查都按“新空闲状态”记录。新增回归在旧实现连续3次回调打印3条，修复后3次回调只打印1条，实际活动后再次空闲打印第2条。

HttpClient 的 Information 级别仍会打印请求开始、发送、响应头、结束，与 DirectLlm/RuntimeActivity 的语义日志叠加；TaskAutoDispatch 的周期诊断也保留。此次去掉已验证的空闲重复状态日志，保留后台学习及请求/故障诊断；不宣称所有后台日志均已降噪。

## 验证与部署

- 新增空闲通知/尾部复用回归：旧实现 2 项失败，修复后通过。
- 呈现门控回归覆盖隐藏与恢复同一 ImageSource、隐藏期间 SDK 延迟赋值、释放订阅。测试曾捕获同步改写 Freezable 的异常，改为合并延迟处理后通过。
- `PuddingDesktop.Tests` 全量 **247/247** 通过；Desktop 定向构建 0 error，现有 7 warning。构建和测试串行，产物位于 `temp/build`、`temp/test-out`。
- IdleDetector/Heartbeat 定向回归 **18/18** 通过，含旧版失败的真实周期回调测试。Core 定向构建0 error、116 warning。
- 通过托盘“退出 Pudding”退出旧 Desktop/Core，再由 Explorer 独立启动新进程；不是启动第二实例来声称重新加载。
- Desktop PID **42912**，第二次部署 Core PID **2844**、**09:39:02 Ready**。Desktop 构建产物与运行目录逐文件 SHA-256 对比无差异；复制时被占用的 WebView2Loader.dll 内容也已核对一致。
- 空闲降噪补丁通过 Desktop 的预编译产物部署接口再次重启 Core，最终 PID **28500**、**09:48:04 Ready**。264个托管产物清单的 prepared/loaded SHA-256 均为 `9cb46db12a8b751fda37ad9105e88bc8f0661242fdc4d32b50313cad2df61f1f`，回执 errors 为空、assembliesReloaded/coreRestarted 均为 true。
- 全部补丁加载后的50秒采样：Desktop均值 **0.040%**、最大0.291%；Core均值 **0.255%**，其中包含启动后的 SessionChunkBackfill（09:48:47完成），不能将该 Core 样本称为严格无后台工作的纯空闲样本。09:48:33–09:49:48跨过多个5秒检查周期，只有一条进入空闲的日志，重复刷屏已消失。
- `/health` healthy、`/health/ready` ready、WorkbenchReady、前端 HTTP hash 与部署文件一致；Codex MCP Available/7 工具。分类器无新裁决时仍是 unknown，不当作故障或真实模型验收。
- 此轮未直接改写业务数据库、Agent 任务、心跳或学习调度配置；没有主动发起付费模型测试，Core启动后的正常后台管道继续执行。

采样原件保留在 ignored `temp/`：`cpu-0921-before.json`、`desktop-threads-0921.json`、`desktop-native-0921.json`、`desktop-pages-{before,after,final}.json`、`desktop-*-probe.log`、`cpu-0921-workbench-final.json`、`cpu-0921-runtime-final.json`、`cpu-0921-all-fixes.json`、`desktop-visibility-final.json`。复现进程采样使用仓库 `TestScripts/measure-pudding-process-baseline.ps1`；替换 PID 前核对父子关系。
