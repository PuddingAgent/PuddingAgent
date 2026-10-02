# TestScripts/perf —— Chat 前端性能实测脚手架

用途：在**真实生产构建**上录制 Chat 长消息渲染、滚动、历史分页与活动回合更新链路的运行时耗时，
把 [Chat 前端性能诊断报告](../../Docs/14_reports/Chat-Frontend-Performance-Diagnosis-2026-10-01.md)
中的源码推断升级为实测结论。

实测结论见 [Chat 前端性能实测报告](../../Docs/14_reports/Chat-Frontend-Performance-Measurement-2026-10-01.md)。

## 前置条件

1. Desktop + Core 正在运行（本机产品形态：`PuddingDesktop.exe` → `PuddingAgent.exe --desktop-child --data-root D:\Data`，
   在 `http://localhost/` 提供 `wwwroot/admin` 生产构建）。
   - 默认不重启 Desktop、不改 WebView2 用户数据目录；测量用**同引擎的独立 Edge 实例**。
2. 独立 Edge 实例已开启远程调试（Chromium 154，与 WebView2 同版本）：

```powershell
Start-Process 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' -ArgumentList `
  '--remote-debugging-port=9333', "--user-data-dir=$PWD\temp\perf-profile", `
  '--no-first-run','--no-default-browser-check','--headless=new','about:blank'
Invoke-RestMethod http://127.0.0.1:9333/json/version   # 应返回 Edg/154.x
```

3. Node ≥ 22（脚本使用全局 `WebSocket`，无第三方依赖）。

## 运行

```powershell
# 全部场景
node TestScripts/perf/record.mjs all

# 单场景：cold | warmLong | scrollLong | abPatches | historyPage | streaming
node TestScripts/perf/record.mjs warmLong

# 汇总为报告用指标
node TestScripts/perf/summarize.mjs

# 定向诊断脚本
node TestScripts/perf/diag-scroll.mjs        # 滚到顶后滚动容器高度塌缩的时间序列
node TestScripts/perf/diag-cssbox.mjs        # 定位内联 totalSize 被压成 662px 的 CSS 来源
node TestScripts/perf/diag-flexshrink.mjs    # 验证 flex-shrink:0 修复
node TestScripts/perf/diag-layoutcost.mjs    # 单条 50KB vs 多条高行的 content-visibility A/B
node TestScripts/perf/diag-containment.mjs   # 后代溢出/包含关系假设验证
```

结果写入 `temp/perf/out/`（`record-results.json`、`conversation-baseline.json`、各诊断 JSON）。

## 方法学与边界

- **只替换数据源**：用 CDP `Fetch` 域拦截 `GET /api/workspaces/{ws}/agents/{ag}/conversation`，
  返回合成的 `AgentConversationView`；其余链路（投影、合并、Markdown、IndexedDB 写入、虚拟化、
  布局测量、事件埋点）全部真实。不写数据库、不调用 LLM、不动运行中的 Desktop。
- **活动回合**：该构建下 `agent-client` 架构默认开启，会话标记为 projection-owned 后
  **SSE `/events/stream` 被显式关闭**（`useChatState.ts` 中 `stopSessionEventStream`），
  活动回合数据由 `index.tsx` 的 1200ms 轮询 `conversation` 提供。因此 `streaming` 场景
  通过让桩响应逐次变长来复现「拉取 → 合并 → 投影 → 权重 → React 提交 → 缓存写」全链路。
- **浏览器口径**：Edge 与 WebView2 同为 Chromium 154，相对分项可比；绝对毫秒数受窗口尺寸、
  扩展与 GPU 环境影响，跨设备不可直接比。
- **已知采样边界**：`chat.output.paint` 有 250ms/事件名的节流，且行卸载时会取消未触发的 rAF，
  因此长行频繁卸载的场景（滚动、塌缩）会低估 paint 样本数；此时以 `browser.longtask` 与
  可用性（滚动高度是否可达）为主证据。
