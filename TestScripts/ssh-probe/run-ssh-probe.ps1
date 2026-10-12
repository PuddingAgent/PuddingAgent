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
    # 目标可能还不存在（例如要交给 WSL 去创建的 known_hosts 变体），所以不能只靠 Resolve-Path。
    if (Test-Path -LiteralPath $Path) {
        $full = (Resolve-Path -LiteralPath $Path).Path
    }
    else {
        $rooted = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $repo $Path }
        $full = [System.IO.Path]::GetFullPath($rooted)
    }

    $drive = $full.Substring(0, 1).ToLowerInvariant()
    return "/mnt/$drive" + ($full.Substring(2) -replace '\\', '/')
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repo

$outPath = Join-Path $repo $OutDir
$transcript = Join-Path $outPath 'probe-run.log'
$probeExePath = Join-Path $repo $ProbeExe

# temp/ 是共享临时区：别的会话可能随时按仓库卫生约定清空 temp\test-out\*。
# 因此每次写入前重建目录，而不是只在脚本开头创建一次（否则一次并发清理就会打断整轮探针）。
function Initialize-OutDir {
    if (-not (Test-Path -LiteralPath $outPath)) {
        New-Item -ItemType Directory -Force -Path $outPath | Out-Null
    }
}

Initialize-OutDir

if (-not (Test-Path $probeExePath)) {
    throw "probe not built: $ProbeExe -- run: dotnet build Source\PuddingSsh.Probe\PuddingSsh.Probe.csproj --artifacts-path temp\build\ssh"
}

function Write-Log([string]$Message) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line
    Initialize-OutDir
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

