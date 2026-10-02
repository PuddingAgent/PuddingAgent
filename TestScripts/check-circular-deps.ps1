<#
.SYNOPSIS
检测 PuddingAgent 解决方案中的项目循环依赖
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path | Split-Path -Parent | Split-Path -Parent
$sourceDir = Join-Path $root 'Source'

Write-Host "==> 扫描项目引用..." -ForegroundColor Cyan

$projects = @{}
$csprojFiles = Get-ChildItem -Path $sourceDir -Recurse -Filter '*.csproj' | Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' }

foreach ($file in $csprojFiles) {
    $name = $file.BaseName
    $projects[$name] = @{
        Path = $file.FullName
        References = [System.Collections.Generic.Dictionary[string,bool]]::new()
    }

    $content = Get-Content $file.FullName -Raw
    $refMatches = [regex]::Matches($content, '<ProjectReference\s+Include="([^"]+)"')
    foreach ($m in $refMatches) {
        $refPath = $m.Groups[1].Value
        $refName = (Split-Path $refPath -LeafBase)
        if (-not $projects[$name].References.ContainsKey($refName)) {
            $projects[$name].References[$refName] = $true
        }
    }
}

Write-Host "发现 $($projects.Count) 个项目" -ForegroundColor Green

# 显示依赖图
Write-Host "`n==> 依赖关系总览" -ForegroundColor Cyan
foreach ($proj in ($projects.Keys | Sort-Object)) {
    $refs = $projects[$proj].References.Keys | Sort-Object
    if ($refs.Count -eq 0) {
        Write-Host "  $proj -> (无项目引用)" -ForegroundColor DarkGray
    } else {
        Write-Host "  $proj -> $($refs -join ', ')" -ForegroundColor Yellow
    }
}

# 检测循环依赖
Write-Host "`n==> 检测循环依赖..." -ForegroundColor Cyan

function Find-Cycles {
    param(
        [hashtable]$Graph,
        [string]$StartNode
    )

    $visited = @{}
    $recStack = @{}
    $path = [System.Collections.Generic.Stack[string]]::new()

    function DFS($node) {
        $visited[$node] = $true
        $recStack[$node] = $true
        $null = $path.Push($node)

        if ($Graph.ContainsKey($node)) {
            foreach ($neighbor in $Graph[$node].Keys) {
                if (-not $visited.ContainsKey($neighbor)) {
                    DFS $neighbor
                } elseif ($recStack.ContainsKey($neighbor)) {
                    # 找到循环
                    $cycle = @()
                    $temp = [System.Collections.Generic.Stack[string]]::new()
                    while ($temp.Count -eq 0 -or $temp.Peek() -ne $neighbor) {
                        $item = $path.Pop()
                        $null = $temp.Push($item)
                        $cycle += $item
                    }
                    $cycle += $neighbor
                    $cycle = [array]::Reverse($cycle)
                    Write-Host "  发现循环: $($cycle -join ' -> ')" -ForegroundColor Red
                    # 恢复路径
                    foreach ($item in $temp) {
                        $null = $path.Push($item)
                    }
                }
            }
        }

        $recStack.Remove($node) | Out-Null
        $null = $path.Pop()
    }

    DFS $StartNode
}

$allProjectNodes = $projects.Keys
$globalVisited = @{}
$cyclesFound = 0

foreach ($node in $allProjectNodes) {
    if (-not $globalVisited.ContainsKey($node)) {
        Find-Cycles -Graph $projects -StartNode $node
        $globalVisited[$node] = $true
    }
}

if ($cyclesFound -eq 0) {
    Write-Host "`n未发现循环依赖。" -ForegroundColor Green
} else {
    Write-Host "`n发现 $cyclesFound 个循环依赖。" -ForegroundColor Red
}
