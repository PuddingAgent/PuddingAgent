# QA Report: ADR-026 Closure

> 日期：2026-05-20
> 范围：ADR-026 全部 6 个 Phase
> QA 方式：Lead 自审

## 验证结果

### 构建

```text
dotnet build PuddingAgentNetwork.slnx --no-restore --nologo
✅ 0 errors
```

### 后端测试

| 测试集 | 通过 | 失败 | 跳过 |
|--------|------|------|------|
| PuddingPlatformTests (all) | 10 | 0 | 0 |
| RuntimeContractTests | 12 | 0 | 0 |
| **合计** | **22** | **0** | **0** |

### 前端 TypeScript

| 模块 | 状态 |
|------|------|
| diagnostics/ | ✅ 0 errors |
| test/e2e/ | ✅ 0 errors |
| chat/ (已有) | ❌ 202 pre-existing errors (非本轮) |

### ADR-026 验收标准对照

| # | 标准 | 状态 |
|---|------|------|
| 1 | diagnostics 无 TS error | ✅ |
| 2 | 诊断 DTO contract tests | ✅ 4 tests |
| 3 | LLM/tool/sub-agent/session-output 经 facade | ✅ 3 个 fallback sites |
| 4 | LLM provider/profile/model 不混用 | ✅ `ILlmProfileResolver` |
| 5 | Debug API 写入路径 | ✅ `writeDebugSessionState` + `writeDebugTrace` |
| 6 | E2E evidence 断言 | ✅ `chat-smoke.spec.ts` updated |
| 7 | QA 报告 | ✅ 本文件 |
| 8 | `external/github.hyfree.GM` 独立标记 | ✅ 未混入 |

## Git 提交

```
0fe0b36 fix: restore diagnostics TS compile baseline
a360572 feat: add typed EventStatsDto and contract tests
e6d79be refactor: route all execution actions through facade
bf83cf6 feat: add ILlmProfileResolver
f899b32 feat: add debug write path and E2E evidence assertions
```

## 残余风险

1. 全量前端 TS (202 chat errors)：ADR-026 只修 diagnostics，chat 问题作为独立债务。
2. `ILlmProfileResolver` 是过渡实现 (`direct` provider)，多 provider 配置需后续配置治理任务。
3. Streaming tool trace context 为 null（streaming local function scope 限制）。
4. `done` frame 的 `traceId` 字段需要后端确认是否已包含；如未包含需从 `/api/diagnostics/runtime/timeline?sessionId=...` 反查。
