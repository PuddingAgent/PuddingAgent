param(
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$AllowDirty,
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot } else { Join-Path $repo $OutputRoot }))
$allowed = [IO.Path]::GetFullPath((Join-Path $repo 'temp/build')) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be a dedicated directory inside repository temp/build.'
}
$manifestPath = Join-Path $output 'browser-companion.json'

function Get-AssemblyIdentity([string]$Path, [switch]$Protocol) {
    $stream = [IO.File]::OpenRead($Path)
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $identity = [ordered]@{
            informationalVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion
            mvid = $reader.GetGuid($reader.GetModuleDefinition().Mvid).ToString()
        }
        if ($Protocol) {
            $version = $null
            foreach ($typeHandle in $reader.TypeDefinitions) {
                $type = $reader.GetTypeDefinition($typeHandle)
                if ($reader.GetString($type.Namespace) -ne 'PuddingBrowser.Protocol' -or
                    $reader.GetString($type.Name) -ne 'BrowserBridgeProtocol') { continue }
                foreach ($fieldHandle in $type.GetFields()) {
                    $field = $reader.GetFieldDefinition($fieldHandle)
                    if ($reader.GetString($field.Name) -eq 'CurrentVersion') {
                        $constant = $reader.GetConstant($field.GetDefaultValue())
                        $blob = $reader.GetBlobReader($constant.Value)
                        $version = $blob.ReadInt32()
                    }
                }
            }
            if ($null -eq $version) { throw "Protocol version constant missing: $Path" }
            $identity.protocolVersion = $version
        }
        return $identity
    }
    finally { $pe.Dispose(); $stream.Dispose() }
}

Push-Location $repo
try {
    if (-not $VerifyOnly) {
        if (Test-Path -LiteralPath $output) { throw 'OutputRoot already exists; select a fresh directory. No package is overwritten.' }
        $revision = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Cannot read source revision.' }
        $dirty = [bool](& git status --porcelain)
        if ($dirty -and -not $AllowDirty) { throw 'Working tree is dirty. Use a clean checkout, or explicitly AllowDirty for a development repair.' }
        New-Item -ItemType Directory -Path $output | Out-Null
        $outDir = $output + [IO.Path]::DirectorySeparatorChar
        & dotnet restore Source/PuddingDesktop/PuddingDesktop.csproj --artifacts-path temp/build/recovery --nologo "-p:Configuration=$Configuration" "-p:OutDir=$outDir"
        if ($LASTEXITCODE -ne 0) { throw 'Desktop restore failed.' }
        # BuildCoreBundle publishes a complete independent Core into this exact TargetDir/core.
        & dotnet build Source/PuddingDesktop/PuddingDesktop.csproj --artifacts-path temp/build/recovery --no-restore --nologo "-p:Configuration=$Configuration" "-p:OutDir=$outDir"
        if ($LASTEXITCODE -ne 0) { throw 'Companion build failed.' }
        if ((& git rev-parse HEAD).Trim() -ne $revision) { throw 'HEAD changed during build; package is not stamped.' }
        foreach ($required in @('PuddingDesktop.exe', 'PuddingDesktop.dll', 'PuddingDesktop.pri', 'core/PuddingAgent.exe', 'core/PuddingAgent.dll', 'core/PuddingHost.dll', 'core/wwwroot/admin/index.html')) {
            if (-not (Test-Path -LiteralPath (Join-Path $output $required))) { throw "Missing bundle file: $required" }
        }
        if (Test-Path -LiteralPath (Join-Path $output 'PuddingHost.dll')) { throw 'Shell directory must not contain the business Host.' }
        $desktopProtocol = Get-AssemblyIdentity (Join-Path $output 'PuddingBrowser.Protocol.dll') -Protocol
        $coreProtocol = Get-AssemblyIdentity (Join-Path $output 'core/PuddingBrowser.Protocol.dll') -Protocol
        if ($desktopProtocol.protocolVersion -ne $coreProtocol.protocolVersion) { throw 'Protocol versions differ across the pair.' }
        $files = @(Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName | ForEach-Object {
            [ordered]@{ path = [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        })
        [ordered]@{
            schemaVersion = 1; sourceRevision = $revision; dirty = $dirty
            purpose = $(if ($dirty) { 'development-repair' } else { 'companion-build' })
            createdAtUtc = [DateTime]::UtcNow.ToString('O'); configuration = $Configuration
            desktop = Get-AssemblyIdentity (Join-Path $output 'PuddingDesktop.dll')
            core = Get-AssemblyIdentity (Join-Path $output 'core/PuddingAgent.dll')
            desktopProtocol = $desktopProtocol; coreProtocol = $coreProtocol; files = $files
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or @($manifest.files).Count -eq 0) { throw 'Unsupported or empty companion manifest.' }
    foreach ($required in @('PuddingDesktop.dll', 'core/PuddingAgent.dll', 'PuddingBrowser.Protocol.dll', 'core/PuddingBrowser.Protocol.dll')) {
        if ($manifest.files.path -notcontains $required) { throw "Manifest does not bind required file: $required" }
    }
    foreach ($file in $manifest.files) {
        $target = [IO.Path]::GetFullPath((Join-Path $output $file.path))
        if (-not $target.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escapes package.' }
        if (-not (Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) {
            throw "Package does not match manifest: $($file.path)"
        }
    }
    $actualFiles = @(Get-ChildItem -LiteralPath $output -File -Recurse | Where-Object { $_.FullName -ne $manifestPath })
    if ($actualFiles.Count -ne $manifest.files.Count) { throw 'Unexpected files in package; deploy into an empty directory.' }
    Write-Output "Verified companion: $output; files=$($manifest.files.Count); protocol=$($manifest.desktopProtocol.protocolVersion); dirty=$($manifest.dirty)"
}
finally { Pop-Location }
