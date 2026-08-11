$lines = Get-Content 'E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\ContextPipeline.cs'
$part1 = $lines[0..623]
$part2 = $lines[1474..($lines.Count-1)]
$placeholder = @(
    '',
    '        // ===============================================================',
    '        // Layer provider methods (L0-L6) extracted to ContextPipelineLayers.cs',
    '        // See ContextPipelineLayers.cs for all layer-building methods.',
    '        // ===============================================================',
    ''
)
$result = $part1 + $placeholder + $part2
$result -join "`r`n" | Set-Content 'E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\ContextPipeline.cs' -Encoding UTF8
Write-Host "Done. Lines: $($lines.Count) -> $($result.Count)"
