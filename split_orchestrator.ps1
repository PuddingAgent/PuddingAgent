$lines = Get-Content 'E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\ContextPipeline.cs'

# Find the start of AssembleAsync and the start of the Layer placeholder comment (after orchestration methods, before utils)
$assembleStart = -1
$afterOrchStart = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'public async Task<ContextAssemblyResult> AssembleAsync') {
        $assembleStart = $i
    }
    if ($assembleStart -gt 0 -and $lines[$i] -match 'Layer provider methods') {
        $afterOrchStart = $i
        break
    }
}

Write-Host "AssembleAsync starts at line: $assembleStart"
Write-Host "After orchestration at line: $afterOrchStart"

$part1 = $lines[0..($assembleStart - 1)]
$part2 = $lines[$afterOrchStart..($lines.Count - 1)]
$result = $part1 + $part2
$result -join "`r`n" | Set-Content 'E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\ContextPipeline.cs' -Encoding UTF8
Write-Host "Done. Lines: $($lines.Count) -> $($result.Count)"
