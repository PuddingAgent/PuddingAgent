# UIA 只读观察 sidecar（原型）
#
# 为什么需要它：Docs/14_reports/2026-11-11-UIA观察层可读性核查-actionable恒0定案.md §8-§10
# 记录了十二轮排查——同一台机上 pwsh 进程能读到完整 UIA 树（469~1622 个后代），
# 而本仓任何 .NET 进程只能读到 2 个框架级元素。机制未查明，但 pwsh 路径已验证可用。
#
# 本脚本只做**只读观察**：枚举目标窗口的 UIA 树，输出 JSON 给调用方。
# 不点击、不输入、不抢焦点、不写注册表。
#
# 用法：
#   pwsh -NoProfile -File observe.ps1 -OutPath out.json            # 观察当前前台窗口
#   pwsh -NoProfile -File observe.ps1 -OutPath out.json -TitleLike "ChatGPT"
#
# 输出 JSON 字段：
#   ok, hwnd, name, className, pid, elementCount, actionableCount,
#   controlTypeHistogram, actionableSample[], elapsedMs, error

param(
    [Parameter(Mandatory = $true)][string]$OutPath,
    [string]$TitleLike = "",
    [int]$SampleLimit = 20
)

$ErrorActionPreference = 'Stop'
$sw = [System.Diagnostics.Stopwatch]::StartNew()

function Write-Result($obj) {
    $sw.Stop()
    $obj | Add-Member -NotePropertyName elapsedMs -NotePropertyValue $sw.ElapsedMilliseconds -Force
    $json = $obj | ConvertTo-Json -Depth 6 -Compress
    [IO.File]::WriteAllText($OutPath, $json, (New-Object Text.UTF8Encoding($false)))
}

try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes

    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class SidecarNative {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
"@

    $root = [System.Windows.Automation.AutomationElement]::RootElement

    # 选目标：优先 -TitleLike 匹配，否则用前台窗口
    $target = $null
    $source = ""
    if ($TitleLike) {
        foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children,
                                     [System.Windows.Automation.Condition]::TrueCondition)) {
            $n = ""
            try { $n = $w.Current.Name } catch { }
            if ($n -and $n.IndexOf($TitleLike, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $target = $w; $source = "title"; break
            }
        }
    }
    if ($null -eq $target) {
        $h = [SidecarNative]::GetForegroundWindow()
        if ($h -ne [IntPtr]::Zero) {
            $target = [System.Windows.Automation.AutomationElement]::FromHandle($h)
            $source = "foreground"
        }
    }

    if ($null -eq $target) {
        Write-Result ([ordered]@{ ok = $false; error = "no_target"; source = $source })
        return
    }

    $name = ""; $cls = ""; $pid_ = -1
    try { $name = $target.Current.Name } catch { }
    try { $cls = $target.Current.ClassName } catch { }
    try { $pid_ = $target.Current.ProcessId } catch { }

    $all = $target.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                           [System.Windows.Automation.Condition]::TrueCondition)

    $hist = @{}
    $actionable = 0
    $sample = New-Object System.Collections.ArrayList

    foreach ($e in $all) {
        $ct = ""
        try { $ct = $e.Current.ControlType.ProgrammaticName } catch { }
        if ($ct) { $hist[$ct] = 1 + ($hist[$ct] -as [int]) }

        $enabled = $true; $offscreen = $false
        try { $enabled = $e.Current.IsEnabled } catch { }
        try { $offscreen = $e.Current.IsOffscreen } catch { }

        $pat = $null
        if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) {
            if ($enabled -and -not $offscreen) {
                $actionable++
                if ($sample.Count -lt $SampleLimit) {
                    $sn = ""; try { $sn = $e.Current.Name } catch { }
                    [void]$sample.Add([ordered]@{
                        controlType = $ct
                        name = $sn
                        patterns = @("Invoke")
                    })
                }
            }
        }
    }

    Write-Result ([ordered]@{
        ok = $true
        source = $source
        hwnd = ("0x" + $target.Current.NativeWindowHandle.ToString("X"))
        name = $name
        className = $cls
        pid = $pid_
        elementCount = $all.Count
        actionableCount = $actionable
        controlTypeHistogram = $hist
        actionableSample = $sample
    })
}
catch {
    Write-Result ([ordered]@{ ok = $false; error = ($_.Exception.GetType().Name + ": " + $_.Exception.Message) })
}
