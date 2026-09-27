param([switch]$Demo)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$output = Join-Path ([IO.Path]::GetTempPath()) ('PuddingWinUiPreview-' + [guid]::NewGuid().ToString('N'))
Push-Location $repoRoot
try {
    dotnet publish Source/PuddingDesktop/PuddingDesktop.csproj -c Release --artifacts-path temp/build/desktop-kernel --nologo -o $output
    if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed.' }
    $arguments = @('--state-root', ('"' + (Join-Path $output 'preview-state') + '"'))
    if ($Demo) { $arguments += '--demo' }
    Start-Process -FilePath (Join-Path $output 'PuddingDesktop.exe') -ArgumentList $arguments -WindowStyle Hidden
    Write-Output "Preview: $output"
    Write-Output 'Core DLL runs in Desktop. Default data is LocalAppData/Pudding/DesktopData; -Demo skips automatic startup.'
}
finally { Pop-Location }
