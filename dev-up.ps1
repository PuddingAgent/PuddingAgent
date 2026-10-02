# 根目录转发 shim：dev-up 脚本本体已归位到 Tools\Dev\dev-up.ps1（2026-10-02）。
# 保留 .\dev-up.ps1 这一历史入口。
$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "Tools\Dev\dev-up.ps1") @args
exit $LASTEXITCODE
