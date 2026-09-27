param([switch]$SkipBuild, [switch]$SkipCoreIntegration)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    dotnet test Source/PuddingChatTests/PuddingChatTests.csproj --artifacts-path temp/build/native-chat --results-directory temp/test-out/native-chat --nologo -p:CollectCoverage=false
    if ($LASTEXITCODE -ne 0) { throw 'Chat component tests failed.' }
    if (-not $SkipBuild) {
        dotnet build Source/PuddingChat.WinUITests/PuddingChat.WinUITests.csproj -c Release --artifacts-path temp/build/native-chat --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Native component harness build failed.' }
    }
    $runRoot = Join-Path $repoRoot ('temp/test-out/native-chat-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $runRoot | Out-Null
    $report = Join-Path $runRoot 'ui.json'
    $exe = Join-Path $repoRoot 'temp/build/winui3/bin/PuddingChat.WinUITests/release_win-x64/PuddingChat.WinUITests.exe'
    $process = Start-Process -FilePath $exe -ArgumentList ('"' + $report + '"') -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Native component harness timed out.' }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw 'Native component harness failed.' }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $result.success) { throw "Native component harness failed: $report" }
    $result | ConvertTo-Json
    if (-not $SkipCoreIntegration) {
        dotnet test Tests/PuddingNativeChat.IntegrationTests/PuddingNativeChat.IntegrationTests.csproj -c Release --artifacts-path temp/build/native-chat-integration --nologo -p:CollectCoverage=false
        if ($LASTEXITCODE -ne 0) { throw 'Direct Core chat integration failed.' }
    }
}
finally { Pop-Location }
