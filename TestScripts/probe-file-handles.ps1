# Enumerate the file handles a process actually holds -- no admin, no handle.exe, no procmon.
#
# Why this exists (2026-10-28): during the read-IO investigation every PDH counter answered
# "how much", never "which file". This script answers "what is open right now", which combined
# with per-file read-rate reasoning narrows the field to a handful of candidates.
#
# WHY GetFileType IS THE CRITICAL GUARD:
#   A naive implementation calls GetFinalPathNameByHandle on every handle. On this host that
#   hangs forever on a couple of device/pipe handles -- the first version of this script stalled
#   at slots 601-700 and again at 1001-1100, which is why it had to be run in segments with a
#   -From offset. Calling GetFileType first and only resolving FILE_TYPE_DISK handles removes
#   the hang entirely, so the whole handle table can be walked in one pass.
#
# Other traps paid for in full during development (kept because each produced a plausible answer):
#   * NtQueryObject(ObjectNameInformation) BLOCKS on synchronous named pipes -- do not use it.
#   * OpenProcess(0x1000) lacks PROCESS_DUP_HANDLE (0x0040); every DuplicateHandle then fails
#     and the script reports a clean-looking but empty result. Use 0x1040.
#   * SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX offsets: Object@0, UniqueProcessId@8, HandleValue@16,
#     GrantedAccess@24, ObjectTypeIndex@30. Being 8 bytes off matches only a handful of bogus
#     "handles" and silently duplicates none.
#   * NtQueryObject returns uint, not int.
#
# Read-only: it duplicates handles into this process and closes them. It never opens, writes or
# deletes anything belonging to the target.
#
# Exit codes:
#   0 = walked the handle table
#   2 = target process not running (or OpenProcess failed)
#   3 = P/Invoke setup failed
[CmdletBinding()]
param(
    [string]$ProcessName = 'PuddingAgent',
    # Module images (DLL) are noise for "which data file is being read"; keep them out by default.
    [switch]$IncludeModules,
    # Emit every distinct path even when it is a module image.
    [switch]$AllPaths
)

$ErrorActionPreference = 'Stop'

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $proc) {
    Write-Output ("FATAL: process '" + $ProcessName + "' is not running")
    exit 2
}
Write-Output ("PID=" + $proc.Id + " START=" + $proc.StartTime.ToString('o'))

$source = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class FileHandleProbe
{
    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int infoLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DuplicateHandle(IntPtr srcProc, IntPtr srcHandle, IntPtr dstProc, out IntPtr dstHandle, int access, bool inherit, int options);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFinalPathNameByHandleW(IntPtr hFile, StringBuilder path, uint pathLen, uint flags);

    // GetFileType return values
    public const uint FILE_TYPE_UNKNOWN = 0x0000;
    public const uint FILE_TYPE_DISK = 0x0001;
    public const uint FILE_TYPE_CHAR = 0x0002;
    public const uint FILE_TYPE_PIPE = 0x0003;
    public const uint FILE_TYPE_REMOTE = 0x8000;

    public static List<IntPtr> Enumerate(int pid, out int systemTotal)
    {
        var result = new List<IntPtr>();
        systemTotal = 0;

        int len = 1 << 22;
        IntPtr buffer = IntPtr.Zero;
        int rc = 0;

        for (int attempt = 0; attempt < 8; attempt++)
        {
            buffer = Marshal.AllocHGlobal(len);
            rc = NtQuerySystemInformation(64, buffer, len, out int needed); // 64 = SystemExtendedHandleInformation
            if (rc == 0) break;
            Marshal.FreeHGlobal(buffer);
            buffer = IntPtr.Zero;
            if (rc == unchecked((int)0xC0000004)) { len = needed + (1 << 16); continue; } // STATUS_INFO_LENGTH_MISMATCH
            throw new Exception("NtQuerySystemInformation failed 0x" + rc.ToString("X8"));
        }
        if (buffer == IntPtr.Zero) throw new Exception("buffer allocation failed");

        try
        {
            long count = Marshal.ReadIntPtr(buffer, 0).ToInt64();
            systemTotal = (int)count;
            const int entrySize = 40;
            IntPtr baseAddr = buffer + IntPtr.Size * 2;
            for (long i = 0; i < count; i++)
            {
                IntPtr e = baseAddr + (int)(i * entrySize);
                IntPtr uniqueProc = Marshal.ReadIntPtr(e, 8);
                IntPtr handleValue = Marshal.ReadIntPtr(e, 16);
                if (uniqueProc.ToInt64() == pid) result.Add(handleValue);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }

        return result;
    }

    public static string TryResolvePath(IntPtr handle)
    {
        uint type = GetFileType(handle);
        if (type != FILE_TYPE_DISK) return null;   // pipes / char devices hang GetFinalPathNameByHandle

        var sb = new StringBuilder(1024);
        uint n = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, 0);
        if (n == 0 || n >= sb.Capacity) return null;
        return sb.ToString(0, (int)n);
    }
}
'@

