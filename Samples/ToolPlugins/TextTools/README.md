# TextTools 示例

此项目只引用发布 SDK，无 Core/Host/Runtime 项目引用。运行 `plugin_catalog` 可找到 `sdkDirectory`。复制此目录到 Agent 工作目录后，用 PowerShell 执行：

```powershell
$sdk = '把 plugin_catalog 返回的 sdkDirectory 填在这里'
dotnet build ./TextTools.csproj -c Release -p:PuddingPluginSdkDirectory="$sdk" --artifacts-path ./artifacts
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
New-Item -ItemType Directory -Force ./package/bin | Out-Null
Copy-Item ./plugin.json ./package/plugin.json
Copy-Item ./artifacts/bin/TextTools/release/Sample.TextTools.dll ./package/bin/
Copy-Item ./artifacts/bin/TextTools/release/Sample.TextTools.deps.json ./package/bin/
Compress-Archive -Path ./package/* -DestinationPath ./text-tools.zip -Force
```

调用 `plugin_import` 的 path 为相对当前执行工作目录的 `text-tools.zip`，再按完整 contentHash 加载。清单请求 `workspace.read`，并声明 `failure_advice` Hook；加载时须传对应 approvedCapabilities 与 enableHooks。可删除 Hook 声明及代码，制作纯工具包。

`text_stats` 返回 Unicode 字符数与行数；`read_text` 经 Core 服务读取当前工作目录的 UTF-8 文件。完整合同见 SDK 目录中的 `AUTHORING.md`。
