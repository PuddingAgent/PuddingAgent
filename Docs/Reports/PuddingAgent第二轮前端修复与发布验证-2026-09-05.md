# PuddingAgent 第二轮：前端修复与发布验证

日期：2026-09-05。承接首轮效率修复，关闭前端类型检查门槛并生成隔离的 Desktop/Core 发布包。**本轮未替换运行中的 Desktop/Core、未修改 D:\data、未派发模型任务；发布包生成不等于产品部署或性能验收完成。**

## 已修复

| 范围 | 实际问题与修复 |
|---|---|
| 管理界面样式 | `PuddingAdminShell/EntityCard/PageHeader/StatusBadge/Toolbar` 原来将原始样式对象交给 `className`，或让 `classnames` 将对象键当作类名。统一改为 `antd-style/createStyles` 生成实际类名；状态圆点通过 `currentColor` 跟随状态色，不再依赖不匹配的 `.dot` 选择器。属于样式失效修复，不是完整视觉重设计。 |
| 性能面板 | `PerfTab` 错把整个 `useChatStyles()` 返回对象当作 styles；现取其 `.styles`。诊断快照、真实 workflow event、capture 的 stopped 状态和指标回调与 `utils/debug` 合同对齐。 |
| 重连提示 | 原 `reconnectCountRef` 未穿过 ChatLayout，ChatMain 还保留每 500ms 轮询该 ref 的逻辑。连接 hook 现提供响应式计数，经 useChatState→ChatLayout→ChatMain 传递；删除轮询。ref 仅保留重连退避的即时计数。收到有效事件后才复位，正常 token 事件不重复 setState。不能将移除一个此前可能未接通的轮询直接换算为实测 CPU 收益。 |
| Chat 数据合同 | 统一 pending delta 为 `{ delta, baseLength }`，修复清理和发送 hook 的错误 Map 类型；Agent 状态保持实际联合类型，切换回调防御 undefined；补齐 initializing 文案。 |
| 其他类型门槛 | 月度总计/provider/model 的 inputCost/cacheHitCost/outputCost 与 `StatsApiController.GetMonthlyTokenStats` 实际响应一致；修正 React 19 ref 初值、语音能力检测和诊断 Window 扩展类型。tsconfig 增加 module=esnext 支持现有动态 import，未放宽 strict。 |
| 可追溯发布 | 前端构建及包体检查共用 `PUDDING_ADMIN_OUTPUT_PATH`；Host 打包及缺失检查共用 `PuddingAdminDistPath`。默认仍是原 dist/dist-dev 行为，隔离发布不覆盖开发态 dist 或运行中的程序。 |

## 验证结果

- 全量 `npm run tsc -- --pretty false`：**70 个错误降至 0，exit 0**；补测试后再次通过。
- Jest：**12 suites、118 tests 全通过**。新增真实 CSS 计算结果、五种状态圆点、性能快照/采集按钮/流程步骤、initializing 文案和响应式重连计数断言；同时回归消息缓冲、清理、session selection、terminal identity、recovery、replay、projection 和 debug。
- 结果文件：`.tmp-test-out/efficiency-results/frontend-round2.json`。初次运行暴露新测试的类型导入转译问题及两处测试扩展名错误；修正入口并完整重跑，不将失败尝试计入通过数。
- 额外对 PerfTab 与 SSE connection 运行 `--detectOpenHandles`：2 suites、5 tests 通过，exit 0，该次未报告句柄定位信息；不据此推断全部测试或产品均无资源残留。
- 前端 production 构建通过，Chat 包体预算通过：sync **1,375,324 B**，chat **466,698 B**，common **191,372 B**。这是当前包体，不是前后性能对照。
- Desktop Release publish（含 Core）exit 0。`core/wwwroot/admin` 中 **148 个源文件 SHA/字节一致，276 个 br/gz 副本解压后也与原文件一致**。最初按文件总数相等检查不适用于 SDK 自动压缩输出，已改为验证压缩副本内容，没有删除这些合法产物。
- `git diff --check` 通过。首轮后端定向测试结果见首轮报告；本轮不冒称重跑了全部后端测试。

## 隔离产物与复现

前端：`.tmp-build/admin-efficiency`。完整产品包：`.tmp-build/desktop-efficiency`。中间程序集输出：`.tmp-build/desktop-efficiency-bin`。均在仓库构建目录，不在运行数据目录。

从 `Source/PuddingPlatformAdmin` 执行：

```powershell
$env:PUDDING_ADMIN_OUTPUT_PATH = 'E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\admin-efficiency'
npm run build
```

从仓库根目录执行（与其他 Desktop build/test/publish 串行）：

```powershell
dotnet publish Source/PuddingDesktop/PuddingDesktop.csproj -c Release --no-restore --nologo -o E:/github/AgentNetworkPlan/PuddingAgent/.tmp-build/desktop-efficiency -p:OutDir=E:/github/AgentNetworkPlan/PuddingAgent/.tmp-build/desktop-efficiency-bin/ -p:PuddingAdminDistPath=E:/github/AgentNetworkPlan/PuddingAgent/.tmp-build/admin-efficiency
```

源码 HEAD：`731d5e063cdd888128cbcfc3ba53c32cfae2284d`，**包含未提交修复和原有用户改动，不是干净提交制品**。发布清单见同目录 `pudding-agent-round2-build-2026-09-05.json`。部分程序集 ProductVersion 仅为 1.0.0，且 apphost exe 可跨源码变更保持相同 hash，因此核对实际业务 DLL 和静态文件，不能只看 exe 或 HEAD。

## 未关闭门槛

1. 当前进程仍为 Desktop PID 20284 → Core PID 32444，路径仍在原 Debug/bin 目录；本轮没有重启它们。下一轮先确认活跃任务/lease 和部署窗口，再由进程外控制器切换到明确包，核对 Ready、实际 DLL/静态文件和进程回收。不能从旧会话运行成功推断新代码已加载。
2. 发布存在现有依赖告警：包括 `Microsoft.Bcl.Memory 9.0.4`、`Newtonsoft.Json 12.0.3` 的 NU1903。未改锁文件或做依赖升级，告警不等于已经修复，发布前需独立评估与升级验证。
3. 部分 Jest 运行仍提示退出时有未关闭异步操作；普通通过结果不证明不存在资源残留，也不能直接归因于产品内存泄漏。
4. 真正的 Chat 视觉效果、滚动/长历史加载、CPU/Private/WS/GC 仍需在新部署上以相同会话和负载对照。本轮没有 UI 实机截图或 CPU/内存改善百分比。
5. 夜间吞吐、Ready 任务真实启动、legacy claim 恢复及连续七日 >99% 缓存命中仍需产品验收。首轮的 **91.849%** 是修正时间窗口后的历史统计，不是本轮优化带来的提升。