# A4：客户端密钥指纹也来自服务端日志，用于证明「哪个 .ssh 目录的密钥被真的用了」。
function Get-AcceptedFingerprints {
    $lines = @(Invoke-Wsl @('-u', 'root', '-e', 'bash', '-lc', "grep 'Accepted publickey' $FixtureRoot/sshd.log 2>/dev/null || true"))
    return @($lines | Where-Object { $_ -is [string] } | ForEach-Object {
        if ($_ -match 'SHA256:(\S+)') { $Matches[1] }
    })
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

    # --- A4 夹具：两类 .ssh 形态的目录 + 撤销/换 key 的 known_hosts ---
    # 上一轮给两个目录里的私钥收紧过 ACL（只读），所以先复位 ACL 再重建目录，否则 WSL 写不进去。
    $scopeOut = Join-Path $repo 'temp/build/ssh/scope'
    if (Test-Path $scopeOut) {
        icacls $scopeOut /reset /T /C /Q | Out-Null
        Remove-Item -Recurse -Force $scopeOut -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Force -Path $scopeOut | Out-Null
    $scopeScript = ConvertTo-WslPath (Join-Path $PSScriptRoot 'prepare-scope-dirs.sh')
    $scopeInfo = Invoke-Wsl @('-u', 'root', '-e', 'bash', $scopeScript, (ConvertTo-WslPath $scopeOut), "$Port")
    $scopeInfo | Where-Object { $_ -match '=' } | ForEach-Object { Write-Log "scope $_" }

    $scopeA = Join-Path $scopeOut 'scope-a'
    $scopeB = Join-Path $scopeOut 'scope-b'
    $rotatedKnownHosts = Join-Path $scopeOut 'scope-b-rotated/known_hosts'
    foreach ($key in @((Join-Path $scopeA 'id_ed25519'), (Join-Path $scopeB 'id_ed25519'))) {
        icacls $key /inheritance:r /grant:r "$($env:USERNAME):R" | Out-Null
    }

    function Get-ScopeValue([string]$line) {
        $match = $scopeInfo | Where-Object { $_ -match "^$line=" } | Select-Object -First 1
        if (-not $match) { return '' }
        return ($match -replace "^$line=", '').Trim()
    }

    $scopeAFp = Get-ScopeValue 'scope_a_client_fp'
    $scopeBFp = Get-ScopeValue 'scope_b_client_fp'

    # 撤销记录：真实 ssh-keyscan 记录 + @revoked 标记（指向同一把主机公钥）。
    $revokedKnownHosts = Join-Path $scopeA 'known_hosts.revoked'
    Get-Content (Join-Path $scopeA 'known_hosts') | ForEach-Object {
        if ($_ -match 'ssh-ed25519' -and $_ -match '\[127\.0\.0\.1\]:') { "@revoked $_" } else { $_ }
    } | Set-Content -Path $revokedKnownHosts -Encoding ASCII

    # 哈希主机记录：由 prepare-scope-dirs.sh 用 OpenSSH 自己的 `ssh-keygen -H` 生成（`|1|salt|hash`）。
    $hashedKnownHosts = Join-Path $scopeA 'known_hosts.hashed'
    if (-not (Select-String -Path $hashedKnownHosts -Pattern '^\|1\|' -Quiet)) {
        Add-Failure "knownhosts-hashed: ssh-keygen -H did not produce a |1|salt|hash record"
    }

    $scopeCommon = @('--host', '127.0.0.1', '--port', "$Port", '--user', 'root',
        '--identity', (Join-Path $scopeA 'id_ed25519'), '--fingerprint', $fingerprint)

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
        $beforeFingerprints = @(Get-AcceptedFingerprints)
        $result = Invoke-NativeCommand $probeExePath $ProbeArgs
        $after = Get-AcceptedCount
        $afterFingerprints = @(Get-AcceptedFingerprints)
        $output = @($result.Text)
        Initialize-OutDir
        $output | Tee-Object -FilePath $logFile | ForEach-Object { Write-Host "    $_" }
        Add-Content -Path $transcript -Value ($output -join [Environment]::NewLine)

        $delta = $after - $before
        $newFingerprints = @($afterFingerprints | Select-Object -Skip $beforeFingerprints.Count)
        Write-Log "scenario=$Name exit=$($result.Exit) accepted_publickey_delta=$delta client_keys=$($newFingerprints -join ',')"
        $summary["$Name.exit"] = $result.Exit
        $summary["$Name.auth_delta"] = $delta
        if ($result.Exit -ne 0) { Add-Failure "scenario $Name exit=$($result.Exit)" }
        if (-not ($output -match '^SUMMARY failures=0$')) { Add-Failure "scenario $Name has failing probe assertions" }

        return @{ Exit = $result.Exit; AuthDelta = $delta; Output = $output; ClientKeys = $newFingerprints }
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

    # 8) Full chain through the ISshClient coordinator (A3): ssh_test + success + wrong pin + non-zero exit + invalid request.
    #    Expected successful authentications: test(1) + execute(1) + exit3(1) = 3;
    #    the wrong-pin and invalid-request operations must NOT authenticate.
    $r = Invoke-Scenario 'coordinator' (@('coordinator') + $common)
    if ($r.AuthDelta -ne 3) { Add-Failure "coordinator: expected 3 successful authentications, got $($r.AuthDelta)" }

    # 9) Real known_hosts records end-to-end (A4): accept works; @revoked and rotated key are refused pre-auth.
    $r = Invoke-Scenario 'knownhosts-accept' (@('knownhosts', '--known-hosts', (Join-Path $scopeA 'known_hosts'), '--expect', 'accept') + $scopeCommon)
    if ($r.AuthDelta -ne 1) { Add-Failure "knownhosts-accept: expected 1 authentication, got $($r.AuthDelta)" }

    $r = Invoke-Scenario 'knownhosts-revoked' (@('knownhosts', '--known-hosts', $revokedKnownHosts, '--expect', 'revoked') + $scopeCommon)
    if ($r.AuthDelta -ne 0) { Add-Failure "knownhosts-revoked: a revoked host key still authenticated (delta=$($r.AuthDelta))" }

    $r = Invoke-Scenario 'knownhosts-rotated' (@('knownhosts', '--known-hosts', $rotatedKnownHosts, '--expect', 'rotated') + $scopeCommon)
    if ($r.AuthDelta -ne 0) { Add-Failure "knownhosts-rotated: a changed host key still authenticated (delta=$($r.AuthDelta))" }

    # 9b) Hashed host records produced by OpenSSH's own `ssh-keygen -H` must match on a live server.
    $r = Invoke-Scenario 'knownhosts-hashed' (@('knownhosts', '--known-hosts', $hashedKnownHosts, '--expect', 'accept') + $scopeCommon)
    if ($r.AuthDelta -ne 1) { Add-Failure "knownhosts-hashed: expected 1 authentication, got $($r.AuthDelta)" }

    # 10) Two .ssh scopes (current_user / agent_private shapes): each key authenticates exactly once,
    #     a missing identity file never falls back, and a rotated trust file is refused.
    $r = Invoke-Scenario 'dirs' (@('dirs', '--scope-a', $scopeA, '--scope-b', $scopeB,
        '--rotated-known-hosts', $rotatedKnownHosts) + $scopeCommon)
    if ($r.AuthDelta -ne 2) { Add-Failure "dirs: expected 2 authentications (one per scope), got $($r.AuthDelta)" }
    $usedA = @($r.ClientKeys | Where-Object { $_ -eq ($scopeAFp -replace '^SHA256:', '') }).Count
    $usedB = @($r.ClientKeys | Where-Object { $_ -eq ($scopeBFp -replace '^SHA256:', '') }).Count
    if ($usedA -ne 1) { Add-Failure "dirs: scope A client key should be used exactly once, got $usedA (keys=$($r.ClientKeys -join ','))" }
    if ($usedB -ne 1) { Add-Failure "dirs: scope B client key should be used exactly once, got $usedB (keys=$($r.ClientKeys -join ','))" }
    $summary['dirs.scope_a_client_fp'] = $scopeAFp
    $summary['dirs.scope_b_client_fp'] = $scopeBFp

    # 11) Server ignores TERM: must stay unknown (remote process may still be alive).
    $r = Invoke-Scenario 'ignoreterm' (@('ignoreterm') + $common)
    if ($r.AuthDelta -ne 2) { Add-Failure "ignoreterm: expected 2 authentications, got $($r.AuthDelta)" }

    # 12) Connection killed mid-command: connection_lost + unknown state, never replayed.
    $r = Invoke-Scenario 'disconnect' (@('disconnect') + $common)
    if ($r.AuthDelta -ne 1) { Add-Failure "disconnect: expected 1 authentication, got $($r.AuthDelta)" }

    # 13) Wrong account: authentication_failed, no submission. The last --user wins in the probe's parser.
    $r = Invoke-Scenario 'auth' (@('auth') + $common + @('--user', 'nosuchuser_xyz'))
    if ($r.AuthDelta -ne 0) { Add-Failure "auth: a wrong account still authenticated (delta=$($r.AuthDelta))" }

    # 14) R1: the cwd guard must stop the WHOLE script when cd fails (no output, no marker files),
    #     and must preserve the original script semantics when cd succeeds.
    $r = Invoke-Scenario 'cwd' (@('cwd', '--cwd', '/tmp') + $common)
    $connLine = $r.Output | Select-String -Pattern 'name=connections value=(\d+)' | Select-Object -First 1
    if ($connLine) {
        $expectedConnections = [int]$connLine.Matches.Groups[1].Value
        if ($r.AuthDelta -ne $expectedConnections) {
            Add-Failure "cwd: probe opened $expectedConnections connections but the server authenticated $($r.AuthDelta) times"
        }
    }

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
