# QA-2026-05-20 ADR-023 可观测性闭环与E2E基线

> 状态：REVIEWED
> 日期：2026-05-20
> 范围：Runtime Timeline、Admin Diagnostics、Debug Mode、Playwright E2E、Docker Smoke

## 完成清单

| 任务 | 状态 | 验证 |
|------|------|------|
| Phase 1: 诊断 DTO + Timeline 聚合 | ✅ | 0 error build, 4 API endpoints |
| Phase 2: Admin Diagnostics UI | ✅ | 3 页面 (Overview, Timeline, SubAgent Runs), 0 TS error |
| Phase 3: 前端 Debug Mode | ✅ | `window.__PUDDING_DEBUG__`, 6 data-testid, 0 TS error |
| Phase 4: Playwright E2E 基线 | ✅ | chat-smoke.spec.ts, evidence helper, tsc 0 error |
| Phase 5: Docker Smoke + QA | ✅ | run-docker-smoke.ps1, QA report |

## 构建验证

- PuddingCore: 0 error
- PuddingPlatform: 0 error
- PuddingRuntime: 0 error
- PuddingAgent: 0 error
- Frontend: 0 TS error (diagnostics pages)

## 残余风险

1. Playwright E2E 需要运行中的服务实例才能执行
2. WebApiTests 文件锁问题需后续独立输出目录方案
3. 前端 debug API 的 session state 读取需要后续接入真实状态管理

## 验证命令

```powershell
# 后端构建
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo

# 前端类型检查
cd Source\PuddingPlatformAdmin && npx tsc --noEmit

# Docker 冒烟
.\build-and-up.ps1
.\TestScripts\e2e\run-docker-smoke.ps1

# Playwright E2E (需要运行中的服务)
cd TestScripts\e2e && npx playwright test
```
