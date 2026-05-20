# QA Report: ADR-027 Final Closure

> 日期：2026-05-20
> 范围：ADR-027 全部 7 个 Phase
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
| PuddingPlatformTests (filtered) | 9 | 0 | 0 |
| RuntimeContractTests | 12 | 0 | 0 |
| **合计** | **21** | **0** | **0** |

### 前端 TypeScript

| 模块 | 状态 |
|------|------|
| diagnostics/ | ✅ 0 errors |
| test/e2e/ | ✅ 0 errors |
| chat/ (已有) | ❌ pre-existing errors |

### ADR-027 完成标准对照

| # | 标准 | 状态 |
|---|------|------|
| 1 | ADR-026 不再误标 implemented | ✅ partially-implemented |
| 2 | E2E traceId 强断言 | ✅ `expect(traceId).toBeTruthy()` |
| 3 | E2E 使用正确 evidence 路由 | ✅ `/api/diagnostics/e2e/evidence/{traceId}` |
| 4 | 后端 done frame 提供 traceId | ✅ `streamTrace.TraceId` in done payload |
| 5 | SubAgentTool 经 ISubAgentInvocationService | ✅ facade path + legacy fallback |
| 6 | LLM resolver 返回 ResolvedLlmInvocationProfile | ✅ provider/profile/model/role 全部记录 |
| 7 | fallback 全部标记 test-only | ✅ 4 fallback sites labeled |
| 8 | build/tests/TS filter/E2E TS 通过 | ✅ |

## 残余风险

1. **LLM resolver 仍是 legacy.direct**：provider/profile 未参与真实多服务商配置选择。需后续配置目录治理任务升级。
2. **全量前端 TS 未清零**：chat 模块有 202 pre-existing errors，与本 ADR 无关。
3. **E2E 未在运行中服务上执行**：Playwright 测试代码已更正但未在真实服务上跑。需 `build-and-up.ps1` + `npm run` 后验证。
4. **`external/github.hyfree.GM`** 卫生项未混入本轮。
