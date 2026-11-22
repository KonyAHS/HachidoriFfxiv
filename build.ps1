#   ./build.ps1 -Verify            # build and run the regression checks
#   ./build.ps1 -Verify -IfChanged # skip both when the last verified build matches the checkout
param([switch]$Verify, [switch]$SkipNative, [switch]$IfChanged)

$ErrorActionPreference = 'Stop'
$taskOutput = Join-Path $PSScriptRoot 'HachidoriFfxiv/bin/x64/Release'
$taskStampPath = Join-Path $taskOutput '.verified-build.json'
$taskStampOutputs = @('HachidoriFfxiv.dll', 'HachidoriFfxiv.deps.json', 'HachidoriFfxiv.json', 'hdw_native.dll', 'hdw_native.json', 'WebView2Loader.dll', 'HachidoriFfxiv/latest.zip')

# Every tracked or unignored file in the checkout, including uncommitted edits.
function Get-TaskInputStamp {
    Push-Location $PSScriptRoot
    try {
        $paths = @(git ls-files -co --exclude-standard | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Sort-Object -Unique)
        if ($LASTEXITCODE -ne 0) { return $null }
        $hashes = @($paths | git hash-object --stdin-paths)
        if ($LASTEXITCODE -ne 0 -or $hashes.Count -ne $paths.Count) { return $null }
        $text = (0..($paths.Count - 1) | ForEach-Object { "$($paths[$_]) $($hashes[$_])" }) -join "`n"
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))) -replace '-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
    }
    finally { Pop-Location }
}

function Get-TaskOutputHashes {
    $result = [ordered]@{}
    foreach ($name in $taskStampOutputs) {
        $path = Join-Path $taskOutput $name
        $result[$name] = if (Test-Path -LiteralPath $path -PathType Leaf) { (Get-FileHash -LiteralPath $path).Hash } else { $null }
    }
    return $result
}

$taskInputStamp = Get-TaskInputStamp
if ($IfChanged -and $Verify -and $taskInputStamp -and (Test-Path -LiteralPath $taskStampPath -PathType Leaf)) {
    $taskPrevious = Get-Content -LiteralPath $taskStampPath -Raw | ConvertFrom-Json
    $taskOutputs = Get-TaskOutputHashes
    $taskUnchanged = $taskPrevious.inputs -eq $taskInputStamp -and
        @($taskStampOutputs | Where-Object { -not $taskOutputs[$_] -or $taskOutputs[$_] -ne $taskPrevious.outputs.$_ }).Count -eq 0
    if ($taskUnchanged) {
        Write-Host "Build and regression checks already passed for this checkout ($($taskPrevious.verifiedAt)); skipping. Omit -IfChanged (deploy-local.ps1 -Rebuild) to force."
        return
    }
}
Remove-Item -LiteralPath $taskStampPath -ErrorAction SilentlyContinue

$taskDotnet = Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) {
    $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

Push-Location $PSScriptRoot
try {
    # The offline engine (hdw_native.dll). Skipped only when asked and already built.
    if (-not ($SkipNative -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'native/bin/hdw_native.dll')))) {
        & (Join-Path $PSScriptRoot 'native/build.ps1')
    }
    & $taskDotnet build HachidoriFfxiv.sln -c Release -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
    if ($Verify) {
        & $taskDotnet run --project tests/HachidoriFfxiv.Checks -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed.' }
    }
    # Package only this assembly's runtime dependencies. Old binaries from the
    # project rename must not be included just because they remain in bin.
    $taskDeps = Get-Content -LiteralPath (Join-Path $taskOutput 'HachidoriFfxiv.deps.json') -Raw | ConvertFrom-Json
    $taskRuntime = $taskDeps.targets.PSObject.Properties[$taskDeps.runtimeTarget.name].Value
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $taskOutput
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination $taskOutput
    $taskPackageFiles = @('HachidoriFfxiv.json', 'HachidoriFfxiv.deps.json', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'hdw_native.dll', 'hdw_native.json', 'WebView2Loader.dll')
    foreach ($taskLibrary in $taskRuntime.PSObject.Properties) {
        foreach ($taskAsset in $taskLibrary.Value.runtime.PSObject.Properties) {
            $taskPackageFiles += [IO.Path]::GetFileName($taskAsset.Name)
        }
    }
    $taskPackageFiles = @($taskPackageFiles | Sort-Object -Unique)
    foreach ($taskFile in $taskPackageFiles) {
        if (!(Test-Path -LiteralPath (Join-Path $taskOutput $taskFile) -PathType Leaf)) {
            throw "Missing package dependency: $taskFile"
        }
    }
    $taskPackagePath = Join-Path $taskOutput 'HachidoriFfxiv/latest.zip'
    $taskStream = [IO.File]::Open($taskPackagePath, [IO.FileMode]::Create)
    try {
        $taskZip = [IO.Compression.ZipArchive]::new($taskStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($taskFile in $taskPackageFiles) {
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip, (Join-Path $taskOutput $taskFile), $taskFile) | Out-Null
            }
        }
        finally { $taskZip.Dispose() }
    }
    finally { $taskStream.Dispose() }
    # Inputs hashed before the build: an edit made during it forces the next run.
    if ($Verify -and $taskInputStamp) {
        [ordered]@{
            inputs = $taskInputStamp
            verifiedAt = (Get-Date).ToString('o')
            outputs = Get-TaskOutputHashes
        } | ConvertTo-Json | Set-Content -LiteralPath $taskStampPath
    }
    Write-Host 'Plugin: HachidoriFfxiv/bin/x64/Release/HachidoriFfxiv.dll'
    Write-Host 'Package: HachidoriFfxiv/bin/x64/Release/HachidoriFfxiv/latest.zip'
}
finally {
    Pop-Location
}
