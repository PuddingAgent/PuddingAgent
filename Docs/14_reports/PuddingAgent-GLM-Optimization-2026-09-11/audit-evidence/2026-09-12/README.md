# 2026-09-12 复核与看板回读

主报告：../../06-实施进度复核与看板状态修订-2026-09-12.md。

- board-receipts.json：7张任务状态、描述、版本、评论与评价回读，仅含本批卡片。
- test-summary.json / platform.trx / runtime.trx / frontend.json：48 + 40 + 28 = 116项通过，含原10个独立探针。
- review-snapshot.json：收口时工作树坐标与hash，采样发生在测试之后；C01 WIP期间发生变化，不能把这些hash冒充精确测试制品hash。
- C#探针更名避免与GLM新增的同名类冲突，断言未放宽。F01探针仍为原文件。

## 复跑位置

将本目录的测试文件、audit.tests.targets、jest.audit.config.cjs复制到仓库根下两级目录，例如 .tmp-test-out/glm-board-20260912/。不要在Docs深层路径原位运行；相对引用按临时目录深度编写。

在仓库根用PowerShell执行后端测试，两个命令串行运行：

```powershell
$auditDir = Join-Path (Get-Location) '.tmp-test-out/glm-board-20260912'
$auditOut = (Join-Path (Get-Location) '.tmp-build/glm-board-20260912') + '/'
dotnet test Source/PuddingPlatformTests/PuddingPlatformTests.csproj --no-restore --nologo "-p:OutDir=$auditOut" "-p:CustomAfterMicrosoftCommonTargets=$auditDir/audit.tests.targets" --filter 'FullyQualifiedName~TokenUsage|FullyQualifiedName~LlmGatewayUsage|FullyQualifiedName~IndependentUsageProbe12|FullyQualifiedName~AuditUsageConflict|FullyQualifiedName~UsageRequestAttribution' --logger 'trx;LogFileName=platform.trx' --results-directory $auditDir
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --no-restore --nologo "-p:OutDir=$auditOut" "-p:CustomAfterMicrosoftCommonTargets=$auditDir/audit.tests.targets" --filter 'FullyQualifiedName~SaveMemoryToolContract|FullyQualifiedName~SaveMemoryTypeContract|FullyQualifiedName~MemoryToolsTests|FullyQualifiedName~IndependentMemoryProbe12|FullyQualifiedName~RequestAttributionWiring' --logger 'trx;LogFileName=runtime.trx' --results-directory $auditDir
```

在 Source/PuddingPlatformAdmin 下执行前端：

```powershell
node node_modules/jest/bin/jest.js --runInBand --config ../../.tmp-test-out/glm-board-20260912/jest.audit.config.cjs --runTestsByPath ../../.tmp-test-out/glm-board-20260912/audit-f01.test.tsx src/pages/chat/runtime/detailHydrationScheduler.test.ts src/pages/chat/hooks/__tests__/turnSurfaceStore.hydration.test.ts src/pages/chat/components/MessageRow.focus.test.tsx --json --outputFile ../../.tmp-test-out/glm-board-20260912/frontend.json
```

新工作树或后续提交可能改变结果。不要与其他构建共享obj并发运行。以上只复跑本轮定向范围；C01新WIP、tsc和产品验收另行完成。
