$lines = Get-Content 'E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\ContextPipeline.cs'
# Remove lines 357-455 (0-indexed: 356-454)
$part1 = $lines[0..355]
$part2 = $lines[455..($lines.Count - 1)]
$result = $part1 + @('') + $part2
$result -join "`r`n" | Set-Content 'E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\ContextPipeline.cs' -Encoding UTF8
Write-Host "Done. Lines: $($lines.Count) -> $($result.Count)"
