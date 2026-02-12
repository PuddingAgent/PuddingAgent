$file = 'Source\PuddingRuntime\Services\AgentExecutionService.cs'
$content = [System.IO.File]::ReadAllText($file)

# Fix the type names in RecordTokenUsageAsync
$content = $content.Replace(
    'IReadOnlyList<LlmToolCall>? toolCalls',
    'IReadOnlyList<ToolCall>? toolCalls')

$content = $content.Replace(
    'List<AccumulatedToolCall>? accumulatedToolCalls',
    'List<AccumulatedToolCall>? accumulatedToolCalls')

[System.IO.File]::WriteAllText($file, $content)
Write-Host "Fixed types"
