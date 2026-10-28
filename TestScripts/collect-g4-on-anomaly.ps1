# Wait for the host's read-IO storm, then capture a managed trace the moment it starts.
#
# WHY A WAITER INSTEAD OF "just run dotnet-trace for 5 minutes":
#   The storm is triggered, not constant. Measured today: 9.6 MB/s at 16:14, 820-955 MB/s at
#   16:28-16:32, 13.4 MB/s at 17:34. A fixed-duration trace almost always lands in a quiet
#   window and produces nothing useful. This script polls, and only spends trace time when the
#   read rate says the storm is actually happening.
#
# HARDWARE/PRIVILEGE FACTS ON THIS HOST (verified 2026-10-28):
#   dotnet-trace / dotnet-counters : NOT installed  -> `dotnet tool install --global dotnet-trace`
#   wpr                            : present at C:\Windows\System32\wpr.exe, needs ADMIN
#   current session                : elevated=False  -> dotnet-trace works fine unelevated
#
# WHAT THIS CAPTURES AND WHAT IT CANNOT:
#   cpu-sampling profile gives managed stacks: "which code is burning CPU and issuing the reads".
#   It does NOT give per-file read counts -- .NET's EventPipe cannot see kernel FileIO events.
#   If the reader turns out not to be SQL at all, the stack samples are what identify it.
#
# Exit codes:
#   0 = ran to the deadline without ever seeing the storm
#   2 = dotnet-trace missing (install command printed), or host not running
#   3 = storm detected and a trace was captured (this is the success case)
#   4 = storm detected but the trace command failed
[CmdletBinding()]
param(
    [string]$ProcessName = 'PuddingAgent',
    [int]$MaxMinutes = 60,
    [double]$ThresholdMBps = 300,
    [int]$TraceSeconds = 60,
    [int]$PollSeconds = 2,
    [string]$OutputRoot = 'D:\CodeProject\PuddingAgent\PuddingAgent\temp\g4-capture'
)

$ErrorActionPreference = 'Stop'

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $proc) {
    Write-Output ("FATAL: process '" + $ProcessName + "' is not running")
    exit 2
}
$pid0 = $proc.Id
Write-Output ("target pid=" + $pid0)

$traceExe = Get-Command dotnet-trace -ErrorAction SilentlyContinue
if ($null -eq $traceExe) {
    Write-Output 'FATAL: the trace collector is not installed.'
    Write-Output '  Install it once with:'
    Write-Output '    dotnet tool install --global dotnet-trace'
    Write-Output '  Then re-run this script. (It does NOT need an elevated shell.)'
    exit 2
}
Write-Output ("trace tool=" + $traceExe.Source)

if (-not (Test-Path -LiteralPath $OutputRoot)) {
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
}

$counter = '\Process(' + $proc.ProcessName + ')\IO Read Bytes/sec'
$deadline = (Get-Date).AddMinutes($MaxMinutes)
Write-Output ("threshold=" + $ThresholdMBps + " MBps   poll=" + $PollSeconds + "s   deadline=" + $deadline.ToString('HH:mm:ss'))
Write-Output 'waiting for the storm (Ctrl+C to stop without capturing)...'
Write-Output ''

$seen = 0
while ((Get-Date) -lt $deadline) {
    $mbps = -1.0
    try {
        $sample = Get-Counter -Counter $counter -MaxSamples 1 -ErrorAction Stop
        $v = $sample.CounterSamples | Select-Object -First 1
        if ($null -ne $v) { $mbps = [Math]::Round($v.CookedValue / 1MB, 1) }
    } catch {
        Write-Output ('  counter read failed: ' + $_.Exception.Message)
    }

    if ($mbps -ge $ThresholdMBps) {
        $seen++
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $tracePath = Join-Path $OutputRoot ('g4-cpu-' + $stamp + '.nettrace')
        Write-Output ''
        Write-Output ("STORM DETECTED at " + (Get-Date -Format 'o') + "  MBps=" + $mbps)
        Write-Output ("capturing " + $TraceSeconds + "s of managed stacks -> " + $tracePath)

        $duration = [TimeSpan]::FromSeconds($TraceSeconds).ToString()
        # --profile cpu-sampling is the low-friction choice: it selects the right providers and
        # avoids hand-writing provider GUIDs / keyword bitmasks.
        # Call through the already-resolved path instead of the bare name: the existence check
        # above is the real guard, and resolving once avoids a second PATH lookup.
        & $traceExe.Source collect --process-id $pid0 --profile cpu-sampling --duration $duration --output $tracePath
        $traceExit = $LASTEXITCODE

        if ($traceExit -eq 0 -and (Test-Path -LiteralPath $tracePath)) {
            $bytes = (Get-Item -LiteralPath $tracePath).Length
            Write-Output ("CAPTURED ok: " + $tracePath + "  bytes=" + $bytes)
            Write-Output ''
            Write-Output 'NEXT (bring these files back):'
            Write-Output ("  1) " + $tracePath)
            Write-Output '  2) if you can also run an ADMIN shell, capture per-file IO for 30 s during a storm:'
            Write-Output '       wpr -start FileIO -filemode'
            Write-Output '       ... wait 30 s while the storm is running ...'
            Write-Output '       wpr -stop D:\CodeProject\PuddingAgent\PuddingAgent\temp\g4-capture\g4-fileio.etl'
            Write-Output '  3) the host log window covering the same timestamps'
            exit 3
        }

        Write-Output ("TRACE FAILED exit=" + $traceExit)
        exit 4
    }

    # Keep the console honest about what was observed; a silent 60 minute wait is indistinguishable
    # from a hung script.
    if (($seen -eq 0) -and (((Get-Date).Minute % 5) -eq 0) -and ((Get-Date).Second -lt $PollSeconds)) {
        Write-Output ("  ... still waiting, last MBps=" + $mbps + " at " + (Get-Date -Format 'HH:mm:ss'))
    }

    Start-Sleep -Seconds $PollSeconds
}

Write-Output ''
Write-Output ("RESULT: no storm above " + $ThresholdMBps + " MBps within " + $MaxMinutes + " minutes (last=" + $mbps + " MBps).")
Write-Output '  Do not record this as "the problem is gone" -- it is triggered and can return hours later.'
exit 0
