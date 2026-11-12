param(
    [Parameter(Mandatory = $true)][string]$CredentialFile,
    [string]$ExpectedTeamId,
    [switch]$TestSocketMode
)

$ErrorActionPreference = 'Stop'
# Never pass tokens in command arguments, environment variables, errors or output.
try {
    $credentials = Get-Content -LiteralPath $CredentialFile -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($credentials.botToken) -or [string]::IsNullOrWhiteSpace($credentials.appToken)
        -or $credentials.botToken -match '\s' -or $credentials.appToken -match '\s') {
        throw 'Invalid credential values'
    }
} catch {
    Write-Output '{"ok":false,"code":"credential_file_invalid"}'
    exit 1
}
try {
    $identity = Invoke-RestMethod -Uri 'https://slack.com/api/auth.test' -Method Post -TimeoutSec 20 `
        -Headers @{Authorization = "Bearer $($credentials.botToken)"}
    if (-not $identity.ok) {
        Write-Output '{"ok":false,"code":"auth_test_rejected"}'
        exit 1
    }
    $matched = -not $ExpectedTeamId -or $identity.team_id -ceq $ExpectedTeamId
    $result=[ordered]@{ok=$matched; teamId=$identity.team_id; teamName=$identity.team; botUserId=$identity.user_id;
        code= $(if ($matched) {'bot_identity_verified'} else {'team_id_mismatch'});
        scope='auth.test only; no messages sent; Socket Mode and delivery remain unverified'}
    if ($matched -and $TestSocketMode) {
        $socket = Invoke-RestMethod -Uri 'https://slack.com/api/apps.connections.open' -Method Post -TimeoutSec 20 `
            -Headers @{Authorization = "Bearer $($credentials.appToken)"}
        $result.ok = [bool]$socket.ok
        $result.code = if ($socket.ok) {'bot_and_app_credentials_verified'} elseif ($socket.error -match '^[a-z_]{1,79}$') {$socket.error} else {'socket_open_failed'}
        $result.scope = 'auth.test + apps.connections.open; WebSocket not connected; no messages sent; delivery unverified'
    }
    $result | ConvertTo-Json
    if (-not $result.ok) { exit 1 }
    if (-not $matched) { exit 1 }
} catch {
    Write-Output '{"ok":false,"code":"credential_probe_transport_failed"}'
    exit 1
} finally {
    $credentials = $null
}
