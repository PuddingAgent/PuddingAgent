$file = 'Source\PuddingAgent\Program.cs'
$content = [System.IO.File]::ReadAllText($file)

$content = $content.Replace(
    'builder.Services.AddScoped<CacheDiagnosticsService>();',
    'builder.Services.AddScoped<CacheDiagnosticsService>();
builder.Services.AddScoped<TokenCostService>();')

[System.IO.File]::WriteAllText($file, $content)
Write-Host "DI registered"
