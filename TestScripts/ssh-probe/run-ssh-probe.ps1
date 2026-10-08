<#
.SYNOPSIS
    PuddingSsh A1/S1 probe runner: controlled OpenSSH (WSL Ubuntu) + real-protocol probe + server-side auth evidence.

.DESCRIPTION
    Does four things and writes raw output under temp/test-out/ssh/ (repo rule: test output only in temp):
      1. prepares a DISPOSABLE sshd fixture in WSL (/tmp/pudding-ssh-probe, never the system sshd);
      2. copies the throwaway keys to a Windows test directory and tightens their ACLs;
      3. runs PuddingSsh.Probe scenario by scenario, using its exit code as the verdict;
      4. cross-checks with the sshd log that host key rejection happens BEFORE authentication
         (a rejected scenario must not produce an "Accepted publickey" line).

    This script executes the A1 acceptance criteria of
    Docs/12_features/SSH工具组件化设计与实施方案-2026-10-08.md.
    It does NOT compose Core, does NOT touch DataRoot and never uses a production host.

    NOTE: this file must stay UTF-8 WITH BOM (non-ASCII text + Windows PowerShell 5.1 decoding);
    TestScripts/check-script-encodings.ps1 is the gate that enforces it.

.EXAMPLE
    powershell -File TestScripts/ssh-probe/run-ssh-probe.ps1
    powershell -File TestScripts/ssh-probe/run-ssh-probe.ps1 -Port 22123 -KeepServer
#>
[CmdletBinding()]
param(
    [int]$Port = 22122,
    [string]$FixtureRoot = '/tmp/pudding-ssh-probe',
    [string]$ProbeExe = 'temp/build/ssh/bin/PuddingSsh.Probe/debug/PuddingSsh.Probe.exe',
    [string]$OutDir = 'temp/test-out/ssh',
    [int]$OutputBudgetBytes = 65536,
    [int]$ConnectTimeoutSeconds = 3,
    [switch]$KeepServer,
    [switch]$SkipSetup
)

$ErrorActionPreference = 'Stop'

