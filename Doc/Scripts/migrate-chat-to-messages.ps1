<#
.SYNOPSIS
回填旧 ChatMessages 到 ADR-013 新 Messages 表。

.DESCRIPTION
- 源库：data/pudding_platform.db（main.ChatMessages）
- 目标库：data/pudding_memory.db（memorydb.Sessions / memorydb.Messages）
- 依赖 sqlite3 CLI（可通过 winget install sqlite.sqlite 安装）

.NOTES
该脚本不接入主构建流程，按需手工执行。
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir '..\..')

$platformDb = Join-Path $repoRoot 'data\pudding_platform.db'
$memoryDb = Join-Path $repoRoot 'data\pudding_memory.db'
$sqlTemplate = Join-Path $scriptDir 'migrate-chat-to-messages.sql'

if (-not (Test-Path $platformDb)) {
    throw "未找到源数据库: $platformDb"
}

if (-not (Test-Path $memoryDb)) {
    throw "未找到目标数据库: $memoryDb"
}

if (-not (Test-Path $sqlTemplate)) {
    throw "未找到 SQL 模板: $sqlTemplate"
}

$sqlite = Get-Command sqlite3 -ErrorAction SilentlyContinue
if (-not $sqlite) {
    throw "sqlite3 CLI 未找到。请先安装：winget install sqlite.sqlite"
}

$templateText = Get-Content -Path $sqlTemplate -Raw -Encoding UTF8
$escapedMemoryDb = $memoryDb.ToString().Replace("'", "''")
$sqlText = $templateText.Replace('__MEMORY_DB_PATH__', $escapedMemoryDb)

$tempSqlPath = Join-Path ([System.IO.Path]::GetTempPath()) ("migrate-chat-to-messages-{0}.sql" -f [Guid]::NewGuid().ToString('N'))

try {
    Set-Content -Path $tempSqlPath -Value $sqlText -Encoding UTF8 -NoNewline

    Push-Location $repoRoot
    try {
        & $sqlite.Path $platformDb ".read $tempSqlPath"
        if ($LASTEXITCODE -ne 0) {
            throw "sqlite3 执行失败，退出码: $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }

    Write-Host "回填完成：$platformDb -> $memoryDb" -ForegroundColor Green
}
finally {
    if (Test-Path $tempSqlPath) {
        Remove-Item -Path $tempSqlPath -Force -ErrorAction SilentlyContinue
    }
}
