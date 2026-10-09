<#
.SYNOPSIS
    Round-completion gates: run the repo's documentation gates and print TOTALS (never a filtered grep).

.DESCRIPTION
    Why this exists (2026-10-08): I used to run check_md_links.py and then grep its output for the ONE
    filename I had just written. Seeing nothing, I reported "zero broken links" -- for over ten rounds,
    while my own changelogs accumulated ~33 broken links. The evidence method itself hid the truth.

    Two rules kept here on purpose:
      * always print the TOTALS (they are the evidence);
      * never grep for a filename to decide whether a gate passed.

    This script is ASCII-only: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI, so non-ASCII
    literals here would be a parse/compare hazard. The gate tools print Chinese labels, therefore the
    parsers below match digits and ASCII separators only.

    Checks: front matter compliance, repo-wide link health, UTF-8 BOM scan over Docs/**/*.md.

.EXAMPLE
    powershell -File TestScripts/check-round-gates.ps1
#>
[CmdletBinding()]
param(
    # Ratchet baselines (measured 2026-10-08, commit 16a6df8). Lower them when the offenders get fixed;
    # never raise them just to make a round pass. Offenders currently known: two files owned by other
    # teams (Docs/10_conventions/Harness*, Docs/14_reports/skill-merge-probe-rerun-*) and 24 broken
    # link targets in other teams' docs (07_architecture / 15_tasks / 16_qa / T6 changelogs).
    [int]$BaselineFrontMatterNonCompliant = 2,
    [int]$BaselineBrokenLinkTargets = 32,
    [int]$BaselineBrokenAnchors = 0,
    [int]$BaselineDocsBomFiles = 0,

    # Rewrite broken relative links in MY deliverables to the correct depth (one command instead of my
    # fourth manual fix). Scoped by -OwnerPattern so it never touches another team's documents.
    # NOTE: the pattern must stay ASCII-only -- Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI, so a
    # literal Chinese path here would be mangled into an invalid regex. "\u6D4F\u89C8\u5668" spells the
    # Chinese word for "browser" (used in those file names) without any non-ASCII byte in this file.
    [switch]$FixLinks,
    [string]$OwnerPattern = '^Docs/(00_changelog/2026Year/10/2026-10-0[89]-(?!T\d)|12_features/(\u6D4F\u89C8\u5668|B6-)|13_runbooks/\u6D4F\u89C8\u5668|14_reports/2026-10-0[89]-\u6D4F\u89C8\u5668)'
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot

$regressions = @()

function Get-Numbers {
    param([string]$Text)
    # Extract every integer from the first line that mentions the scanned file count.
    foreach ($line in ($Text -split "`r?`n")) {
        if ($line -match "md") {
            $numbers = [regex]::Matches($line, "[0-9]+") | ForEach-Object { [int]$_.Value }
            if ($numbers.Count -ge 2) { return ,$numbers }
        }
    }
    return $null
}

function Get-RelativePathFrom {
    param([string]$FromDirectory, [string]$ToPath)
    # [System.IO.Path]::GetRelativePath does not exist in Windows PowerShell 5.1 (.NET Framework),
    # so compute it by hand (learned in round 89, forgotten, and paid for again in round 94).
    $from = (Resolve-Path $FromDirectory).Path.TrimEnd('\')
    $to = (Resolve-Path $ToPath).Path
    $fromParts = $from.Split('\')
    $toParts = $to.Split('\')
    $common = 0
    while ($common -lt $fromParts.Count -and $common -lt $toParts.Count -and
           $fromParts[$common] -eq $toParts[$common]) {
        $common++
    }
    $parts = @()
    for ($index = $common; $index -lt $fromParts.Count; $index++) { $parts += '..' }
    for ($index = $common; $index -lt $toParts.Count; $index++) { $parts += $toParts[$index] }
    return ($parts -join '/')
}

try {
    New-Item -ItemType Directory -Force -Path temp/test-out | Out-Null

    Write-Host "=== 1/3 front matter (Docs)"
    $frontMatter = & python Tools/Docs/front_matter.py --check --paths Docs 2>&1 | Out-String
    $frontMatter | Out-File -Encoding UTF8 temp/test-out/round-gates-front-matter.txt
    $frontNumbers = Get-Numbers -Text $frontMatter
    if ($null -eq $frontNumbers) {
        Write-Warning "cannot parse front matter totals; raw output kept at temp/test-out/round-gates-front-matter.txt"
        $regressions += "front matter totals unreadable"
    }
    else {
        # Front matter summary line: "CHECK ... <scanned> ... md ... <non-compliant> ... <other>".
        # The NON-COMPLIANT count is the SECOND number -- taking the last one silently reported 0
        # (that would have been a false pass, i.e. the very failure mode this script exists to stop).
        $nonCompliant = $frontNumbers[1]
        Write-Host ("    scanned={0}  non-compliant={1}  (baseline {2})" -f $frontNumbers[0], $nonCompliant, $BaselineFrontMatterNonCompliant)
        if ($nonCompliant -gt $BaselineFrontMatterNonCompliant) {
            $regressions += "front matter non-compliant $nonCompliant > baseline $BaselineFrontMatterNonCompliant"
        }
    }

    Write-Host "=== 2/3 links (repo-wide)"
    $linksReport = "temp/test-out/round-gates-links.txt"
    $links = & python Tools/Docs/check_md_links.py --all-repo --report $linksReport 2>&1 | Out-String

    if ($FixLinks) {
        # One command instead of a fifth manual fix. Only touches files matching -OwnerPattern.
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        $ownerRegex = [regex]$OwnerPattern
        $fixed = 0
        $skipped = @()

        if (Test-Path $linksReport) {
            # Offender lines look like: "  Docs/....md  ->  ../../target.md"
            foreach ($line in (Get-Content $linksReport -Encoding UTF8)) {
                $match = [regex]::Match($line, '^\s+(\S+\.md)\s+->\s+(\S+)\s*$')
                if (-not $match.Success) { continue }

                $offenderRel = $match.Groups[1].Value.Replace('/', '\')
                $targetText = $match.Groups[2].Value
                $repoRel = $offenderRel.Replace('\', '/')
                if (-not $ownerRegex.IsMatch($repoRel)) { continue }

                $offenderFull = Join-Path $repositoryRoot $offenderRel
                if (-not (Test-Path $offenderFull)) { continue }

                $leaf = [System.IO.Path]::GetFileName($targetText)
                if ([string]::IsNullOrWhiteSpace($leaf)) { continue }

                $candidates = @(Get-ChildItem -Path $repositoryRoot -Recurse -File -Filter $leaf -ErrorAction SilentlyContinue |
                    Where-Object { $_.FullName -notmatch '\\(bin|obj|node_modules|\.git)\\' })
                if ($candidates.Count -ne 1) {
                    $skipped += "$repoRel -> $targetText (matches=$($candidates.Count))"
                    continue
                }

                $text = [System.IO.File]::ReadAllText($offenderFull)
                if (-not $text.Contains($targetText)) { continue }

                $correct = Get-RelativePathFrom -FromDirectory ([System.IO.Path]::GetDirectoryName($offenderFull)) -ToPath $candidates[0].FullName

                # Anchor the replacement to a whole markdown link: a plain substring replace double-applied
                # ("../../../x" CONTAINS "../../x"), which over-corrected by one level once already.
                $pattern = '\]\(' + [regex]::Escape($targetText) + '\)'
                $rewritten = [regex]::Replace($text, $pattern, '](' + $correct + ')')
                if ($rewritten -eq $text) { continue }

                [System.IO.File]::WriteAllText($offenderFull, $rewritten, $utf8NoBom)
                Write-Host "    fixed: $repoRel -> $correct"
                $fixed++
            }
        }

        Write-Host ("    links rewritten = {0}; skipped (ambiguous/missing target) = {1}" -f $fixed, $skipped.Count)
        $skipped | Select-Object -First 5 | ForEach-Object { Write-Host "      skipped: $_" }

        if ($fixed -gt 0) {
            # Re-measure after rewriting so the reported totals describe the current state.
            $links = & python Tools/Docs/check_md_links.py --all-repo --report $linksReport 2>&1 | Out-String
        }
    }

    $linkNumbers = Get-Numbers -Text $links
    if ($null -eq $linkNumbers -or $linkNumbers.Count -lt 3) {
        Write-Warning "cannot parse link totals; raw output kept at $linksReport"
        $regressions += "link totals unreadable"
    }
    else {
        $brokenTargets = $linkNumbers[1]
        $brokenAnchors = $linkNumbers[2]
        Write-Host ("    scanned={0}  broken targets={1} (baseline {2})  broken anchors={3} (baseline {4})" -f `
            $linkNumbers[0], $brokenTargets, $BaselineBrokenLinkTargets, $brokenAnchors, $BaselineBrokenAnchors)
        if ($brokenTargets -gt $BaselineBrokenLinkTargets) { $regressions += "broken link targets $brokenTargets > baseline $BaselineBrokenLinkTargets" }
        if ($brokenAnchors -gt $BaselineBrokenAnchors) { $regressions += "broken anchors $brokenAnchors > baseline $BaselineBrokenAnchors" }
        Write-Host "    full offender list: $linksReport"
    }

    Write-Host "=== 3/3 UTF-8 BOM in Docs markdown"
    $bomFiles = @()
    Get-ChildItem -Recurse Docs -Filter *.md | ForEach-Object {
        $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            $bomFiles += $_.FullName.Replace("$repositoryRoot\", "")
        }
    }
    Write-Host ("    BOM files = {0} (baseline {1})" -f $bomFiles.Count, $BaselineDocsBomFiles)
    if ($bomFiles.Count -gt $BaselineDocsBomFiles) {
        $regressions += "Docs BOM files $($bomFiles.Count) > baseline $BaselineDocsBomFiles"
        $bomFiles | Select-Object -First 10 | ForEach-Object { Write-Host "      - $_" }
    }

    if ($regressions.Count -gt 0) {
        Write-Host ""
        Write-Warning "GATE REGRESSIONS:"
        $regressions | ForEach-Object { Write-Host "  - $_" }
        exit 1
    }

    Write-Host ""
    Write-Host "=== all gates within baseline (the totals above are the evidence, not a filtered list)"
    exit 0
}
finally {
    Pop-Location
}