try {
    Add-Type -TypeDefinition $source -Language CSharp
} catch {
    Write-Output ("FATAL: Add-Type failed: " + $_.Exception.Message)
    exit 3
}

$systemTotal = 0
$handles = [FileHandleProbe]::Enumerate($proc.Id, [ref]$systemTotal)
Write-Output ("SYSTEM_TOTAL_HANDLES=" + $systemTotal)
Write-Output ("PID_HANDLES=" + $handles.Count)

$procHandle = [FileHandleProbe]::OpenProcess(0x1040, $false, $proc.Id)  # QUERY_LIMITED_INFORMATION | DUP_HANDLE
if ($procHandle -eq [IntPtr]::Zero) {
    Write-Output 'FATAL: OpenProcess(0x1040) failed'
    exit 2
}
$self = [FileHandleProbe]::GetCurrentProcess()

$paths = @{}
$resolved = 0
$duplicated = 0
$sw = [Diagnostics.Stopwatch]::StartNew()

foreach ($h in $handles) {
    $dup = [IntPtr]::Zero
    if (-not [FileHandleProbe]::DuplicateHandle($procHandle, $h, $self, [ref]$dup, 0, $false, 2)) { continue }
    $duplicated++
    try {
        $p = [FileHandleProbe]::TryResolvePath($dup)
        if ($null -ne $p) {
            $resolved++
            if ($paths.ContainsKey($p)) { $paths[$p]++ } else { $paths[$p] = 1 }
        }
    } catch { }
    finally { if ($dup -ne [IntPtr]::Zero) { [void][FileHandleProbe]::CloseHandle($dup) } }
}
[void][FileHandleProbe]::CloseHandle($procHandle)
$sw.Stop()

Write-Output ("DUP_OK=" + $duplicated + "  DISK_PATHS_RESOLVED=" + $resolved)
Write-Output ("ELAPSED_MS=" + $sw.ElapsedMilliseconds)

# Classify. Module images (.dll/.exe/.pdb) are runtime noise; data files and directories matter.
$dataFiles = @{}
$dirs = @{}
$modules = @{}
foreach ($kv in $paths.GetEnumerator()) {
    $p = [string]$kv.Key
    $leaf = $p
    $sep = $p.LastIndexOf('\')
    if ($sep -ge 0) { $leaf = $p.Substring($sep + 1) }
    $ext = ''
    $dot = $leaf.LastIndexOf('.')
    if ($dot -ge 0) { $ext = $leaf.Substring($dot).ToLowerInvariant() }

    if ($ext -eq '.dll' -or $ext -eq '.exe' -or $ext -eq '.pdb') { $modules[$p] = $kv.Value }
    elseif ($ext -eq '') { $dirs[$p] = $kv.Value }        # no extension => likely a directory or extensionless file
    else { $dataFiles[$p] = $kv.Value }
}

Write-Output ''
Write-Output '=== data files (the interesting set) ==='
if ($dataFiles.Count -eq 0) { Write-Output '  <none>' }
foreach ($kv in ($dataFiles.GetEnumerator() | Sort-Object Name)) {
    Write-Output ("  " + $kv.Value + "x  " + $kv.Key)
}

Write-Output ''
Write-Output '=== directories / extensionless (watch scopes, locks) ==='
if ($dirs.Count -eq 0) { Write-Output '  <none>' }
foreach ($kv in ($dirs.GetEnumerator() | Sort-Object Name)) {
    Write-Output ("  " + $kv.Value + "x  " + $kv.Key)
}

Write-Output ''
Write-Output ("MODULE_IMAGES=" + $modules.Count + " distinct (suppressed; pass -IncludeModules to list)")
if ($IncludeModules -or $AllPaths) {
    foreach ($kv in ($modules.GetEnumerator() | Sort-Object Name)) {
        Write-Output ("  " + $kv.Value + "x  " + $kv.Key)
    }
}

Write-Output ''
Write-Output ("UNIQUE_PATHS=" + $paths.Count + "  DATA=" + $dataFiles.Count + "  DIRS=" + $dirs.Count + "  MODULES=" + $modules.Count)
exit 0