# Native commands here (wsl.exe) always write a localhost-proxy warning to stderr. Under
# $ErrorActionPreference='Stop' PowerShell 5.1 turns the first stderr line into a terminating
# NativeCommandError, so every native call goes through this wrapper: stderr is captured as
# ErrorRecords (never as strings), and the exit code is checked explicitly.
function Invoke-NativeCommand([string]$FilePath, [string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $raw = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    return @{
        Exit = $exitCode
        Raw  = @($raw)
        Text = @($raw | Where-Object { $_ -is [string] })
    }
}

function Invoke-Wsl([string[]]$Arguments) {
    $result = Invoke-NativeCommand 'wsl.exe' $Arguments
    if ($result.Exit -ne 0) {
        $joinedArguments = $Arguments -join ' '
        $joinedOutput = (@($result.Raw) | ForEach-Object { "$_" }) -join ' | '
        throw "wsl command failed (exit $($result.Exit)): wsl $joinedArguments -- $joinedOutput"
    }

    return $result.Text
}

function ConvertTo-WslPath([string]$Path) {
    $full = (Resolve-Path -LiteralPath $Path).Path
    $drive = $full.Substring(0, 1).ToLowerInvariant()
    return "/mnt/$drive" + ($full.Substring(2) -replace '\\', '/')
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repo

$outPath = Join-Path $repo $OutDir
New-Item -ItemType Directory -Force -Path $outPath | Out-Null
$transcript = Join-Path $outPath 'probe-run.log'
$probeExePath = Join-Path $repo $ProbeExe

if (-not (Test-Path $probeExePath)) {
    throw "probe not built: $ProbeExe -- run: dotnet build Source\PuddingSsh.Probe\PuddingSsh.Probe.csproj --artifacts-path temp\build\ssh"
}

function Write-Log([string]$Message) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line
    Add-Content -Path $transcript -Value $line
}

$failures = New-Object System.Collections.ArrayList
$summary = [ordered]@{}

function Add-Failure([string]$Message) {
    [void]$failures.Add($Message)
    Write-Log "FAILED $Message"
}

function Get-AcceptedCount {
    $lines = @(Invoke-Wsl @('-u', 'root', '-e', 'bash', '-lc', "grep -c 'Accepted publickey' $FixtureRoot/sshd.log 2>/dev/null || true"))
    $number = $lines | Where-Object { $_ -match '^\s*\d+\s*$' } | Select-Object -Last 1
    if (-not $number) { return 0 }
    return [int]$number.Trim()
}

try {
    # Kill any leftover fixture sshd first: a stale server would still own the OLD host key,
    # and the fingerprint cross-check below would (correctly) fail. The [s] bracket trick keeps
    # pkill from matching its own command line.
    Invoke-Wsl @('-u', 'root', '-e', 'bash', '-lc',
        "pkill -f '[s]shd -f $FixtureRoot/sshd_config' >/dev/null 2>&1; sleep 1; echo PRECLEAN_OK") | Out-Null

    if (-not $SkipSetup) {
        $setupScript = ConvertTo-WslPath (Join-Path $PSScriptRoot 'setup-wsl-sshd.sh')
        $fixture = Invoke-Wsl @('-u', 'root', '-e', 'bash', $setupScript, "$Port")
        $fixture | Where-Object { $_ -match '=' } | ForEach-Object { Write-Log "fixture $_" }
    }

    # Start the fixture sshd (daemonized inside WSL) with its log inside the fixture directory,
    # so authentication evidence can be read back per scenario.
    Invoke-Wsl @('-u', 'root', '-e', 'bash', '-lc',
        "nohup /usr/sbin/sshd -f $FixtureRoot/sshd_config -E $FixtureRoot/sshd.log >/dev/null 2>&1 & sleep 1; ss -lnt | grep -q ':$Port ' && echo LISTENING") | Out-Null
    Write-Log "fixture sshd listening on 127.0.0.1:$Port (root=$FixtureRoot)"

    # Throwaway keys to the Windows side; Windows private-key semantics require tight ACLs.
    # The directory is recreated first: a previous run left read-only ACLs on those files,
    # so the ACLs are reset before deletion.
    $keyDir = Join-Path $repo 'temp/build/ssh/probe-keys'
    if (Test-Path $keyDir) {
        icacls $keyDir /reset /T /C /Q | Out-Null
        Remove-Item -Recurse -Force $keyDir -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Force -Path $keyDir | Out-Null
    Invoke-Wsl @('-u', 'root', '-e', 'bash', '-lc',
        "cp $FixtureRoot/id_ed25519 $FixtureRoot/id_ed25519_encrypted $FixtureRoot/authorized_keys $(ConvertTo-WslPath $keyDir)/") | Out-Null
    foreach ($name in @('id_ed25519', 'id_ed25519_encrypted')) {
        icacls (Join-Path $keyDir $name) /inheritance:r /grant:r "$($env:USERNAME):R" | Out-Null
    }

    $fingerprint = ((Invoke-Wsl @('-u', 'root', '-e', 'bash', '-lc',
        "ssh-keygen -lf $FixtureRoot/hostkey_ed25519.pub -E sha256 | cut -d' ' -f2")) -join '').Trim()
    $serverVersion = ((Invoke-Wsl @('-u', 'root', '-e', 'bash', '-lc',
        '/usr/sbin/sshd -V 2>&1 | head -1')) -join '').Trim()
    $osVersion = ((Invoke-Wsl @('-u', 'root', '-e', 'bash', '-lc',
        '. /etc/os-release; echo $PRETTY_NAME')) -join '').Trim()

    Write-Log "server: $osVersion / $serverVersion"
    Write-Log "host key ed25519 = $fingerprint"

    $identityPath = Join-Path $keyDir 'id_ed25519'

    # Decoy fingerprint: flip the middle character of the expected one (used by hostkey/reject).
    $fingerprintB64 = $fingerprint -replace '^SHA256:', ''
    $middle = [int]($fingerprintB64.Length / 2)
    $replacement = 'A'
    if ($fingerprintB64[$middle] -eq 'A') { $replacement = 'B' }
    $decoy = 'SHA256:' + $fingerprintB64.Substring(0, $middle) + $replacement + $fingerprintB64.Substring($middle + 1)

    $summary['server.version'] = $serverVersion
    $summary['server.os'] = $osVersion
    $summary['hostkey.sha256'] = $fingerprint

    function Invoke-Scenario([string]$Name, [string[]]$ProbeArgs) {
        $logFile = Join-Path $outPath "probe-$Name.log"
        Write-Log "--- scenario $Name"
        $before = Get-AcceptedCount
        $result = Invoke-NativeCommand $probeExePath $ProbeArgs
        $after = Get-AcceptedCount
        $output = @($result.Text)
        $output | Tee-Object -FilePath $logFile | ForEach-Object { Write-Host "    $_" }
        Add-Content -Path $transcript -Value ($output -join [Environment]::NewLine)

        $delta = $after - $before
        Write-Log "scenario=$Name exit=$($result.Exit) accepted_publickey_delta=$delta"
        $summary["$Name.exit"] = $result.Exit
        $summary["$Name.auth_delta"] = $delta
        if ($result.Exit -ne 0) { Add-Failure "scenario $Name exit=$($result.Exit)" }
        if (-not ($output -match '^SUMMARY failures=0$')) { Add-Failure "scenario $Name has failing probe assertions" }

        return @{ Exit = $result.Exit; AuthDelta = $delta; Output = $output }
    }

    $common = @('--host', '127.0.0.1', '--port', "$Port", '--user', 'root', '--identity', $identityPath,
        '--fingerprint', $fingerprint, '--budget', "$OutputBudgetBytes", '--timeout', '60',
        '--connect-timeout', "$ConnectTimeoutSeconds", '--remote-tmp', $FixtureRoot)

    # 1) Observe-then-deny: the presented key is recorded, the connection is refused, no auth.
    $r = Invoke-Scenario 'fingerprint' (@('fingerprint') + $common)
    if ($r.AuthDelta -ne 0) { Add-Failure "fingerprint: rejection still produced a successful authentication (delta=$($r.AuthDelta))" }

    # 2) Correct fingerprint: connect and authenticate exactly once.
    $r = Invoke-Scenario 'hostkey-accept' (@('hostkey', '--expect', 'accept') + $common)
    if ($r.AuthDelta -ne 1) { Add-Failure "hostkey-accept: expected exactly 1 successful authentication, got $($r.AuthDelta)" }

    # 3) Wrong fingerprint: rejected pre-authentication. The decoy is appended AFTER $common
    #    because the last occurrence of --fingerprint wins in the probe's parser.
    $r = Invoke-Scenario 'hostkey-reject' (@('hostkey', '--expect', 'reject') + $common + @('--fingerprint', $decoy))
    if ($r.AuthDelta -ne 0) { Add-Failure "hostkey-reject: mismatched fingerprint still authenticated (delta=$($r.AuthDelta))" }

    # 4) Encrypted private key: explicit passphrase-required error.
    $r = Invoke-Scenario 'encrypted' (@('encrypted') + $common)
    if ($r.AuthDelta -ne 0) { Add-Failure "encrypted: encrypted key must not connect (delta=$($r.AuthDelta))" }

    # 5) Connect deadline against an unreachable address.
    $r = Invoke-Scenario 'deadline' (@('deadline') + $common)

    # 6) Cancellation plus remote evidence.
    $r = Invoke-Scenario 'cancel' (@('cancel') + $common)

    # 7) Dual-stream throughput and memory.
    $r = Invoke-Scenario 'throughput' (@('throughput', '--timeout', '180') + $common)

    # Cross-check: the fingerprint the probe observed must equal OpenSSH's own.
    $observedLine = Get-Content (Join-Path $outPath 'probe-fingerprint.log') |
        Select-String -Pattern 'sha256=(SHA256:\S+)' | Select-Object -First 1
    $observed = $observedLine.Matches.Groups[1].Value
    Write-Log "probe observed fingerprint = $observed"
    $summary['fingerprint.probe'] = $observed
    if ($observed -ne $fingerprint) {
        Add-Failure "fingerprint mismatch: openssh=$fingerprint probe=$observed"
    }

    Write-Log '--- sshd log evidence (tail) ---'
    $evidence = Invoke-Wsl @('-u', 'root', '-e', 'bash', '-lc',
        "grep -E 'Accepted publickey|Connection closed by|Unable to negotiate|Bad' $FixtureRoot/sshd.log | tail -20")
    $evidence | ForEach-Object { Write-Log "    $_" }
}
finally {
    if (-not $KeepServer) {
        try {
            Invoke-Wsl @('-u', 'root', '-e', 'bash', '-lc',
                "pkill -f '[s]shd -f $FixtureRoot/sshd_config' >/dev/null 2>&1; sleep 1; echo STOPPED") | Out-Null
            Write-Log 'fixture sshd stopped'
        }
        catch {
            Write-Log "teardown warning: $($_.Exception.Message)"
        }
    }
}

Write-Log '===== SUMMARY ====='
foreach ($key in $summary.Keys) { Write-Log ("{0} = {1}" -f $key, $summary[$key]) }
Write-Log "failures = $($failures.Count)"

if ($failures.Count -gt 0) {
    exit 1
}

exit 0
