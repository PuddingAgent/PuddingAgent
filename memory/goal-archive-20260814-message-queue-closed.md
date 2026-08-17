# goal.md 归档 —— 消息队列任务闭环（2026-08-13 全天）

> 本文件为 goal.md 47KB 超限后的精简归档，替代性参考。详细过程见 git 提交与 session logs。

## 一、任务最终状态（全部闭环 ✅）

消息队列任务主线 + 卡片 UI + 图片占位修复，五件事一次重启全部生效并验证：

| # | 事项 | commit | 状态 |
|---|------|--------|------|
| 1 | busy 排队修复（DeferAsync + 30s 冷却 + 不递增 attempt）| `cb4c405` | ✅ 验证通过 |
| 2 | 投影字段（substate/deferCount/executionState/position）| `22f5b0bb` | ✅ |
| 3 | Phase 2 前端接线（substate 驱动徽标 + 动作按钮占位）| `169c417`（蜜糖）| ✅ |
| 4 | P0 卡片 UI（时间戳/按钮/代码块/日期分隔线）| `d2686e0`/`b490c04`/`4a6307a` | ✅ |
| 5 | 图片占位修复（post 混排图 → visionArtifactIds）| `768f5a0` | ✅ 待明天混排活体确认 |

**QV 真验证双证据互证闭环**：
- 投递面（蜜糖）：忙碌无报错 → 空闲按序投递（A 先 B 后）→ deliveryIds 非空
- DB 面（6a8 捕证）：defer 期 status=queued / defer_count 递增 / execution_state=Busy / **attempt = defer+1** 不膨胀
- 旧峰值 attempt 248/452 → 新二进制个位数

**唯一剩余**：混排图文回归（文字+图片同条）。用户 20:59 回「明天发你」，样本明天到，蜜糖收到即验证回传收尾。

## 二、协作约定（与蜜糖 258 固化）

1. **只推自己 commit**，不代推他方 commit；push 时机自管（验证后推）。
2. **提交 ≠ 部署**：集成验证前必须核对宿主二进制版本（最后重启时间 vs 提交时间）。
3. **前端 build 先于 dotnet build**：`pnpm run build`（产出 dist/）必须先于 `dotnet build`（PuddingHostContent.props 按 `Exists(dist/index.html)` 条件纳入）。
4. 文件边界：前端渲染文件（PuddingPlatformAdmin）归蜜糖；后端语义归 6a8；跨边界只列「delta 清单」。

## 三、遗留 backlog（不阻塞，记录在案）

1. `LlmStreamObservabilityTests ×3` —— 观测指标（firstChunkMetric/rateLimitMetric/WaitMs）测试先写、源功能未实现，暂缓（做 LLM 性能监控时一并实现）。
2. `IntentConsole.test.tsx` 陈旧语音用例 —— voiceInputAdapter prop 未消费，测试指向已移除的旧语音 UI。
3. `[图片]` 占位替换 Markdown 引用 —— FeishuPostContentConverter:127 仍输出字面「[图片]」（最小改动优先，未做）。
4. 前端 jest Babel 插件缺失：`Cannot find module '@babel/plugin-transform-modules-commonjs'`（pnpm 依赖隔离）。
5. `dotnet test` 全量遇 4 个既有失败用例会退出码 1，被子代理框架误判为致命失败（shell 非零退出码≠任务失败）。

## 四、关键技术知识（已存记忆库）

- 数据库位置：`D:\data\databases\pudding_platform.db`，队列表 `message_deliveries`。
- 前端部署：宿主 serve dist（非独立静态），路由前缀 `/admin/`，`wwwroot/admin/**`。
- 前端构建命令：`cd Source/PuddingPlatformAdmin && pnpm run build`（= max build）。
- 飞书主动投递修复（`cbc9716e`）：`isReply` 标志区分回信（StableId 幂等）/主动投递（Guid.NewGuid），解决 messageId 钉死 + deliveryIds 恒空 + externalMessageId 复用入站 id 三症状。

## 五、教训固化

- `git commit -m` 在 cmd 下 message 含空格会被拆成 pathspec，必须用 `-F` message 文件。
- 诊断类子代理若跑 `dotnet test` 复现，必须要求用 file_read 的 TailLines/offset 读日志尾部、设 tool output 上限；同一诊断任务失败 2 次即停重派。
- 挂死进程树排查用 Get-CimInstance 全系统查询 + CommandLine 复核，tasklist 有盲区。
