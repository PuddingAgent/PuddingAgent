$ErrorActionPreference = "Stop"
$base = "http://localhost:5000"

Write-Host "1. Testing Login..."
$loginBody = @{username="admin";password="Admin@123456"} | ConvertTo-Json
try {
    $resp = Invoke-WebRequest -Uri "$base/api/login/account" -Method POST -Body $loginBody -ContentType "application/json" -UseBasicParsing
    $token = ($resp.Content | ConvertFrom-Json).token
    Write-Host "Login OK. Token length: $($token.Length)"
} catch {
    Write-Host "Login Failed: $(.Exception.Message)"
    if ($_.Exception.Response) {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        Write-Host "Response: $($reader.ReadToEnd())"
    }
    exit 1
}

Write-Host "2. Testing GET /api/workspaces..."
$headers = @{ Authorization = "Bearer $token" }
try {
    $resp = Invoke-WebRequest -Uri "$base/api/workspaces" -Headers $headers -UseBasicParsing
    Write-Host "Workspaces: $($resp.Content)"
} catch {
    Write-Host "Workspaces Failed: $(.Exception.Message)"
}
