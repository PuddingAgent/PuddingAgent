$ErrorActionPreference = "Stop"
$base = "http://localhost:5000"

Write-Host "1. Testing Login..."
$loginBody = @{username="admin";password="Admin@123456"} | ConvertTo-Json
try {
    $resp = Invoke-WebRequest -Uri "$base/api/login/account" -Method POST -Body $loginBody -ContentType "application/json" -UseBasicParsing
    Write-Host "Login Raw Content: $($resp.Content)"
    $data = $resp.Content | ConvertFrom-Json
    $token = $data.token
    if (!$token) { 
        Write-Host "Token is missing in response!"
        exit 1
    }
    Write-Host "Login OK. Token length: $($token.Length)"
} catch {
    Write-Host "Login Failed: $($_.Exception.Message)"
    exit 1
}

Write-Host "2. Testing GET /api/workspaces..."
$headers = @{ Authorization = "Bearer $token" }
try {
    $resp = Invoke-WebRequest -Uri "$base/api/workspaces" -Headers $headers -UseBasicParsing
    Write-Host "Workspaces: $($resp.Content)"
} catch {
    Write-Host "Workspaces Failed: $($_.Exception.Message)"
    if ($_.Exception.Response) {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        Write-Host "Response Body: $($reader.ReadToEnd())"
    }
}
