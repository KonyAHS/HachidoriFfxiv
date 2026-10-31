param([switch]$Verify)

$ErrorActionPreference = 'Stop'
$taskDotnet = Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) {
    $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

Push-Location $PSScriptRoot
try {
    & $taskDotnet build HachidoriFfxiv.sln -c Release -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
    if ($Verify) {
        & $taskDotnet run --project tests/HachidoriFfxiv.Checks -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed.' }
    }
    # Package only this assembly's runtime dependencies. Old binaries from the
    # project rename must not be included just because they remain in bin.
    $taskOutput = Join-Path $PSScriptRoot 'HachidoriFfxiv/bin/x64/Release'
    $taskDeps = Get-Content -LiteralPath (Join-Path $taskOutput 'HachidoriFfxiv.deps.json') -Raw | ConvertFrom-Json
    $taskRuntime = $taskDeps.targets.PSObject.Properties[$taskDeps.runtimeTarget.name].Value
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $taskOutput
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination $taskOutput
    $taskPackageFiles = @('HachidoriFfxiv.json', 'HachidoriFfxiv.deps.json', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')
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
    Write-Host 'Plugin: HachidoriFfxiv/bin/x64/Release/HachidoriFfxiv.dll'
    Write-Host 'Package: HachidoriFfxiv/bin/x64/Release/HachidoriFfxiv/latest.zip'
}
finally {
    Pop-Location
}
