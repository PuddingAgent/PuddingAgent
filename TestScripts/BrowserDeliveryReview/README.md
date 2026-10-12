# Browser delivery review regressions

这些测试断言要求的正确行为。在复核基线 `c5869a2b2c4d4353747d44ded819af83a23589a0` 上，11 条用例全部失败；这是拒收证据，**不是**允许发布的基线。

`Review.targets` 仅在显式传入 MSBuild 参数时将三个文件编译到对应的既有测试项目；不修改生产代码或项目默认编译项。修复时应将用例迁入各项目常规测试并删除这份临时注入关系，避免重复编译。

从仓库根目录在 PowerShell 串行运行（首次含 restore）：

```powershell
$reviewTargets = (Resolve-Path TestScripts/BrowserDeliveryReview/Review.targets).Path
$reviewArtifacts = Join-Path (Get-Location) 'temp/build/recovery/browser-review'
$reviewResults = Join-Path (Get-Location) 'temp/test-out/browser-review'
$reviewProjects = @(
  'Source/Pudding.DesktopServiceTests/Pudding.DesktopServiceTests.csproj',
  'Source/Pudding.DesktopConnectionTests/Pudding.DesktopConnectionTests.csproj',
  'Tests/PuddingDesktop.Tests/PuddingDesktop.Tests.csproj'
)
foreach ($reviewProject in $reviewProjects) {
  $reviewName = [IO.Path]::GetFileNameWithoutExtension($reviewProject)
  dotnet test $reviewProject --artifacts-path $reviewArtifacts --nologo `
    "-p:CustomAfterMicrosoftCommonTargets=$reviewTargets" -p:CollectCoverage=false `
    "-p:CoverletOutput=$reviewResults/coverage-$reviewName" `
    --filter 'FullyQualifiedName~BrowserDeliveryReview' `
    --logger "trx;LogFileName=$reviewName.trx" --results-directory $reviewResults
  Write-Output "$reviewName exit=$LASTEXITCODE"
}
```

预期基线失败数：Service 4、Connection 4、Bridge 3。修复后必须全部通过，并在常规套件增加合法授权的成功用例。

完成上述构建后，可复现本轮的既有对照测试（分别 4 条、6 条通过）：

```powershell
dotnet test $reviewProjects[0] --artifacts-path $reviewArtifacts --no-build --no-restore `
  -p:CollectCoverage=false "-p:CoverletOutput=$reviewResults/coverage-control-service" `
  --filter 'FullyQualifiedName~BrowserRevalidateBeforeTouchingTests|FullyQualifiedName~BrowserLeaseLifecycleTests|FullyQualifiedName~BrowserAdmissionGateTests' `
  --logger 'trx;LogFileName=service-existing-control.trx' --results-directory $reviewResults
dotnet test $reviewProjects[1] --artifacts-path $reviewArtifacts --no-build --no-restore `
  -p:CollectCoverage=false "-p:CoverletOutput=$reviewResults/coverage-control-connection" `
  --filter 'FullyQualifiedName~BrowserBindingDecodeTests' `
  --logger 'trx;LogFileName=connection-existing-control.trx' --results-directory $reviewResults
```

注意：Bridge 的跨身份与并发测试采用旧 dispatcher fixture；准入修复后应升级为 **A 已授权、B 未授权** 的 fixture，明确断言首次成功、相同 A/operationId 并发执行恰好 1 次、B 被拒绝。不能靠一律拒绝导致 handler 调用 0 次来声称去重修复。任务绑定测试也应改用可信的 A 任务选择事件创建许可，保留拒绝无关 B 的断言。

这些用例使用假 driver 和实际 dispatcher/service/decoder，不能证明 WebView2、真实模型视觉传输或产品生命周期通过。

完整判定见 [复核报告](../../Docs/14_reports/浏览器交付代码复核与拒收依据-2026-10-12.md)，施工见 [修订方案](../../Docs/12_features/浏览器交付修订施工方案-2026-10-12.md)。
