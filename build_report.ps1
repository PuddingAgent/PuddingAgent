$ErrorActionPreference = 'Continue'
Set-Location 'E:\github\AgentNetworkPlan\PuddingAgent'
$output = & dotnet build Source/PuddingHost/PuddingHost.csproj Source/PuddingDesktop/PuddingDesktop.csproj Source/PuddingAgent/PuddingAgent.csproj --no-restore 2>&1 | Out-String
$exit = $LASTEXITCODE
($output -split "`r?`n" | Where-Object { $_ -ne '' } | Select-Object -Last 20) -join "`n"
Write-Output "=====BUILD_EXIT_CODE====="
Write-Output $exit
