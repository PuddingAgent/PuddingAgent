# 独立审计证据与复跑

对应 2026-09-11 `04-批次1独立审计与下一步.md`。这不是修复补丁；探针按应满足的合同断言，当前源码结果为 **10 失败**，原相关测试为 **91 通过**。测试源码均置于临时目录，未修改 Source/Tests 项目文件。

## 文件

- `audit-baseline.json`：审计 HEAD、文件 hash、源文件在本次验证期间未变的检查。
- `source-comparison.json`：与原规划 50 文件的比较（38 未变、10 变更、2 删除）。
- `test-results.json`：最终测试摘要；前端既有 23 项、usage 39 项、记忆 28 项、Host 1 项，以及独立 5+3+2 失败。
- `frontend-probes.json/.log`：修正测试 harness 后的最终前端结果；root StrictMode 的失败是调用次数 0，不是 React import 异常。
- `platform-usage.trx`：既有 usage 39 通过、新探针 3 失败。
- `runtime-memory.trx`、`runtime-memory-probes.trx`：既有记忆 28 通过、新探针 2 失败。
- `host-composition.trx`、`entry-compile.json`、`entry-build.log`、`typescript.log`：Host 1 通过、Compile 仅 Program.cs、入口构建 0 errors/49 warnings、tsc exit 0（空日志）。
- 三份 `audit-*.tsx` / `Audit*.cs`、`jest.audit.config.cjs`、`audit.tests.targets`：可复跑探针与临时配置。
- `board-receipts.json`：六个新任务和四个既有任务更新摘要，不包含 Access Token。

## 准备临时探针

在仓库根目录的 PowerShell 执行。所有路径可随仓库位置改变；不将产物放入 DataRoot。

```powershell
$auditRepo = (Get-Location).Path
$auditEvidence = Join-Path $auditRepo 'Docs/Reports/PuddingAgent-GLM-Optimization-2026-09-11/audit-evidence'
$auditTemp = Join-Path $auditRepo '.tmp-test-out/glm-audit-20260911'
$auditOut = Join-Path $auditRepo '.tmp-build/glm-audit-platform'
New-Item -ItemType Directory -Path $auditTemp -Force | Out-Null
@('audit-f01.test.tsx', 'AuditUsageConflictTests.cs', 'AuditMemoryContractTests.cs',
  'jest.audit.config.cjs', 'audit.tests.targets') | ForEach-Object {
    Copy-Item -LiteralPath (Join-Path $auditEvidence $_) -Destination (Join-Path $auditTemp $_)
}
```

## 前端

使用项目已安装依赖；本次没有更新 package 或 lockfile。

```powershell
Push-Location (Join-Path $auditRepo 'Source/PuddingPlatformAdmin')
node node_modules/jest/bin/jest.js --runInBand --config ../../.tmp-test-out/glm-audit-20260911/jest.audit.config.cjs --runTestsByPath ../../.tmp-test-out/glm-audit-20260911/audit-f01.test.tsx
node node_modules/jest/bin/jest.js --runInBand --runTestsByPath src/pages/chat/runtime/detailHydrationScheduler.test.ts src/pages/chat/hooks/__tests__/turnSurfaceStore.hydration.test.ts src/pages/chat/components/MessageRow.focus.test.tsx
node node_modules/typescript/bin/tsc --noEmit
Pop-Location
```

当前预期：新探针 5 失败；既有 23 通过；tsc exit 0。取消探针验证 hook 传给 API 的 signal；GLM 修复时还必须加 adapter→request 及真实可取消 transport 的测试。`reactStrictMode: true` 必须作为根选项，不能换成不重放根 effect 的嵌套 wrapper 来使断言失效。

## 后端（串行）

临时 targets 只向名称匹配的测试项目加入审计 Compile，不编辑 csproj。参数省略此 targets 时恢复原项目编译集合；构建产物保持在隔离目录。

```powershell
$auditTargets = Join-Path $auditTemp 'audit.tests.targets'
dotnet test Source/PuddingPlatformTests/PuddingPlatformTests.csproj --no-restore --nologo "-p:OutDir=$auditOut/" "-p:CustomAfterMicrosoftCommonTargets=$auditTargets" --filter 'FullyQualifiedName~TokenUsage|FullyQualifiedName~LlmGatewayUsage|FullyQualifiedName~AuditUsageConflict' --logger 'trx;LogFileName=platform-usage-rerun.trx' --results-directory $auditTemp
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --no-restore --nologo "-p:OutDir=$auditOut/" "-p:CustomAfterMicrosoftCommonTargets=$auditTargets" --filter 'FullyQualifiedName~SaveMemoryToolContract|FullyQualifiedName~MemoryToolsTests|FullyQualifiedName~AuditMemoryContract' --logger 'trx;LogFileName=runtime-memory-rerun.trx' --results-directory $auditTemp
dotnet test Tests/PuddingHost.Tests/PuddingHost.Tests.csproj --no-restore --nologo "-p:OutDir=$auditOut/" --filter 'FullyQualifiedName~Composition'
dotnet build Source/PuddingAgent/PuddingAgent.csproj --no-restore --nologo "-p:OutDir=$auditOut/"
dotnet msbuild Source/PuddingAgent/PuddingAgent.csproj -getItem:Compile
```

当前预期：平台 39 通过/3 失败；记忆 28 通过/2 失败；Host 1 通过；入口构建成功，Compile 仅 Program.cs。本次原始记忆测试与探针分两次执行，以上命令为等价合并筛选。

usage 探针使用临时 SQLite 文件、独立 context。`audit_guard` trigger 只验证无关 UNIQUE 异常是否被误吞，不允许加到产品数据库。记忆探针使用记录型替身，检查 Upsert 调用次数和收到的 Content；不宣称真实记忆库已产生这些坏数据。

新异常类型或接口形式可导致测试适配，但必须保留“冲突不能冒充成功、非法输入不进入写路径、取消到 transport”的验收语义。不能删除失败断言、降低预算约束或启用错误兼容分支来转绿。

## 边界

没有运行全部前后端测试、产品 HTTP E2E、真实 LLM、Desktop 生命周期或七日缓存验收。Jest 曾提示未退出句柄，随后退出，来源未定位。部署需要另外绑定新构建身份与 canonical 功能证据。
